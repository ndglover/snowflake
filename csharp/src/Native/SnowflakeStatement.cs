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
using System.Diagnostics;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Configuration;
using AdbcDrivers.Snowflake.Native.Services.ConnectionPool;
using AdbcDrivers.Snowflake.Native.Services.Query;
using AdbcDrivers.Snowflake.Native.Services.TypeConversion;

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.Tracing;

// Disambiguate from AdbcDrivers.Snowflake.Native.Services.Query.QueryResult.
using QueryResult = Apache.Arrow.Adbc.QueryResult;

namespace AdbcDrivers.Snowflake.Native;

/// <summary>
/// Snowflake statement implementation for ADBC. Executions publish telemetry spans through
/// the connection's ActivitySource (see <see cref="SnowflakeConnection"/>), always tagged
/// with Snowflake's queryId; SQL text rides along only when the connection opts in.
/// </summary>
public sealed class SnowflakeStatement : TracingStatement
{
    private readonly SnowflakeConnection _connection;
    private readonly ConnectionConfig _config;
    private readonly IPooledConnection _pooledConnection;
    private readonly IQueryExecutor _queryExecutor;
    private readonly ITypeConverter _typeConverter;
    private RecordBatch? _boundParameters;
    // The request id of the in-flight query, set before each execution so Cancel (called from
    // another thread) can abort that specific request. Volatile for cross-thread visibility.
    private volatile string? _currentRequestId;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnowflakeStatement"/> class.
    /// </summary>
    /// <param name="connection">The owning connection (supplies the telemetry trace).</param>
    /// <param name="config">The connection configuration.</param>
    /// <param name="pooledConnection">The pooled connection.</param>
    /// <param name="queryExecutor">The query executor.</param>
    internal SnowflakeStatement(
        SnowflakeConnection connection,
        ConnectionConfig config,
        IPooledConnection pooledConnection,
        IQueryExecutor queryExecutor)
        : base(connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _pooledConnection = pooledConnection ?? throw new ArgumentNullException(nameof(pooledConnection));
        _queryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
        _typeConverter = TypeConverter.Shared;
    }

    /// <inheritdoc/>
    public override string AssemblyName => _connection.AssemblyName;

    /// <inheritdoc/>
    public override string AssemblyVersion => _connection.AssemblyVersion;

    /// <summary>
    /// Sets a statement option. Supported: <see cref="AdbcOptions.Telemetry.TraceParent"/> —
    /// re-links this statement's spans to the caller's current distributed trace (long-lived
    /// statements serve many traces).
    /// </summary>
    public override void SetOption(string key, string value)
    {
        ThrowIfDisposed();

        if (string.Equals(key, AdbcOptions.Telemetry.TraceParent, StringComparison.Ordinal))
        {
            SetTraceParent(string.IsNullOrWhiteSpace(value) ? null : value);
            return;
        }

        base.SetOption(key, value);
    }

    /// <summary>
    /// Tags common statement attributes: the database namespace, and — only when the
    /// connection opted in — the SQL text (queryId is tagged after execution instead, as the
    /// privacy-safe join key to QUERY_HISTORY).
    /// </summary>
    private void TagStatement(Activity? activity)
    {
        if (activity == null)
            return;

        if (!string.IsNullOrEmpty(_config.Database))
        {
            activity.SetTag(SemanticConventions.Namespace,
                string.IsNullOrEmpty(_config.Schema) ? _config.Database : $"{_config.Database}.{_config.Schema}");
        }

        if (_connection.IncludeQueryTextInTraces)
            activity.SetTag(SemanticConventions.Db.Query.Text, SqlQuery);
    }

    /// <summary>
    /// Binds parameters to the statement using a RecordBatch.
    /// </summary>
    /// <param name="batch">The RecordBatch containing parameter values.</param>
    /// <param name="schema">The schema of the RecordBatch.</param>
    public override void Bind(RecordBatch batch, Schema schema)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(schema);

        _boundParameters = batch;
    }

    /// <summary>
    /// Executes the query and returns a QueryResult.
    /// </summary>
    /// <returns>A QueryResult containing the query results.</returns>
    public override QueryResult ExecuteQuery()
    {
        // Use async-first pattern: sync version calls async with proper blocking
        return ExecuteQueryAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the query asynchronously and returns a QueryResult.
    /// </summary>
    /// <returns>A QueryResult containing the query results.</returns>
    public override async ValueTask<QueryResult> ExecuteQueryAsync()
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(SqlQuery))
            throw new InvalidOperationException("SQL query must be set before execution.");

        return await this.TraceActivityAsync(async activity =>
        {
            TagStatement(activity);
            try
            {
                var request = BuildRequest();

                var result = await _queryExecutor.ExecuteQueryAsync(request).ConfigureAwait(false);

                if (result.Status == QueryStatus.Cancelled)
                    throw new AdbcException("Query was cancelled.");

                if (result.Status != QueryStatus.Success)
                    throw ToAdbcException("Query failed", result);

                activity?.SetTag(SemanticConventions.Db.Response.OperationId, result.QueryId);
                activity?.SetTag(SemanticConventions.Db.Response.ReturnedRows, result.RowCount);

                // Every Success shape from the executor carries a stream (unsupported response
                // shapes fail before reaching here). The reader wrapper spans batch reads so
                // fetch time is visible separately from execution time.
                return new QueryResult(result.RowCount, new SnowflakeTracingReader(this, result.ResultStream!));
            }
            catch (AdbcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AdbcException($"Query execution failed: {ex.Message}", ex);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the query request for the current SqlQuery, recording the request id for
    /// Cancel and attaching any bound parameters.
    /// </summary>
    private QueryRequest BuildRequest()
    {
        var request = new QueryRequest
        {
            // Callers validate SqlQuery is set before building the request.
            Statement = SqlQuery!,
            Database = _config.Database,
            Schema = _config.Schema,
            Warehouse = _config.Warehouse,
            Role = _config.Role,
            Timeout = _config.QueryTimeout,
            PrefetchConcurrency = _config.PrefetchConcurrency,
            RequestId = NewRequestId(),
            AuthToken = _pooledConnection.AuthToken
        };

        if (_boundParameters != null)
        {
            var parameterSet = _typeConverter.ConvertArrowBatchToParameters(_boundParameters);
            foreach (var kvp in parameterSet.Parameters)
                request.Bindings[kvp.Key] = kvp.Value;
        }

        return request;
    }

    /// <summary>
    /// Executes an update query and returns the number of affected rows.
    /// </summary>
    /// <returns>An UpdateResult containing the number of affected rows.</returns>
    public override UpdateResult ExecuteUpdate()
    {
        // Use async-first pattern: sync version calls async with proper blocking
        return ExecuteUpdateAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes an update query asynchronously and returns the number of affected rows.
    /// </summary>
    /// <returns>An UpdateResult containing the number of affected rows.</returns>
    public override async Task<UpdateResult> ExecuteUpdateAsync()
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(SqlQuery))
            throw new InvalidOperationException("SQL query must be set before execution.");

        return await this.TraceActivityAsync(async activity =>
        {
            TagStatement(activity);
            try
            {
                var request = BuildRequest();

                var result = await _queryExecutor.ExecuteQueryAsync(request).ConfigureAwait(false);

                if (result.Status == QueryStatus.Cancelled)
                    throw new AdbcException("Update was cancelled.");

                if (result.Status != QueryStatus.Success)
                    throw ToAdbcException("Update failed", result);

                // DML statements report the affected-row count parsed from the JSON row-count
                // summary in QueryExecutor. Any other statement (SELECT, DDL status rows, ...)
                // affects no rows, so report -1 (unknown/not applicable) per the ADBC contract.
                long affectedRows = result.AffectedRows ?? -1;

                activity?.SetTag(SemanticConventions.Db.Response.OperationId, result.QueryId);
                activity?.SetTag("snowflake.affected_rows", affectedRows);

                // ExecuteUpdate has no use for the result set (DML/DDL surface theirs for
                // ExecuteQuery); release it rather than hold the batch until finalization.
                result.ResultStream?.Dispose();
                return new UpdateResult(affectedRows);
            }
            catch (AdbcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AdbcException($"Update execution failed: {ex.Message}", ex);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the query currently executing on this statement, if any. Safe to call from a
    /// different thread than the one running the query (the typical use). No-ops when nothing is
    /// executing. The server-side abort is best-effort: a query that has already finished is not
    /// an error.
    /// </summary>
    public override void Cancel() => CancelAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronous form of <see cref="Cancel"/>.
    /// </summary>
    public async Task CancelAsync()
    {
        ThrowIfDisposed();

        var requestId = _currentRequestId;
        if (string.IsNullOrEmpty(requestId))
            return;

        await this.TraceActivityAsync(
            _ => _queryExecutor.CancelQueryAsync(requestId, _pooledConnection.AuthToken)).ConfigureAwait(false);
    }

    // Generates and records the request id for the execution that is about to start, so a
    // concurrent Cancel can abort exactly this request.
    private string NewRequestId()
    {
        var requestId = Guid.NewGuid().ToString();
        _currentRequestId = requestId;
        return requestId;
    }

    /// <summary>
    /// Builds the failure exception from a failed result, carrying the originating exception as the
    /// inner exception (when there was one) so the full stack survives instead of just its message.
    /// </summary>
    private static AdbcException ToAdbcException(string prefix, Services.Query.QueryResult result)
    {
        var errorMessages = result.Errors.Count > 0
            ? string.Join("; ", result.Errors.ConvertAll(e => $"[{e.ErrorCode}] {e.Message}"))
            : "Unknown error";
        var cause = result.Errors.Find(e => e.Exception != null)?.Exception;
        return cause is null
            ? new AdbcException($"{prefix}: {errorMessages}")
            : new AdbcException($"{prefix}: {errorMessages}", cause);
    }

    /// <summary>
    /// Prepares the statement for execution.
    /// </summary>
    /// <remarks>
    /// Snowflake has no server-side prepare step -- statements are compiled when they are
    /// executed -- so this only validates that a query has been set and otherwise does nothing.
    /// </remarks>
    public override void Prepare()
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(SqlQuery))
            throw new InvalidOperationException("SQL query must be set before preparation.");
    }

    /// <summary>
    /// Gets the parameter schema for a prepared statement.
    /// </summary>
    /// <remarks>
    /// Not supported: Snowflake's protocol does not report the types of a statement's bind
    /// parameters, so a parameter schema cannot be determined.
    /// </remarks>
    public override Schema GetParameterSchema()
    {
        ThrowIfDisposed();

        throw AdbcException.NotImplemented("Snowflake does not provide a parameter schema.");
    }

    /// <summary>
    /// Disposes the statement and releases any resources. (The tracing base class owns
    /// <c>Dispose()</c> and routes here.)
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _boundParameters?.Dispose();
            _boundParameters = null;

            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
