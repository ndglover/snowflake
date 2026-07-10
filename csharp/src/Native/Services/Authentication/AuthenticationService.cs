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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;

using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Authentication;

/// <summary>
/// Provides authentication services for Snowflake connections.
/// </summary>
internal class AuthenticationService : IAuthenticationService
{
    private readonly IBasicAuthenticator _basicAuth;
    private readonly IKeyPairAuthenticator _keyPairAuth;
    private readonly IOAuthAuthenticator _oauthAuth;
    private readonly ISsoAuthenticator _ssoAuth;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationService"/> class.
    /// </summary>
    /// <param name="basicAuth">The basic authenticator.</param>
    /// <param name="keyPairAuth">The key pair authenticator.</param>
    /// <param name="oauthAuth">The OAuth authenticator.</param>
    /// <param name="ssoAuth">The SSO authenticator.</param>
    public AuthenticationService(
        IBasicAuthenticator basicAuth,
        IKeyPairAuthenticator keyPairAuth,
        IOAuthAuthenticator oauthAuth,
        ISsoAuthenticator ssoAuth)
    {
        _basicAuth = basicAuth ?? throw new ArgumentNullException(nameof(basicAuth));
        _keyPairAuth = keyPairAuth ?? throw new ArgumentNullException(nameof(keyPairAuth));
        _oauthAuth = oauthAuth ?? throw new ArgumentNullException(nameof(oauthAuth));
        _ssoAuth = ssoAuth ?? throw new ArgumentNullException(nameof(ssoAuth));
    }

    /// <inheritdoc/>
    public async Task<AuthenticationToken> AuthenticateAsync(
        string account,
        string user,
        AuthenticationConfig authConfig,
        ConnectionConfig? connectionConfig = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(account))
            throw new ArgumentException("Account cannot be null or empty.", nameof(account));

        if (authConfig == null)
            throw new ArgumentNullException(nameof(authConfig));

        // Validate configuration
        var validationResults = authConfig.Validate();
        if (validationResults.Any())
        {
            var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
            throw new ArgumentException($"Invalid authentication configuration: {errors}", nameof(authConfig));
        }

        // Route to appropriate authenticator with type-specific parameter validation
        return authConfig.Type switch
        {
            AuthenticationType.UsernamePassword => await AuthenticateWithPassword(account, user, authConfig, connectionConfig, cancellationToken).ConfigureAwait(false),
            AuthenticationType.KeyPair => await AuthenticateWithKeyPair(account, user, authConfig, cancellationToken).ConfigureAwait(false),
            AuthenticationType.OAuth => await _oauthAuth.AuthenticateAsync(account, authConfig.OAuthToken!, connectionConfig, cancellationToken).ConfigureAwait(false),
            AuthenticationType.Sso or AuthenticationType.ExternalBrowser => await AuthenticateWithSso(account, user, authConfig, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Authentication type {authConfig.Type} is not supported.")
        };
    }

    private async Task<AuthenticationToken> AuthenticateWithPassword(
        string account, string user, AuthenticationConfig authConfig,
        ConnectionConfig? connectionConfig, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("User is required for username/password authentication.", nameof(user));

        return await _basicAuth.AuthenticateAsync(account, user, authConfig.Password!, connectionConfig, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationToken> AuthenticateWithKeyPair(
        string account, string user, AuthenticationConfig authConfig,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("User is required for key pair authentication.", nameof(user));

        // The authenticator takes the key material itself; a configured file path is resolved
        // here so an inline key (jwt_private_key_pkcs8_value) is never treated as a path.
        string privateKeyPem;
        if (!string.IsNullOrEmpty(authConfig.PrivateKeyPath))
        {
            if (!File.Exists(authConfig.PrivateKeyPath))
                throw new AdbcException($"Private key file not found: {authConfig.PrivateKeyPath}");
            privateKeyPem = await File.ReadAllTextAsync(authConfig.PrivateKeyPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            privateKeyPem = authConfig.PrivateKey!;
        }

        return await _keyPairAuth.AuthenticateAsync(account, user, privateKeyPem, authConfig.PrivateKeyPassphrase, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthenticationToken> AuthenticateWithSso(
        string account, string user, AuthenticationConfig authConfig,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("User is required for SSO authentication.", nameof(user));

        return await _ssoAuth.AuthenticateAsync(account, user, authConfig.SsoProperties, cancellationToken).ConfigureAwait(false);
    }
}
