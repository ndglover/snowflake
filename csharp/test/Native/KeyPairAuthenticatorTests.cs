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
using AdbcDrivers.Snowflake.Native.Configuration;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
using Apache.Arrow.Adbc;
using NSubstitute;
using Xunit;

namespace AdbcDrivers.Snowflake.Native.Tests;

/// <summary>
/// Offline tests for key-pair (SNOWFLAKE_JWT) authentication: the login JWT's claims and
/// signature (verifiable without a server by checking against the key that signed it), and
/// the AuthenticationService wiring that resolves an inline PEM vs. a private-key file path.
/// </summary>
[Trait("Category", "Unit")]
public class KeyPairAuthenticatorTests
{
    private const string Account = "testaccount";
    private const string User = "testuser";

    private static (RSA Rsa, string Pem) CreateKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa, rsa.ExportPkcs8PrivateKeyPem());
    }

    private static JsonDocument DecodeSegment(string jwt, int segment)
    {
        string part = jwt.Split('.')[segment];
        return JsonDocument.Parse(FromBase64Url(part));
    }

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    [Fact]
    public void GenerateJwtToken_HasSnowflakeClaimShapes()
    {
        (RSA rsa, string pem) = CreateKey();
        using (rsa)
        {
            string jwt = KeyPairAuthenticator.GenerateJwtToken(Account, User, pem, passphrase: null);

            using JsonDocument header = DecodeSegment(jwt, 0);
            Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
            Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());

            // Snowflake requires iss = ACCOUNT.USER.SHA256:<base64 fingerprint of the public key>
            // and sub = ACCOUNT.USER, both upper-cased.
            string fingerprint = Convert.ToBase64String(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
            using JsonDocument payload = DecodeSegment(jwt, 1);
            Assert.Equal($"TESTACCOUNT.TESTUSER.SHA256:{fingerprint}", payload.RootElement.GetProperty("iss").GetString());
            Assert.Equal("TESTACCOUNT.TESTUSER", payload.RootElement.GetProperty("sub").GetString());
            Assert.Equal(3600, payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64());
        }
    }

    [Fact]
    public void GenerateJwtToken_SignatureVerifiesWithPublicKey()
    {
        (RSA rsa, string pem) = CreateKey();
        using (rsa)
        {
            string jwt = KeyPairAuthenticator.GenerateJwtToken(Account, User, pem, passphrase: null);
            string[] parts = jwt.Split('.');
            Assert.Equal(3, parts.Length);

            bool valid = rsa.VerifyData(
                Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
                FromBase64Url(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            Assert.True(valid);
        }
    }

    [Fact]
    public void GenerateJwtToken_AccountWithRegionSuffix_UsesBareAccountLocator()
    {
        // Account identifiers can carry a region/cloud suffix (xy12345.eu-west-1); the JWT
        // claims must use only the bare locator, like gosnowflake/connector-net.
        (RSA rsa, string pem) = CreateKey();
        using (rsa)
        {
            string jwt = KeyPairAuthenticator.GenerateJwtToken("xy12345.eu-west-1", User, pem, passphrase: null);

            using JsonDocument payload = DecodeSegment(jwt, 1);
            Assert.Equal("XY12345.TESTUSER", payload.RootElement.GetProperty("sub").GetString());
            Assert.StartsWith("XY12345.TESTUSER.SHA256:", payload.RootElement.GetProperty("iss").GetString());
        }
    }

    [Fact]
    public void GenerateJwtToken_EncryptedKey_DecryptsWithPassphrase()
    {
        (RSA rsa, string _) = CreateKey();
        using (rsa)
        {
            string encryptedPem = rsa.ExportEncryptedPkcs8PrivateKeyPem(
                "key-passphrase",
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

            string jwt = KeyPairAuthenticator.GenerateJwtToken(Account, User, encryptedPem, "key-passphrase");
            Assert.Equal(3, jwt.Split('.').Length);

            Assert.Throws<AdbcException>(() =>
                KeyPairAuthenticator.GenerateJwtToken(Account, User, encryptedPem, "wrong-passphrase"));
        }
    }

    [Fact]
    public void GenerateJwtToken_InvalidPem_ThrowsAdbcException()
    {
        var ex = Assert.Throws<AdbcException>(() =>
            KeyPairAuthenticator.GenerateJwtToken(Account, User, "not a pem key", passphrase: null));
        Assert.Contains("private key", ex.Message);
    }

    // ---- AuthenticationService wiring: inline PEM vs. private-key file path ----

    private static (AuthenticationService Service, IKeyPairAuthenticator KeyPairAuth) CreateService()
    {
        var keyPairAuth = Substitute.For<IKeyPairAuthenticator>();
        var service = new AuthenticationService(
            Substitute.For<IBasicAuthenticator>(),
            keyPairAuth,
            Substitute.For<IOAuthAuthenticator>(),
            Substitute.For<ISsoAuthenticator>());
        return (service, keyPairAuth);
    }

    [Fact]
    public async Task AuthenticateAsync_InlineKey_PassesPemThroughUnchanged()
    {
        (AuthenticationService service, IKeyPairAuthenticator keyPairAuth) = CreateService();
        (RSA rsa, string pem) = CreateKey();
        using (rsa)
        {
            var authConfig = new AuthenticationConfig { Type = AuthenticationType.KeyPair, PrivateKey = pem };

            await service.AuthenticateAsync(Account, User, authConfig);

            await keyPairAuth.Received(1).AuthenticateAsync(Account, User, pem, null, Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task AuthenticateAsync_KeyFilePath_ReadsFileAndPassesContent()
    {
        (AuthenticationService service, IKeyPairAuthenticator keyPairAuth) = CreateService();
        (RSA rsa, string pem) = CreateKey();
        using (rsa)
        {
            string keyFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(keyFile, pem);
                var authConfig = new AuthenticationConfig { Type = AuthenticationType.KeyPair, PrivateKeyPath = keyFile };

                await service.AuthenticateAsync(Account, User, authConfig);

                // The file's CONTENT reaches the authenticator, never the path.
                await keyPairAuth.Received(1).AuthenticateAsync(Account, User, pem, null, Arg.Any<CancellationToken>());
            }
            finally
            {
                File.Delete(keyFile);
            }
        }
    }

    [Fact]
    public async Task AuthenticateAsync_MissingKeyFile_ThrowsAdbcException()
    {
        (AuthenticationService service, _) = CreateService();
        var authConfig = new AuthenticationConfig
        {
            Type = AuthenticationType.KeyPair,
            PrivateKeyPath = Path.Combine(Path.GetTempPath(), "does-not-exist.p8"),
        };

        var ex = await Assert.ThrowsAsync<AdbcException>(() => service.AuthenticateAsync(Account, User, authConfig));
        Assert.Contains("not found", ex.Message);
    }
}
