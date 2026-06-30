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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;
using AdbcDrivers.Snowflake.Native.Services.Authentication;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.ConnectionPool;

/// <summary>
/// Implements connection pooling for Snowflake connections.
/// </summary>
internal class ConnectionPoolManager : IConnectionPoolManager, IDisposable
{
    private readonly IAuthenticationService _authService;
    private readonly Func<AuthenticationToken, ConnectionConfig, CancellationToken, Task>? _sessionCloser;
    private readonly ConcurrentDictionary<string, ConnectionPoolEntry> _pools;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _cleanupStartLock = new();
    private Task? _cleanupTask;
    private long _totalConnectionsCreated;
    private long _totalConnectionsClosed;
    private long _totalConnectionReuses;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionPoolManager"/> class.
    /// </summary>
    /// <param name="authService">The authentication service.</param>
    /// <param name="sessionCloser">
    /// Optional best-effort callback to close a connection's server-side session when the pool
    /// discards it (so sessions aren't orphaned until they time out).
    /// </param>
    public ConnectionPoolManager(
        IAuthenticationService authService,
        Func<AuthenticationToken, ConnectionConfig, CancellationToken, Task>? sessionCloser = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _sessionCloser = sessionCloser;
        _pools = new ConcurrentDictionary<string, ConnectionPoolEntry>();
    }

    /// <inheritdoc/>
    public async Task<IPooledConnection> AcquireConnectionAsync(
        ConnectionConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        EnsureCleanupStarted();

        var poolKey = GeneratePoolKey(config);
        var poolEntry = _pools.GetOrAdd(poolKey, static (_, cfg) => new ConnectionPoolEntry(cfg), config);

        if (TryAcquireIdleConnection(poolEntry, out var idleConnection))
            return idleConnection!;

        poolEntry.IncrementPendingRequests();
        try
        {
            await poolEntry.CapacitySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            poolEntry.DecrementPendingRequests();
        }

        try
        {
            if (TryAcquireIdleConnection(poolEntry, out var connection))
            {
                poolEntry.CapacitySemaphore.Release();
                return connection!;
            }

            var newConnection = await CreateConnectionAsync(config, cancellationToken);
            poolEntry.ActiveConnections.TryAdd(newConnection.ConnectionId, newConnection);
            Interlocked.Increment(ref _totalConnectionsCreated);
            return newConnection;
        }
        catch
        {
            poolEntry.CapacitySemaphore.Release();
            throw;
        }
    }

    private bool IsConnectionValid(IPooledConnection connection)
    {
        return !connection.IsDisposed && !connection.IsFaulted && !connection.IsTokenExpired &&
               (DateTimeOffset.UtcNow - connection.CreatedAt) <= connection.Config.PoolConfig.MaxConnectionLifetime;
    }

    private bool TryAcquireIdleConnection(ConnectionPoolEntry poolEntry, out IPooledConnection? idleConnection)
    {
        lock (poolEntry.IdleLock)
        {
            while (poolEntry.IdleConnections.TryPop(out var connection))
            {
                if (IsConnectionValid(connection))
                {
                    connection.UpdateLastUsedAt();
                    poolEntry.ActiveConnections.TryAdd(connection.ConnectionId, connection);
                    Interlocked.Increment(ref _totalConnectionReuses);
                    idleConnection = connection;
                    return true;
                }

                connection.Dispose();
                poolEntry.CapacitySemaphore.Release();
                Interlocked.Increment(ref _totalConnectionsClosed);
            }
        }

        idleConnection = null;
        return false;
    }

    public void ReleaseConnection(IPooledConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var poolKey = GeneratePoolKey(connection.Config);
        if (!_pools.TryGetValue(poolKey, out var poolEntry))
            return;

        poolEntry.ActiveConnections.TryRemove(connection.ConnectionId, out _);
        if (IsConnectionValid(connection))
            poolEntry.IdleConnections.Push(connection);
        else
        {
            connection.Dispose();
            poolEntry.CapacitySemaphore.Release();
            Interlocked.Increment(ref _totalConnectionsClosed);
        }
    }

    /// <summary>
    /// Disposes the connection pool and all pooled connections.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cts.Cancel();

        if (_cleanupTask != null)
        {
            try
            {
                _cleanupTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The cleanup loop observed cancellation while shutting down — expected.
            }
        }

        // Dispose the CTS only after the cleanup loop has stopped using its token.
        _cts.Dispose();

        foreach (var poolEntry in _pools.Values)
        {
            foreach (var connection in poolEntry.GetAllConnections())
            {
                connection.Dispose();
            }

            poolEntry.CapacitySemaphore.Dispose();
        }

        _pools.Clear();
    }

    private void EnsureCleanupStarted()
    {
        if (_cleanupTask != null) return;
        lock (_cleanupStartLock)
        {
            _cleanupTask ??= CleanupLoopAsync();
        }
    }

    private async Task CleanupLoopAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        // Capture the token once: re-reading _cts.Token each iteration races with Dispose() and
        // throws ObjectDisposedException once the CTS is disposed.
        CancellationToken token = _cts.Token;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    Cleanup();
                }
                catch
                {
                    //Swallow to keep loop alive
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void Cleanup()
    {
        if (_disposed)
            return;

        foreach (var poolEntry in _pools.Values)
        {
            var now = DateTimeOffset.UtcNow;
            var connectionsToKeep = new List<IPooledConnection>();
            var connectionsToRemove = new List<IPooledConnection>();
            lock (poolEntry.IdleLock)
            {
                while (poolEntry.IdleConnections.TryPop(out var connection))
                {
                    var idleTime = now - connection.LastUsedAt;
                    if (!IsConnectionValid(connection) ||
                        idleTime > poolEntry.Config.PoolConfig.IdleTimeout)
                        connectionsToRemove.Add(connection);
                    else
                        connectionsToKeep.Add(connection);
                }

                foreach (IPooledConnection pooledConnection in connectionsToKeep)
                {
                    poolEntry.IdleConnections.Push(pooledConnection);
                }
            }

            foreach (var connection in connectionsToRemove)
            {
                connection.Dispose();
                poolEntry.CapacitySemaphore.Release();
                Interlocked.Increment(ref _totalConnectionsClosed);
            }
        }
    }

    private async Task<IPooledConnection> CreateConnectionAsync(
        ConnectionConfig config,
        CancellationToken cancellationToken)
    {
        var authToken = await _authService.AuthenticateAsync(
            config.Account,
            config.User,
            config.Authentication,
            config,
            cancellationToken);

        return new PooledConnection(
            Guid.NewGuid().ToString(),
            authToken,
            config,
            _sessionCloser == null ? null : () => _sessionCloser(authToken, config, CancellationToken.None));
    }

    private static string GeneratePoolKey(ConnectionConfig config)
    {
        return $"{config.Account}|{config.User}|{config.Database}|{config.Schema}|{config.Warehouse}|{config.Role}";
    }

    public Task<PoolStatistics> GetStatisticsAsync()
    {
        var totalConnections = 0;
        var activeConnections = 0;
        var idleConnections = 0;
        var pendingRequests = 0L;

        foreach (var poolEntry in _pools.Values)
        {
            var poolSize = poolEntry.Config.PoolConfig.MaxPoolSize - poolEntry.CapacitySemaphore.CurrentCount;
            var idle = poolEntry.IdleConnections.Count;

            totalConnections += poolSize;
            idleConnections += idle;
            activeConnections += poolSize - idle;
            pendingRequests += poolEntry.PendingRequests;
        }

        return Task.FromResult(new PoolStatistics
        {
            TotalConnections = totalConnections,
            ActiveConnections = activeConnections,
            IdleConnections = idleConnections,
            TotalConnectionsCreated = _totalConnectionsCreated,
            TotalConnectionsClosed = _totalConnectionsClosed,
            TotalConnectionReuses = _totalConnectionReuses,
            PendingRequests = pendingRequests
        });
    }
}
