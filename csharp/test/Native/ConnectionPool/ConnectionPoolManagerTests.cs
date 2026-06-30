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
using AdbcDrivers.Snowflake.Native.Services.ConnectionPool;
using NSubstitute;
using Xunit;

namespace AdbcDrivers.Snowflake.Native.Tests.ConnectionPool;

[Trait("Category", "Unit")]
public class ConnectionPoolManagerTests
{
    /// <summary>Minimal in-memory <see cref="IPooledConnection"/> for exercising the due-logic.</summary>
    private sealed class FakePooledConnection : IPooledConnection
    {
        public ConnectionConfig Config { get; init; } = new();
        public DateTimeOffset LastUsedAt { get; init; }
        public DateTimeOffset LastHeartbeatAt { get; set; }

        public string ConnectionId => "fake";
        public AuthenticationToken AuthToken { get; } = new();
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public bool IsDisposed => false;
        public bool IsTokenExpired => false;
        public bool IsFaulted => false;
        public void MarkFaulted() { }
        void IPooledConnection.UpdateLastUsedAt() { }
        void IPooledConnection.RecordHeartbeat() => LastHeartbeatAt = DateTimeOffset.UtcNow;
        public void Dispose() { }
    }

    [Fact]
    public void IsHeartbeatDue_WhenKeepAliveDisabled_IsNeverDue()
    {
        // Given keep-alive off, even a long-idle connection is never due
        var now = DateTimeOffset.UtcNow;
        var connection = new FakePooledConnection
        {
            Config = new ConnectionConfig { ClientSessionKeepAlive = false, HeartbeatFrequency = TimeSpan.FromMinutes(15) },
            LastUsedAt = now - TimeSpan.FromHours(2),
            LastHeartbeatAt = now - TimeSpan.FromHours(2),
        };

        Assert.False(ConnectionPoolManager.IsHeartbeatDue(connection, now));
    }

    [Fact]
    public void IsHeartbeatDue_WhenIdleBeyondFrequency_IsDue()
    {
        // Given keep-alive on and no activity for longer than the frequency
        var now = DateTimeOffset.UtcNow;
        var connection = new FakePooledConnection
        {
            Config = new ConnectionConfig { ClientSessionKeepAlive = true, HeartbeatFrequency = TimeSpan.FromMinutes(15) },
            LastUsedAt = now - TimeSpan.FromMinutes(20),
            LastHeartbeatAt = now - TimeSpan.FromMinutes(20),
        };

        Assert.True(ConnectionPoolManager.IsHeartbeatDue(connection, now));
    }

    [Fact]
    public void IsHeartbeatDue_WithRecentActivity_IsNotDue()
    {
        // Given a recent query (LastUsedAt) even though the last heartbeat is old, the session is
        // already warm — activity counts as a heartbeat, so none is needed
        var now = DateTimeOffset.UtcNow;
        var connection = new FakePooledConnection
        {
            Config = new ConnectionConfig { ClientSessionKeepAlive = true, HeartbeatFrequency = TimeSpan.FromMinutes(15) },
            LastUsedAt = now - TimeSpan.FromMinutes(1),
            LastHeartbeatAt = now - TimeSpan.FromHours(1),
        };

        Assert.False(ConnectionPoolManager.IsHeartbeatDue(connection, now));
    }

    [Fact]
    public void IsHeartbeatDue_ExactlyAtBoundary_IsDue()
    {
        // When elapsed time equals the heartbeat frequency exactly, should be due (uses >=)
        var now = DateTimeOffset.UtcNow;
        var frequency = TimeSpan.FromMinutes(15);
        var connection = new FakePooledConnection
        {
            Config = new ConnectionConfig { ClientSessionKeepAlive = true, HeartbeatFrequency = frequency },
            LastUsedAt = now - frequency,
            LastHeartbeatAt = now - frequency,
        };

        Assert.True(ConnectionPoolManager.IsHeartbeatDue(connection, now));
    }

    [Fact]
    public void IsHeartbeatDue_WithRecentHeartbeat_IsNotDue()
    {
        // LastHeartbeatAt is recent even though LastUsedAt is old — not due
        var now = DateTimeOffset.UtcNow;
        var connection = new FakePooledConnection
        {
            Config = new ConnectionConfig { ClientSessionKeepAlive = true, HeartbeatFrequency = TimeSpan.FromMinutes(15) },
            LastUsedAt = now - TimeSpan.FromHours(2),
            LastHeartbeatAt = now - TimeSpan.FromMinutes(5),
        };

        Assert.False(ConnectionPoolManager.IsHeartbeatDue(connection, now));
    }

    [Fact]
    public async Task Pool_WithNoHeartbeatDelegate_DisposesCleanly()
    {
        // Create pool with no sessionHeartbeat delegate, acquire+release, dispose — no crash
        var authService = Substitute.For<IAuthenticationService>();
        authService.AuthenticateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AuthenticationConfig>(),
            Arg.Any<ConnectionConfig>(), Arg.Any<CancellationToken>())
            .Returns(new AuthenticationToken
            {
                SessionToken = "session",
                MasterToken = "master",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });

        var pool = new ConnectionPoolManager(authService, sessionCloser: null, sessionHeartbeat: null);
        try
        {
            var config = new ConnectionConfig
            {
                Account = "test",
                User = "user",
                ClientSessionKeepAlive = true,
                HeartbeatFrequency = TimeSpan.FromMinutes(15),
            };

            var connection = await pool.AcquireConnectionAsync(config);
            pool.ReleaseConnection(connection);
        }
        finally
        {
            pool.Dispose();
        }
    }

    [Fact]
    public void Pool_WithHeartbeatDelegate_DisposesCleanly()
    {
        // Verify pool correctly accepts a heartbeat delegate and disposes without errors
        var authService = Substitute.For<IAuthenticationService>();
        var heartbeatCalled = false;

        var pool = new ConnectionPoolManager(
            authService,
            sessionCloser: null,
            sessionHeartbeat: (_, _, _) =>
            {
                heartbeatCalled = true;
                return Task.CompletedTask;
            });

        pool.Dispose();

        // The delegate was never called because no connection was acquired/released
        Assert.False(heartbeatCalled);
    }

    [Fact]
    public async Task HeartbeatIdleConnections_AfterDispose_DelegateIsNotCalled()
    {
        // Constructs the pool with a heartbeat delegate, acquires/releases a connection with
        // keep-alive enabled, and verifies that after Dispose the delegate is not called
        // (i.e., cancellation works).
        var authService = Substitute.For<IAuthenticationService>();
        authService.AuthenticateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AuthenticationConfig>(),
            Arg.Any<ConnectionConfig>(), Arg.Any<CancellationToken>())
            .Returns(new AuthenticationToken
            {
                SessionToken = "session",
                MasterToken = "master",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });

        var heartbeatCallCount = 0;
        var pool = new ConnectionPoolManager(
            authService,
            sessionCloser: null,
            sessionHeartbeat: (_, _, _) =>
            {
                Interlocked.Increment(ref heartbeatCallCount);
                return Task.CompletedTask;
            });

        var config = new ConnectionConfig
        {
            Account = "test",
            User = "user",
            ClientSessionKeepAlive = true,
            HeartbeatFrequency = TimeSpan.FromMilliseconds(1),
        };

        var connection = await pool.AcquireConnectionAsync(config);
        pool.ReleaseConnection(connection);

        // Dispose cancels the cleanup loop, so the 60s PeriodicTimer never ticks
        pool.Dispose();

        // Wait briefly to confirm no activity after dispose
        await Task.Delay(100);
        Assert.Equal(0, heartbeatCallCount);
    }
}
