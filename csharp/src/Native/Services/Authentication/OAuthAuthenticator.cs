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
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;

using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Authentication;

/// <summary>
/// Implements OAuth 2.0 authentication for Snowflake.
/// </summary>
internal class OAuthAuthenticator : IOAuthAuthenticator
{
    private readonly SnowflakeLoginClient _loginClient;
    private readonly HttpClient _httpClient;
    private const string TokenEndpoint = "/oauth/token-request";

    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthAuthenticator"/> class.
    /// </summary>
    /// <param name="loginClient">The shared login client.</param>
    /// <param name="httpClient">The HTTP client for token refresh requests.</param>
    public OAuthAuthenticator(SnowflakeLoginClient loginClient, HttpClient httpClient)
    {
        _loginClient = loginClient ?? throw new ArgumentNullException(nameof(loginClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc/>
    public async Task<AuthenticationToken> AuthenticateAsync(
        string account,
        string oauthToken,
        ConnectionConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(account))
            throw new ArgumentException("Account cannot be null or empty.", nameof(account));

        if (string.IsNullOrEmpty(oauthToken))
            throw new ArgumentException("OAuth token cannot be null or empty.", nameof(oauthToken));

        var authData = new LoginRequestData
        {
            AUTHENTICATOR = "OAUTH",
            TOKEN = oauthToken
        };

        var result = await _loginClient.LoginAsync(account, authData, config, cancellationToken);
        result.TokenType = "Bearer";
        return result;
    }

    /// <inheritdoc/>
    public async Task<AuthenticationToken> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
            throw new ArgumentException("Refresh token cannot be null or empty.", nameof(refreshToken));

        var tokenRequest = new
        {
            grant_type = "refresh_token",
            refresh_token = refreshToken
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(TokenEndpoint, tokenRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

            if (responseContent == null)
                throw new AdbcException("Invalid response from OAuth token refresh.");

            return new AuthenticationToken
            {
                AccessToken = responseContent.AccessToken ?? throw new AdbcException("No access token received."),
                RefreshToken = responseContent.RefreshToken ?? refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(responseContent.ExpiresIn),
                TokenType = responseContent.TokenType ?? "Bearer"
            };
        }
        catch (HttpRequestException ex)
        {
            throw new AdbcException($"Failed to refresh OAuth token: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new AdbcException($"Failed to parse OAuth token refresh response: {ex.Message}", ex);
        }
    }

    private class TokenResponse
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? TokenType { get; set; }
        public int ExpiresIn { get; set; } = 3600;
    }
}
