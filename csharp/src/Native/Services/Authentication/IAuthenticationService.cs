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

using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Authentication;

/// <summary>
/// Provides authentication services for Snowflake connections.
/// </summary>
internal interface IAuthenticationService
{
    /// <summary>
    /// Authenticates using the provided configuration and returns an authentication token.
    /// </summary>
    /// <param name="account">The Snowflake account identifier.</param>
    /// <param name="user">The username.</param>
    /// <param name="authConfig">The authentication configuration.</param>
    /// <param name="connectionConfig">The connection configuration containing warehouse, database, schema, and role settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An authentication token.</returns>
    Task<AuthenticationToken> AuthenticateAsync(string account, string user, AuthenticationConfig authConfig, ConnectionConfig? connectionConfig = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes an existing authentication token.
    /// </summary>
    /// <param name="token">The token to refresh.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A refreshed authentication token.</returns>
    Task<AuthenticationToken> RefreshTokenAsync(AuthenticationToken token, CancellationToken cancellationToken = default);
}
