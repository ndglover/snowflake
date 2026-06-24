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
using AdbcDrivers.Snowflake.Native.Configuration;
using Xunit;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Tests.Configuration;

[Trait("Category", "Unit")]
public class ConnectionStringParserTests
{
    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pairs = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                parameters[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return parameters;
    }

    [Fact]
    public void Parse_WithValidBasicConnectionString_ShouldReturnValidConfig()
    {
        // Arrange
        var parameters = ParseConnectionString("adbc.snowflake.sql.account=testaccount;username=testuser;password=testpass;adbc.snowflake.sql.db=testdb");

        // Act
        var config = ConnectionStringParser.ParseParameters(parameters);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("testaccount", config.Account);
        Assert.Equal("testuser", config.User);
        Assert.Equal("testdb", config.Database);
        Assert.Equal(AuthenticationType.UsernamePassword, config.Authentication.Type);
        Assert.Equal("testpass", config.Authentication.Password);
    }

    [Fact]
    public void Parse_WithKeyPairAuthentication_ShouldReturnValidConfig()
    {
        // Arrange
        var parameters = ParseConnectionString("adbc.snowflake.sql.account=testaccount;username=testuser;adbc.snowflake.sql.auth_type=jwt;adbc.snowflake.sql.client_option.jwt_private_key_pkcs8_value=PRIVATE_KEY_CONTENT");

        // Act
        var config = ConnectionStringParser.ParseParameters(parameters);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("testaccount", config.Account);
        Assert.Equal("testuser", config.User);
        Assert.Equal(AuthenticationType.KeyPair, config.Authentication.Type);
        Assert.Equal("PRIVATE_KEY_CONTENT", config.Authentication.PrivateKey);
    }

    [Fact]
    public void Parse_WithOAuthAuthentication_ShouldReturnValidConfig()
    {
        // Arrange
        var parameters = ParseConnectionString("adbc.snowflake.sql.account=testaccount;username=testuser;adbc.snowflake.sql.auth_type=oauth;adbc.snowflake.sql.client_option.auth_token=test_token");

        // Act
        var config = ConnectionStringParser.ParseParameters(parameters);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("testaccount", config.Account);
        Assert.Equal("testuser", config.User);
        Assert.Equal(AuthenticationType.OAuth, config.Authentication.Type);
        Assert.Equal("test_token", config.Authentication.OAuthToken);
    }

    [Fact]
    public void Parse_WithTimeoutSettings_ShouldReturnValidConfig()
    {
        // Arrange
        var parameters = ParseConnectionString("adbc.snowflake.sql.account=testaccount;username=testuser;password=testpass;connection_timeout=300");

        // Act
        var config = ConnectionStringParser.ParseParameters(parameters);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(TimeSpan.FromSeconds(300), config.QueryTimeout);
    }

    [Fact]
    public void Parse_WithPoolConfiguration_ShouldReturnValidConfig()
    {
        // Arrange
        var parameters = ParseConnectionString("adbc.snowflake.sql.account=testaccount;username=testuser;password=testpass;max_pool_size=20");

        // Act
        var config = ConnectionStringParser.ParseParameters(parameters);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(20, config.PoolConfig.MaxPoolSize);
    }

    [Fact]
    public void Parse_WithSsoProperties_ShouldReturnValidConfig()
    {
        // Arrange - SSO not currently supported in ADBC standard, removing this test
        var parameters = ParseConnectionString("adbc.snowflake.sql.account=testaccount;username=testuser;adbc.snowflake.sql.auth_type=externalbrowser");

        // Act
        var config = ConnectionStringParser.ParseParameters(parameters);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(AuthenticationType.ExternalBrowser, config.Authentication.Type);
    }

    [Fact]
    public void Parse_WithNullParameters_ShouldThrowArgumentException()
    {
        // Act & Assert - null parameters result in empty dictionary which fails validation
        var exception = Assert.Throws<ArgumentException>(() => ConnectionStringParser.ParseParameters(null));
        Assert.Contains("account", exception.Message);
    }

    [Fact]
    public void Parse_WithMissingRequiredParameter_ShouldThrowArgumentException()
    {
        // Arrange
        var parameters = ParseConnectionString("username=testuser;password=testpass"); // Missing account

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ConnectionStringParser.ParseParameters(parameters));
        Assert.Contains("account", exception.Message);
    }

    [Fact]
    public void Parse_WithInvalidAuthenticator_ShouldThrowArgumentException()
    {
        // Arrange
        var parameters = ParseConnectionString("adbc.snowflake.sql.account=testaccount;username=testuser;adbc.snowflake.sql.auth_type=invalid_auth");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ConnectionStringParser.ParseParameters(parameters));
        Assert.Contains("Unsupported auth_type", exception.Message);
    }

    [Fact]
    public void Parse_WithCaseInsensitiveParameters_ShouldReturnValidConfig()
    {
        // Arrange
        var parameters = ParseConnectionString("ADBC.SNOWFLAKE.SQL.ACCOUNT=testaccount;Username=testuser;PASSWORD=testpass;ADBC.SNOWFLAKE.SQL.DB=testdb");

        // Act
        var config = ConnectionStringParser.ParseParameters(parameters);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("testaccount", config.Account);
        Assert.Equal("testuser", config.User);
        Assert.Equal("testdb", config.Database);
        Assert.Equal("testpass", config.Authentication.Password);
    }

    [Fact]
    public void ParseParameters_WithConnectionOverrides_ShouldMergeCorrectly()
    {
        // Arrange - database parameters
        var databaseParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "adbc.snowflake.sql.account", "testaccount" },
            { "username", "testuser" },
            { "password", "testpass" },
            { "adbc.snowflake.sql.warehouse", "DEFAULT_WH" },
            { "adbc.snowflake.sql.db", "DEFAULT_DB" }
        };

        // Connection-specific overrides
        var connectionParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "adbc.snowflake.sql.warehouse", "ANALYTICS_WH" },  // Override
            { "adbc.snowflake.sql.schema", "PUBLIC" }  // New parameter
        };

        // Act
        var config = ConnectionStringParser.ParseParameters(connectionParams, databaseParams);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("testaccount", config.Account);
        Assert.Equal("testuser", config.User);
        Assert.Equal("ANALYTICS_WH", config.Warehouse); // Overridden value
        Assert.Equal("DEFAULT_DB", config.Database); // From database params
        Assert.Equal("PUBLIC", config.Schema); // From connection params
    }

    [Fact]
    public void ParseParameters_WithCaseInsensitiveMerge_ShouldHandleCorrectly()
    {
        // Arrange - test that case-insensitive merge works correctly
        var databaseParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ADBC.SNOWFLAKE.SQL.ACCOUNT", "testaccount" },
            { "Username", "testuser" },
            { "ADBC.SNOWFLAKE.SQL.WAREHOUSE", "DEFAULT_WH" }
        };

        var connectionParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "adbc.snowflake.sql.warehouse", "OVERRIDE_WH" },  // Different casing, should override
            { "password", "testpass" }
        };

        // Act
        var config = ConnectionStringParser.ParseParameters(connectionParams, databaseParams);

        // Assert
        Assert.NotNull(config);
        Assert.Equal("testaccount", config.Account);
        Assert.Equal("testuser", config.User);
        Assert.Equal("OVERRIDE_WH", config.Warehouse); // Should use connection override despite case difference
        Assert.Equal("testpass", config.Authentication.Password);
    }
}
