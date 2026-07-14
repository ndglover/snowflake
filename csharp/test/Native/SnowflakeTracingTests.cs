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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
using AdbcDrivers.Snowflake.Native.Services.ConnectionPool;
using AdbcDrivers.Snowflake.Native.Services.Query;
using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Types;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AdbcDrivers.Snowflake.Native.Tests;

/// <summary>
/// Offline tests for the driver's OpenTelemetry-standard tracing (ADBC
/// <c>TracingConnection</c>/<c>TracingStatement</c>): spans exist only when a listener
/// subscribes to the ActivitySource, carry the queryId join key (SQL text only when opted
/// in), mark failures as errors, and honor the trace-parent and source-name options.
/// Each test listens on a unique source name (via the activity-source option) so parallel
/// tests cannot capture each other's activities.
/// </summary>
[Trait("Category", "Unit")]
public class SnowflakeTracingTests
{
    private sealed class CapturingListener : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Stopped { get; } = [];

        public CapturingListener(string sourceName)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => { lock (Stopped) Stopped.Add(activity); },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity Single(string name)
        {
            lock (Stopped)
                return Assert.Single(Stopped, a => a.OperationName == name);
        }

        public void Dispose() => _listener.Dispose();
    }

    private static AuthenticationToken CreateToken() => new()
    {
        SessionToken = "session-token",
        MasterToken = "master-token",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    private static Services.Query.QueryResult SuccessResult() => new()
    {
        Status = QueryStatus.Success,
        RowCount = 2,
        QueryId = "qid-123",
        ResultStream = new EmptyArrowArrayStream(new Schema([new Field("ID", Int32Type.Default, true)], null)),
    };

    private static SnowflakeConnection CreateConnection(
        Dictionary<string, string> properties, Services.Query.QueryResult? executorResult = null)
    {
        var executor = Substitute.For<IQueryExecutor>();
        executor.ExecuteQueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => executorResult ?? SuccessResult());

        var pooled = Substitute.For<IPooledConnection>();
        pooled.AuthToken.Returns(CreateToken());

        return new SnowflakeConnection(new ConnectionConfig { Database = "DB", Schema = "SC" },
            Substitute.For<IConnectionPoolManager>(), pooled, executor,
            NullLogger<SnowflakeConnection>.Instance, properties);
    }

    private static Dictionary<string, string> SourceProperties(string sourceName, params (string Key, string Value)[] extra)
    {
        var properties = new Dictionary<string, string>
        {
            [SnowflakeConnection.ActivitySourceNameOption] = sourceName,
        };
        foreach ((string key, string value) in extra)
            properties[key] = value;
        return properties;
    }

    [Fact]
    public void ActivitySourceName_DefaultsToDriverName_AndHonorsOverride()
    {
        using var defaulted = CreateConnection([]);
        Assert.Equal(SnowflakeConnection.DefaultActivitySourceName, defaulted.AssemblyName);

        using var overridden = CreateConnection(SourceProperties("my-service"));
        Assert.Equal("my-service", overridden.AssemblyName);
    }

    [Fact]
    public async Task ExecuteQuery_EmitsSpanWithQueryId_AndNoSqlTextByDefault()
    {
        using var listener = new CapturingListener("trace-test-query");
        using var connection = CreateConnection(SourceProperties("trace-test-query"));
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1";

        var result = await statement.ExecuteQueryAsync();
        result.Stream!.Dispose();

        Activity span = listener.Single("ExecuteQueryAsync");
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("qid-123", span.GetTagItem("db.response.operation_id"));
        Assert.Equal("DB.SC", span.GetTagItem("db.namespace"));
        Assert.Null(span.GetTagItem("db.query.text"));
    }

    [Fact]
    public async Task ExecuteQuery_WithIncludeQueryTextOption_TagsSqlText()
    {
        using var listener = new CapturingListener("trace-test-text");
        using var connection = CreateConnection(SourceProperties("trace-test-text",
            (SnowflakeConnection.IncludeQueryTextOption, "true")));
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 42";

        var result = await statement.ExecuteQueryAsync();
        result.Stream!.Dispose();

        Assert.Equal("SELECT 42", listener.Single("ExecuteQueryAsync").GetTagItem("db.query.text"));
    }

    [Fact]
    public async Task ExecuteQuery_Failure_MarksSpanError()
    {
        using var listener = new CapturingListener("trace-test-error");
        using var connection = CreateConnection(SourceProperties("trace-test-error"),
            Services.Query.QueryResult.Failed("BOOM", "query exploded"));
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1";

        await Assert.ThrowsAsync<AdbcException>(async () => await statement.ExecuteQueryAsync());

        Assert.Equal(ActivityStatusCode.Error, listener.Single("ExecuteQueryAsync").Status);
    }

    [Fact]
    public async Task TraceParentOption_LinksSpansToCallerTrace()
    {
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        using var listener = new CapturingListener("trace-test-parent");
        using var connection = CreateConnection(SourceProperties("trace-test-parent",
            (AdbcOptions.Telemetry.TraceParent, traceParent)));
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1";

        var result = await statement.ExecuteQueryAsync();
        result.Stream!.Dispose();

        Assert.Equal("0af7651916cd43dd8448eb211c80319c",
            listener.Single("ExecuteQueryAsync").TraceId.ToString());
    }

    [Fact]
    public async Task StatementSetOption_TraceParent_RelinksSubsequentSpans()
    {
        const string traceParent = "00-1bf7651916cd43dd8448eb211c80319d-c7ad6b7169203332-01";
        using var listener = new CapturingListener("trace-test-relink");
        using var connection = CreateConnection(SourceProperties("trace-test-relink"));
        using var statement = connection.CreateStatement();
        statement.SetOption(AdbcOptions.Telemetry.TraceParent, traceParent);
        statement.SqlQuery = "SELECT 1";

        var result = await statement.ExecuteQueryAsync();
        result.Stream!.Dispose();

        Assert.Equal("1bf7651916cd43dd8448eb211c80319d",
            listener.Single("ExecuteQueryAsync").TraceId.ToString());
    }

    [Fact]
    public void TransactionOperations_EmitSpans()
    {
        using var listener = new CapturingListener("trace-test-txn");
        using var connection = CreateConnection(SourceProperties("trace-test-txn"),
            new Services.Query.QueryResult { Status = QueryStatus.Success });

        connection.SetOption(AdbcOptions.Connection.Autocommit, AdbcOptions.Disabled);
        connection.Commit();

        Assert.Equal(ActivityStatusCode.Ok, listener.Single("SetAutocommit").Status);
        Assert.Equal(ActivityStatusCode.Ok, listener.Single("Commit").Status);
    }
}
