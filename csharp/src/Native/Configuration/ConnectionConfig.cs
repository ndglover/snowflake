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
using System.ComponentModel.DataAnnotations;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Configuration;

/// <summary>
/// Represents connection configuration parameters for Snowflake ADBC driver.
/// </summary>
internal class ConnectionConfig
{
    /// <summary>
    /// Gets or sets the Snowflake account identifier.
    /// </summary>
    [Required]
    public string Account { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the username for authentication.
    /// Optional when using OAuth (user is derived from the token).
    /// </summary>
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default database name.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Gets or sets the default schema name.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Gets or sets the warehouse to use for query execution.
    /// </summary>
    public string? Warehouse { get; set; }

    /// <summary>
    /// Gets or sets the role to assume after connection.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Gets or sets the authentication configuration.
    /// </summary>
    [Required]
    public AuthenticationConfig Authentication { get; set; } = new();

    /// <summary>
    /// Gets or sets the connection pool configuration.
    /// </summary>
    public ConnectionPoolConfig PoolConfig { get; set; } = new();

    /// <summary>
    /// Gets or sets the query timeout.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether to enable compression for requests.
    /// </summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>
    /// Gets or sets the network transport configuration (host, proxy).
    /// </summary>
    public NetworkConfig Network { get; set; } = new();

    /// <summary>
    /// Gets or sets whether the driver keeps idle pooled sessions alive with a periodic heartbeat
    /// (Snowflake's <c>CLIENT_SESSION_KEEP_ALIVE</c>). Off by default; when on, an idle connection is
    /// pinged every <see cref="HeartbeatFrequency"/> so it does not lapse to master-token expiry.
    /// </summary>
    public bool ClientSessionKeepAlive { get; set; }

    /// <summary>
    /// Gets or sets how often an idle session is heartbeated when <see cref="ClientSessionKeepAlive"/>
    /// is enabled. Defaults to one hour (well under the ~4h master-token window); the parser clamps it
    /// to [15 minutes, 1 hour].
    /// </summary>
    public TimeSpan HeartbeatFrequency { get; set; } = TimeSpan.FromHours(1);
}
