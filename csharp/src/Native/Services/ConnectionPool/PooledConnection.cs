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
using AdbcDrivers.Snowflake.Native.Services.Authentication;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.ConnectionPool;

/// <summary>
/// Represents a pooled Snowflake connection.
/// </summary>
internal class PooledConnection : IPooledConnection
{
    private DateTimeOffset _lastUsedAt;
    private bool _disposed;
    private readonly Func<Task>? _sessionCloser;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledConnection"/> class.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="authToken">The authentication token.</param>
    /// <param name="config">The connection configuration.</param>
    /// <param name="sessionCloser">
    /// Optional best-effort callback that closes the server-side Snowflake session; invoked when the
    /// pool discards this connection (not when it is returned to the idle pool for reuse).
    /// </param>
    public PooledConnection(
        string connectionId,
        AuthenticationToken authToken,
        ConnectionConfig config,
        Func<Task>? sessionCloser = null)
    {
        ConnectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        AuthToken = authToken ?? throw new ArgumentNullException(nameof(authToken));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        CreatedAt = DateTimeOffset.UtcNow;
        _lastUsedAt = CreatedAt;
        _sessionCloser = sessionCloser;
    }

    public string ConnectionId { get; }

    public AuthenticationToken AuthToken { get; }

    public ConnectionConfig Config { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastUsedAt => _lastUsedAt;

    public bool IsDisposed => _disposed;

    public bool IsTokenExpired => AuthToken.IsExpired;

    public bool IsFaulted { get; private set; }

    public void MarkFaulted() => IsFaulted = true;

    /// <summary>
    /// Updates the last used timestamp (internal use only).
    /// </summary>
    void IPooledConnection.UpdateLastUsedAt() => _lastUsedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Disposes the connection.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Best-effort: close the server-side session so it isn't orphaned until it times out.
        // Bounded so a slow/hung close can't stall teardown; a failure just lets the session expire.
        if (_sessionCloser != null)
        {
            try
            {
                _sessionCloser().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // ignore — closing the session is best-effort
            }
        }

        GC.SuppressFinalize(this);
    }
}
