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
using System.Net.Http;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;
using Microsoft.Extensions.Logging;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
using AdbcDrivers.Snowflake.Native.Services.ConnectionPool;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native;

/// <summary>
/// Snowflake database implementation for ADBC.
/// </summary>
public sealed class SnowflakeDatabase : AdbcDatabase
{
    private readonly IReadOnlyDictionary<string, string>? _parameters;
    private readonly IConnectionPoolManager _connectionPool;
    private readonly HttpClient _httpClient;
    private readonly ILoggerFactory? _loggerFactory;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnowflakeDatabase"/> class.
    /// </summary>
    /// <param name="parameters">The ADBC connection parameters.</param>
    /// <param name="httpClient">Custom HttpClient support</param>
    /// <param name="loggerFactory">Custom ILoggerFactory</param>
    public SnowflakeDatabase(IReadOnlyDictionary<string, string>? parameters = null, HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null)
    {
        _parameters = parameters;
        _loggerFactory = loggerFactory;
        _httpClient = httpClient ?? CreateHttpClient(ParseNetworkFromParameters(parameters));

        var loginClient = new SnowflakeLoginClient(_httpClient);
        var basicAuth = new BasicAuthenticator(loginClient);
        var keyPairAuth = new KeyPairAuthenticator(loginClient);
        var oauthAuth = new OAuthAuthenticator(loginClient, _httpClient);
        var ssoAuth = new SsoAuthenticator(loginClient, _httpClient);

        var authService = new AuthenticationService(basicAuth, keyPairAuth, oauthAuth, ssoAuth);
        _connectionPool = new ConnectionPoolManager(authService);
    }

    private static NetworkConfig ParseNetworkFromParameters(IReadOnlyDictionary<string, string>? parameters)
    {
        var network = new NetworkConfig();
        if (parameters == null) return network;

        if (parameters.TryGetValue("adbc.snowflake.sql.uri.host", out var host) && !string.IsNullOrWhiteSpace(host))
            network.Host = host;

        if (parameters.TryGetValue("adbc.snowflake.sql.uri.port", out var portStr) && int.TryParse(portStr, out var port))
            network.Port = port;

        if (parameters.TryGetValue("adbc.snowflake.sql.uri.protocol", out var protocol) && !string.IsNullOrWhiteSpace(protocol))
            network.Protocol = protocol;

        if (parameters.TryGetValue("adbc.snowflake.sql.client_option.no_proxy", out var noProxy) &&
            string.Equals(noProxy, "true", StringComparison.OrdinalIgnoreCase))
            network.NoProxy = true;

        if (parameters.TryGetValue("adbc.snowflake.sql.ssl_skip_verify", out var sslStr) &&
            string.Equals(sslStr, "true", StringComparison.OrdinalIgnoreCase))
            network.SslSkipVerify = true;

        return network;
    }

    private static HttpClient CreateHttpClient(NetworkConfig network)
    {
        var handler = new HttpClientHandler();

        // Allow enough concurrent connections for parallel chunk prefetching. The
        // net8.0 default is effectively unbounded, but set it explicitly so the
        // result-chunk downloaders aren't throttled (and to be safe under any host).
        handler.MaxConnectionsPerServer = 32;

        if (network.SslSkipVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        if (network.NoProxy)
        {
            handler.UseProxy = false;
        }

        return new HttpClient(handler);
    }

    /// <summary>
    /// Creates a new connection to the Snowflake database.
    /// </summary>
    /// <param name="parameters">Connection-specific parameters that override database parameters.</param>
    /// <returns>An AdbcConnection instance.</returns>
    public override AdbcConnection Connect(IReadOnlyDictionary<string, string>? parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ConnectAsync(parameters).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously create a new connection to the Snowflake database.
    /// </summary>
    public async Task<AdbcConnection> ConnectAsync(IReadOnlyDictionary<string, string>? parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var config = ConnectionStringParser.ParseParameters(parameters, _parameters);
        return await SnowflakeConnection.CreateAsync(config, _httpClient, _connectionPool, _loggerFactory).ConfigureAwait(false);
    }


    /// <summary>
    /// Disposes the database and releases any resources.
    /// </summary>
    public override void Dispose()
    {
        if (!_disposed)
        {
            _connectionPool?.Dispose();
            _disposed = true;
        }
        base.Dispose();
    }
}
