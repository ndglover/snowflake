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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Services;
using AdbcDrivers.Snowflake.Native.Services.Transport;
using AdbcDrivers.Snowflake.Native.Services.TypeConversion;
using Microsoft.Extensions.Logging;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Query;

/// <summary>
/// Implements query execution for Snowflake connections.
/// </summary>
internal class QueryExecutor : IQueryExecutor
{
    private readonly IRestApiClient _apiClient;
    private readonly ITypeConverter _typeConverter;
    private readonly string _accountUrl;
    private readonly ILogger<QueryExecutor> _logger;
    private const string QueryEndpoint = "/queries/v1/query-request";

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryExecutor"/> class.
    /// </summary>
    /// <param name="apiClient">The REST API client.</param>
    /// <param name="typeConverter">The Snowflake/Arrow type converter.</param>
    /// <param name="account">The Snowflake account identifier.</param>
    /// <param name="network">The network configuration.</param>
    /// <param name="logger">The ILogger instance for logging.</param>
    public QueryExecutor(
        IRestApiClient apiClient,
        ITypeConverter typeConverter,
        string account,
        Configuration.NetworkConfig? network,
        ILogger<QueryExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(typeConverter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(account);

        _apiClient = apiClient;
        _typeConverter = typeConverter;
        _logger = logger;

        _accountUrl = SnowflakeAccountUrl.Build(account, network);
    }

    /// <inheritdoc/>
    public async Task<QueryResult> ExecuteQueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Statement, nameof(request.Statement));
        ArgumentNullException.ThrowIfNull(request.AuthToken);
        var authToken = request.AuthToken;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var queryRequest = BuildQueryRequest(request, out string endpoint);
            var response = await _apiClient.PostAsync<SnowflakeQueryResponse>(
                endpoint,
                queryRequest,
                authToken,
                cancellationToken);

            stopwatch.Stop();

            if (!response.Success || response.Data == null)
                return CreateFailedResponseResult(response, stopwatch.Elapsed);

            var data = response.Data;
            _logger.LogDebug(
                "QueryResultFormat={QueryResultFormat}, HasRowSetBase64={HasRowSetBase64}, ChunkCount={ChunkCount}, HasRowSet={HasRowSet}, HasRowType={HasRowType}",
                data.QueryResultFormat,
                !string.IsNullOrEmpty(data.RowSetBase64),
                data.Chunks?.Count ?? 0,
                data.RowSet != null,
                data.RowType != null);

            if (HasArrowResult(data))
                return await CreateSuccessResultAsync(data, authToken, cancellationToken, stopwatch.Elapsed).ConfigureAwait(false);

            // DML statements (INSERT/UPDATE/DELETE/MERGE) return a JSON summary row whose
            // columns are the affected-row counts (e.g. "number of rows inserted"), not Arrow.
            if (TryGetDmlAffectedRows(data, out long affectedRows))
                return CreateDmlResult(data, affectedRows, stopwatch.Elapsed);

            // Any other non-Arrow JSON result (DDL status messages such as
            // "Table X successfully created.", USE/ALTER SESSION, etc.) is a successful
            // statement that simply produced no Arrow result set.
            return CreateNoResultSuccess(data, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            return new QueryResult
            {
                Status = QueryStatus.Cancelled,
                ExecutionTime = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new QueryResult
            {
                Status = QueryStatus.Failed,
                ExecutionTime = stopwatch.Elapsed,
                Errors = new List<QueryError>
                {
                    new QueryError
                    {
                        ErrorCode = "EXECUTION_ERROR",
                        Message = $"Query execution failed: {ex.Message}"
                    }
                }
            };
        }
    }

    private static bool HasArrowResult(SnowflakeQueryResponse data) =>
        !string.IsNullOrEmpty(data.RowSetBase64) || (data.Chunks?.Count > 0);

    /// <summary>
    /// Detects a DML row-count summary result and sums the affected-row counts.
    /// Snowflake returns DML results as a single JSON row whose columns are named
    /// "number of rows inserted" / "...updated" / "...deleted" (MERGE returns several).
    /// </summary>
    internal static bool TryGetDmlAffectedRows(SnowflakeQueryResponse data, out long affectedRows)
    {
        affectedRows = 0;

        List<RowType>? rowTypes = data.RowType;
        List<List<string>>? rowSet = data.RowSet;
        if (rowTypes == null || rowTypes.Count == 0 || rowSet == null || rowSet.Count == 0)
            return false;

        foreach (RowType rowType in rowTypes)
        {
            if (rowType.Name == null ||
                !rowType.Name.StartsWith("number of ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        long total = 0;
        foreach (string cell in rowSet[0])
        {
            if (long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
                total += count;
        }

        affectedRows = total;
        return true;
    }

    private static QueryResult CreateDmlResult(SnowflakeQueryResponse data, long affectedRows, TimeSpan executionTime) =>
        new()
        {
            StatementHandle = data.QueryId ?? string.Empty,
            Status = QueryStatus.Success,
            RowCount = affectedRows,
            ExecutionTime = executionTime
        };

    private static QueryResult CreateFailedResponseResult(ApiResponse<SnowflakeQueryResponse> response, TimeSpan executionTime) =>
        new()
        {
            Status = QueryStatus.Failed,
            ExecutionTime = executionTime,
            Errors =
            [
                new QueryError
                {
                    ErrorCode = response.Code ?? "UNKNOWN",
                    Message = response.Message ?? "Query execution failed."
                }
            ]
        };

    private static QueryResult CreateNoResultSuccess(SnowflakeQueryResponse data, TimeSpan executionTime) =>
        new()
        {
            StatementHandle = data.QueryId ?? string.Empty,
            Status = QueryStatus.Success,
            RowCount = data.Returned ?? 0,
            ExecutionTime = executionTime
        };

    private async Task<QueryResult> CreateSuccessResultAsync(
        SnowflakeQueryResponse data,
        Authentication.AuthenticationToken authToken,
        CancellationToken cancellationToken,
        TimeSpan executionTime)
    {
        var arrayStream = await ChunkedArrowArrayStream.CreateAsync(
            _apiClient,
            authToken,
            data.RowSetBase64,
            data.Chunks,
            data.ChunkHeaders,
            data.Qrmk,
            cancellationToken).ConfigureAwait(false);

        return new QueryResult
        {
            StatementHandle = data.QueryId ?? string.Empty,
            Status = QueryStatus.Success,
            Schema = arrayStream.Schema,
            ResultStream = arrayStream,
            RowCount = data.Returned ?? 0,
            ExecutionTime = executionTime
        };
    }

    /// <inheritdoc/>
    public async Task<PreparedStatement> DescribeAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Statement, nameof(request.Statement));
        ArgumentNullException.ThrowIfNull(request.AuthToken);

        // Snowflake's internal protocol has no dedicated prepare endpoint; a statement is
        // described (compiled without executing) by sending it to the query-request endpoint
        // with describeOnly=true. The response's rowtype is the result schema.
        var describeRequest = BuildQueryRequest(request, out string endpoint, describeOnly: true);
        var response = await _apiClient.PostAsync<SnowflakeQueryResponse>(
            endpoint,
            describeRequest,
            request.AuthToken,
            cancellationToken).ConfigureAwait(false);

        if (!response.Success || response.Data == null)
            throw new AdbcException($"Failed to describe statement: {response.Message ?? "Unknown error"}");

        return new PreparedStatement
        {
            StatementHandle = response.Data.QueryId ?? string.Empty,
            Statement = request.Statement,
            // describeOnly returns the result columns (rowtype) but not bind/parameter
            // metadata, so ParameterSchema is left null (matches gosnowflake behavior).
            ParameterSchema = null,
            ResultSchema = BuildSchemaFromRowType(response.Data.RowType)
        };
    }

    private Schema? BuildSchemaFromRowType(List<RowType>? rowTypes)
    {
        if (rowTypes == null || rowTypes.Count == 0)
            return null;

        var fields = new List<Field>(rowTypes.Count);
        foreach (RowType rowType in rowTypes)
        {
            var snowflakeType = new SnowflakeDataType
            {
                TypeName = rowType.Type ?? string.Empty,
                Precision = rowType.Precision,
                Scale = rowType.Scale,
                Length = rowType.Length,
                IsNullable = rowType.Nullable ?? true
            };

            fields.Add(new Field(
                rowType.Name ?? string.Empty,
                _typeConverter.ConvertSnowflakeTypeToArrow(snowflakeType),
                rowType.Nullable ?? true));
        }

        return new Schema(fields, null);
    }

    private object BuildQueryRequest(QueryRequest request, out string endpoint, bool describeOnly = false)
    {
        var queryRequest = RequestBuilder.BuildQueryRequest(
            request.Statement,
            request.Database,
            request.Schema,
            request.Warehouse,
            request.Role,
            (int)request.Timeout.TotalSeconds,
            request.Parameters,
            request.IsMultiStatement,
            describeOnly);

        var requestId = Guid.NewGuid().ToString();
        var requestGuid = Guid.NewGuid().ToString();
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        endpoint = $"{_accountUrl}{QueryEndpoint}?requestId={requestId}&request_guid={requestGuid}&startTime={startTime}";
        var sessionId = request.AuthToken?.SessionId;
        if (!string.IsNullOrEmpty(sessionId))
            endpoint += $"&sid={sessionId}";
        return queryRequest;
    }

    /// <inheritdoc/>
    public async Task<QueryResult> ExecutePreparedStatementAsync(
        PreparedStatement statement,
        ParameterSet parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(parameters);

        var request = new QueryRequest
        {
            Statement = statement.Statement,
            Parameters = parameters.Parameters
        };

        return await ExecuteQueryAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task CancelQueryAsync(string queryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryId);

        throw new NotImplementedException("Query cancellation not yet implemented");
    }

}
