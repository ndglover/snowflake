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
using System.Threading;
using System.Threading.Tasks;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Authentication;

/// <summary>
/// Provides Single Sign-On (SSO) authentication for Snowflake.
/// </summary>
internal interface ISsoAuthenticator
{
    /// <summary>
    /// Authenticates using SSO with external browser.
    /// </summary>
    /// <param name="account">The Snowflake account identifier.</param>
    /// <param name="user">The username.</param>
    /// <param name="ssoProperties">Additional SSO properties.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An authentication token.</returns>
    Task<AuthenticationToken> AuthenticateAsync(
        string account,
        string user,
        Dictionary<string, string>? ssoProperties = null,
        CancellationToken cancellationToken = default);
}
