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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Authentication;

/// <summary>
/// Implements RSA key pair authentication for Snowflake.
/// </summary>
internal class KeyPairAuthenticator : IKeyPairAuthenticator
{
    private readonly SnowflakeLoginClient _loginClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyPairAuthenticator"/> class.
    /// </summary>
    /// <param name="loginClient">The shared login client.</param>
    public KeyPairAuthenticator(SnowflakeLoginClient loginClient)
    {
        _loginClient = loginClient ?? throw new ArgumentNullException(nameof(loginClient));
    }

    /// <inheritdoc/>
    public async Task<AuthenticationToken> AuthenticateAsync(
        string account,
        string user,
        string privateKeyPath,
        string? privateKeyPassphrase = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(account))
            throw new ArgumentException("Account cannot be null or empty.", nameof(account));

        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("User cannot be null or empty.", nameof(user));

        if (string.IsNullOrEmpty(privateKeyPath))
            throw new ArgumentException("Private key path cannot be null or empty.", nameof(privateKeyPath));

        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException($"Private key file not found: {privateKeyPath}");

        var privateKeyPem = await File.ReadAllTextAsync(privateKeyPath, cancellationToken);
        var jwtToken = GenerateJwtToken(account, user, privateKeyPem, privateKeyPassphrase);

        var authData = new LoginRequestData
        {
            AUTHENTICATOR = "SNOWFLAKE_JWT",
            LOGIN_NAME = user,
            TOKEN = jwtToken
        };

        return await _loginClient.LoginAsync(account, authData, null, cancellationToken);
    }

    private static string GenerateJwtToken(string account, string user, string privateKeyPem, string? passphrase)
    {
        try
        {
            using var rsa = RSA.Create();

            if (!string.IsNullOrEmpty(passphrase))
            {
                rsa.ImportFromEncryptedPem(privateKeyPem, passphrase);
            }
            else
            {
                rsa.ImportFromPem(privateKeyPem);
            }

            var publicKey = rsa.ExportSubjectPublicKeyInfo();
            var publicKeyFingerprint = Convert.ToBase64String(SHA256.HashData(publicKey));

            var header = new
            {
                alg = "RS256",
                typ = "JWT"
            };

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new
            {
                iss = $"{account.ToUpperInvariant()}.{user.ToUpperInvariant()}.{publicKeyFingerprint}",
                sub = $"{account.ToUpperInvariant()}.{user.ToUpperInvariant()}",
                iat = now,
                exp = now + 3600
            };

            var headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header)));
            var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

            var signatureInput = $"{headerBase64}.{payloadBase64}";
            var signatureBytes = rsa.SignData(
                Encoding.UTF8.GetBytes(signatureInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            return $"{signatureInput}.{Base64UrlEncode(signatureBytes)}";
        }
        catch (CryptographicException ex)
        {
            throw new AdbcException($"Failed to process private key: {ex.Message}", ex);
        }
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
