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

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AdbcDrivers.Snowflake.Native.Configuration;

/// <summary>
/// Represents authentication configuration for Snowflake connections.
/// </summary>
internal class AuthenticationConfig
{
    /// <summary>
    /// Gets or sets the authentication type.
    /// </summary>
    public AuthenticationType Type { get; set; } = AuthenticationType.UsernamePassword;

    /// <summary>
    /// Gets or sets the password for basic authentication.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the path to the RSA private key file.
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Gets or sets the RSA private key value in PKCS8 format (inline, not from file).
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// Gets or sets the passphrase for encrypted private keys.
    /// </summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>
    /// Gets or sets the OAuth access token.
    /// </summary>
    public string? OAuthToken { get; set; }

    /// <summary>
    /// Gets or sets the OAuth refresh token.
    /// </summary>
    public string? OAuthRefreshToken { get; set; }

    /// <summary>
    /// Gets or sets additional SSO properties.
    /// </summary>
    public Dictionary<string, string> SsoProperties { get; set; } = new();

    /// <summary>
    /// Validates the authentication configuration based on the selected type.
    /// </summary>
    /// <returns>A collection of validation results.</returns>
    public IEnumerable<ValidationResult> Validate()
    {
        var results = new List<ValidationResult>();

        switch (Type)
        {
            case AuthenticationType.UsernamePassword:
                if (string.IsNullOrEmpty(Password))
                {
                    results.Add(new ValidationResult(
                        "Password is required for username/password authentication.",
                        [nameof(Password)]));
                }
                break;

            case AuthenticationType.KeyPair:
                if (string.IsNullOrEmpty(PrivateKeyPath) && string.IsNullOrEmpty(PrivateKey))
                {
                    results.Add(new ValidationResult(
                        "Private key path or private key value is required for key pair authentication.",
                        [nameof(PrivateKeyPath), nameof(PrivateKey)]));
                }
                break;

            case AuthenticationType.OAuth:
                if (string.IsNullOrEmpty(OAuthToken))
                {
                    results.Add(new ValidationResult(
                        "OAuth token is required for OAuth authentication.",
                        [nameof(OAuthToken)]));
                }
                break;

            case AuthenticationType.Sso:
                // SSO validation can be extended based on specific requirements
                break;

            case AuthenticationType.ExternalBrowser:
                // External browser authentication doesn't require additional validation
                break;
        }

        return results;
    }
}

/// <summary>
/// Represents the available authentication types for Snowflake.
/// </summary>
internal enum AuthenticationType
{
    /// <summary>
    /// Username and password authentication.
    /// </summary>
    UsernamePassword,

    /// <summary>
    /// RSA key pair authentication.
    /// </summary>
    KeyPair,

    /// <summary>
    /// OAuth 2.0 token authentication.
    /// </summary>
    OAuth,

    /// <summary>
    /// Single Sign-On authentication.
    /// </summary>
    Sso,

    /// <summary>
    /// External browser authentication.
    /// </summary>
    ExternalBrowser
}
