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
using AdbcDrivers.Snowflake.Native.Configuration;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
using AdbcDrivers.Snowflake.Native.Services.ConnectionPool;
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
}
