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
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
using Apache.Arrow.Ipc;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Query;

/// <summary>
/// Provides query execution services for Snowflake connections.
/// </summary>
internal interface IQueryExecutor
{
    /// <summary>
    /// Executes a query and returns the result.
    /// </summary>
    /// <param name="request">The query request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query result.</returns>
    Task<QueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Describes (prepares) a statement without executing it, returning its result schema.
    /// </summary>
    /// <param name="request">The query request describing the statement.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A prepared statement with the result schema populated.</returns>
    Task<PreparedStatement> DescribeAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a prepared statement with parameters.
    /// </summary>
    /// <param name="statement">The prepared statement.</param>
    /// <param name="parameters">The parameter set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query result.</returns>
    Task<QueryResult> ExecutePreparedStatementAsync(PreparedStatement statement, ParameterSet parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a running query.
    /// </summary>
    /// <param name="queryId">The query ID to cancel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the cancellation operation.</returns>
    Task CancelQueryAsync(string queryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a query execution request.
/// </summary>
internal class QueryRequest
{
    /// <summary>
    /// Gets or sets the SQL statement to execute.
    /// </summary>
    public string Statement { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the database context for the query.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Gets or sets the schema context for the query.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Gets or sets the warehouse to use for query execution.
    /// </summary>
    public string? Warehouse { get; set; }

    /// <summary>
    /// Gets or sets the role to use for query execution.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Gets or sets the query timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the result format (should be ArrowV1 for ADBC).
    /// </summary>
    public ResultFormat Format { get; set; } = ResultFormat.ArrowV1;

    /// <summary>
    /// Gets or sets the query parameters.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this is a multi-statement query.
    /// </summary>
    public bool IsMultiStatement { get; set; }

    /// <summary>
    /// Gets or sets the authentication token for the request.
    /// </summary>
    public AuthenticationToken? AuthToken { get; set; }
}

/// <summary>
/// Represents the result of a query execution.
/// </summary>
internal class QueryResult
{
    /// <summary>
    /// Gets or sets the statement handle for the executed query.
    /// </summary>
    public string StatementHandle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the query execution status.
    /// </summary>
    public QueryStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the Arrow schema for the result set.
    /// </summary>
    public Schema? Schema { get; set; }

    /// <summary>
    /// Gets or sets the Arrow array stream containing the results.
    /// </summary>
    public IArrowArrayStream? ResultStream { get; set; }

    /// <summary>
    /// Gets or sets the number of rows affected or returned.
    /// </summary>
    public long RowCount { get; set; }

    /// <summary>
    /// Gets or sets the query execution time.
    /// </summary>
    public TimeSpan ExecutionTime { get; set; }

    /// <summary>
    /// Gets or sets any errors that occurred during execution.
    /// </summary>
    public List<QueryError> Errors { get; set; } = new();

    /// <summary>
    /// Gets or sets additional metadata about the query execution.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Represents a prepared statement.
/// </summary>
internal class PreparedStatement
{
    /// <summary>
    /// Gets or sets the statement handle.
    /// </summary>
    public string StatementHandle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SQL statement text.
    /// </summary>
    public string Statement { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parameter schema.
    /// </summary>
    public Schema? ParameterSchema { get; set; }

    /// <summary>
    /// Gets or sets the result schema (if known).
    /// </summary>
    public Schema? ResultSchema { get; set; }
}

/// <summary>
/// Represents a set of parameters for a prepared statement.
/// </summary>
internal class ParameterSet
{
    /// <summary>
    /// Gets or sets the parameter values.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>
    /// Gets or sets the parameter batch (for batch execution).
    /// </summary>
    public RecordBatch? ParameterBatch { get; set; }
}

/// <summary>
/// Represents query execution status.
/// </summary>
internal enum QueryStatus
{
    /// <summary>
    /// Query is queued for execution.
    /// </summary>
    Queued,

    /// <summary>
    /// Query is currently running.
    /// </summary>
    Running,

    /// <summary>
    /// Query completed successfully.
    /// </summary>
    Success,

    /// <summary>
    /// Query failed with an error.
    /// </summary>
    Failed,

    /// <summary>
    /// Query was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Represents the result format for queries.
/// </summary>
internal enum ResultFormat
{
    /// <summary>
    /// Apache Arrow format version 1.
    /// </summary>
    ArrowV1,

    /// <summary>
    /// JSON format (not recommended for ADBC).
    /// </summary>
    Json
}

/// <summary>
/// Represents a query execution error.
/// </summary>
internal class QueryError
{
    /// <summary>
    /// Gets or sets the error code.
    /// </summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SQL state.
    /// </summary>
    public string? SqlState { get; set; }

    /// <summary>
    /// Gets or sets the line number where the error occurred.
    /// </summary>
    public int? LineNumber { get; set; }

    /// <summary>
    /// Gets or sets the column number where the error occurred.
    /// </summary>
    public int? ColumnNumber { get; set; }
}