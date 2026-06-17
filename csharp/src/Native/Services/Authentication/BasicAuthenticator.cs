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
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;

namespace AdbcDrivers.Snowflake.Native.Services.Authentication;

/// <summary>
/// Implements basic username/password authentication for Snowflake.
/// </summary>
internal class BasicAuthenticator : IBasicAuthenticator
{
    private readonly SnowflakeLoginClient _loginClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasicAuthenticator"/> class.
    /// </summary>
    /// <param name="loginClient">The shared login client.</param>
    public BasicAuthenticator(SnowflakeLoginClient loginClient)
    {
        _loginClient = loginClient ?? throw new ArgumentNullException(nameof(loginClient));
    }

    /// <inheritdoc/>
    public Task<AuthenticationToken> AuthenticateAsync(
        string account,
        string user,
        string password,
        CancellationToken cancellationToken = default)
    {
        return AuthenticateAsync(account, user, password, null, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AuthenticationToken> AuthenticateAsync(
        string account,
        string user,
        string password,
        ConnectionConfig? config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(account))
            throw new ArgumentException("Account cannot be null or empty.", nameof(account));

        if (string.IsNullOrEmpty(user))
            throw new ArgumentException("User cannot be null or empty.", nameof(user));

        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));

        var authData = new LoginRequestData
        {
            AUTHENTICATOR = "snowflake",
            LOGIN_NAME = user,
            PASSWORD = password
        };

        return await _loginClient.LoginAsync(account, authData, config, cancellationToken);
    }
}
