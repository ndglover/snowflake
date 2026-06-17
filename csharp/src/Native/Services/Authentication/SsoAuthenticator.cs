/*
* Copyright (c) 2025 ADBC Drivers Contributors
*
* This file has been modified from its original version, which is
* under the Apache License:
*
* Licensed to the Apache Software Foundation (ASF) under one
* or more contributor license agreements.  See the NOTICE file
* distributed with this work for additional information
* regarding copyright ownership.  The ASF licenses this file
* to you under the Apache License, Version 2.0 (the
* "License"); you may not use this file except in compliance
* with the License.  You may obtain a copy of the License at
*
*    http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Authentication;

/// <summary>
/// Implements Single Sign-On (SSO) authentication for Snowflake using external browser.
/// </summary>
internal class SsoAuthenticator : ISsoAuthenticator
{
    private readonly SnowflakeLoginClient _loginClient;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="SsoAuthenticator"/> class.
    /// </summary>
    /// <param name="loginClient">The shared login client.</param>
    /// <param name="httpClient">The HTTP client for the authenticator-request endpoint.</param>
    public SsoAuthenticator(SnowflakeLoginClient loginClient, HttpClient httpClient)
    {
        _loginClient = loginClient ?? throw new ArgumentNullException(nameof(loginClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc/>
    public async Task<AuthenticationToken> AuthenticateAsync(
        string account,
        string user,
        Dictionary<string, string>? ssoProperties = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(account))
            throw new ArgumentException("Account cannot be null or empty.", nameof(account));

        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("User cannot be null or empty.", nameof(user));

        // Step 1: Get SSO URL from Snowflake
        var ssoUrl = await GetSsoUrlAsync(account, user, cancellationToken);

        // Step 2: Open browser for user authentication
        var samlResponse = await AuthenticateWithBrowserAsync(ssoUrl, cancellationToken);

        // Step 3: Complete authentication with SAML response
        var authData = new LoginRequestData
        {
            AUTHENTICATOR = "EXTERNALBROWSER",
            LOGIN_NAME = user,
            RAW_SAML_RESPONSE = samlResponse
        };

        return await _loginClient.LoginAsync(account, authData, null, cancellationToken);
    }

    private async Task<string> GetSsoUrlAsync(
        string account,
        string user,
        CancellationToken cancellationToken)
    {
        var authenticatorUrl = _loginClient.BuildUrl(account, SnowflakeLoginClient.AuthenticatorEndpoint);
        var authenticatorRequest = new LoginRequestBody
        {
            Data = new LoginRequestData
            {
                ACCOUNT_NAME = account,
                LOGIN_NAME = user,
                AUTHENTICATOR = "EXTERNALBROWSER",
                CLIENT_APP_ID = ".NET",
                CLIENT_APP_VERSION = "3.1.0",
                CLIENT_ENVIRONMENT = ClientEnvironment.Create(),
                SESSION_PARAMETERS = new Dictionary<string, object>
                {
                    { "DOTNET_QUERY_RESULT_FORMAT", "ARROW" }
                }
            }
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(authenticatorUrl, authenticatorRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadFromJsonAsync<AuthenticatorResponse>(cancellationToken);

            if (responseContent?.Data?.SsoUrl == null)
                throw new AdbcException("Failed to retrieve SSO URL from Snowflake.");

            return responseContent.Data.SsoUrl;
        }
        catch (HttpRequestException ex)
        {
            throw new AdbcException($"Failed to get SSO URL from Snowflake: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new AdbcException($"Failed to parse SSO URL response: {ex.Message}", ex);
        }
    }

    private static async Task<string> AuthenticateWithBrowserAsync(string ssoUrl, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        var callbackPort = 8080;
        var callbackUrl = $"http://localhost:{callbackPort}/";
        listener.Prefixes.Add(callbackUrl);

        try
        {
            listener.Start();
            OpenBrowser(ssoUrl);

            var context = await listener.GetContextAsync();
            var samlResponse = context.Request.QueryString["SAMLResponse"];

            if (string.IsNullOrEmpty(samlResponse))
                throw new AdbcException("No SAML response received from SSO authentication.");

            var responseBytes = System.Text.Encoding.UTF8.GetBytes(
                "<html><body><h1>Authentication Successful</h1><p>You can close this window.</p></body></html>");
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, cancellationToken);
            context.Response.Close();

            return samlResponse;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            throw new AdbcException($"Failed to open browser for SSO authentication: {ex.Message}", ex);
        }
    }
}
