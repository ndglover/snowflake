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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AdbcDrivers.Snowflake.Native.Configuration;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.ConnectionPool;

/// <summary>
/// Represents a single connection pool for a specific configuration.
/// </summary>
internal class ConnectionPoolEntry(ConnectionConfig config)
{
    /// <summary>
    /// Gets the connection configuration for this pool.
    /// </summary>
    public ConnectionConfig Config { get; } = config;

    /// <summary>
    /// Gets the dictionary of active connections currently in use.
    /// </summary>
    public ConcurrentDictionary<string, IPooledConnection> ActiveConnections { get; } = new();

    /// <summary>
    /// Gets the stack of idle connections available for reuse.
    /// </summary>
    public ConcurrentStack<IPooledConnection> IdleConnections { get; } = new();

    /// <summary>
    /// Gets the semaphore that enforces the maximum pool size.
    /// </summary>
    public SemaphoreSlim CapacitySemaphore { get; } = new(
        config.PoolConfig.MaxPoolSize,
        config.PoolConfig.MaxPoolSize);

    /// <summary>
    /// Lock object for synchronizing access to idle connections.
    /// </summary>
    public readonly object IdleLock = new();

    private int _pendingRequests;

    /// <summary>
    /// Gets the number of threads currently waiting for a connection.
    /// </summary>
    public int PendingRequests => _pendingRequests;

    /// <summary>
    /// Increments the pending requests counter.
    /// </summary>
    public void IncrementPendingRequests() => Interlocked.Increment(ref _pendingRequests);

    /// <summary>
    /// Decrements the pending requests counter.
    /// </summary>
    public void DecrementPendingRequests() => Interlocked.Decrement(ref _pendingRequests);

    /// <summary>
    /// Gets all connections in the pool (both active and idle).
    /// </summary>
    /// <returns>An enumerable of all connections.</returns>
    public IEnumerable<IPooledConnection> GetAllConnections()
    {
        foreach (var connection in ActiveConnections.Values)
            yield return connection;
        foreach (var connection in IdleConnections.ToArray())
            yield return connection;
    }
}
