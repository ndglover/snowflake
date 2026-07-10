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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
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
    private readonly Action _onConnectionFault;
    // Serializes session renewal on this connection so concurrent statements don't double-renew.
    private readonly SemaphoreSlim _renewLock = new(1, 1);
    private const string QueryEndpoint = "/queries/v1/query-request";
    private const string AbortEndpoint = "/queries/v1/abort-request";
    private const string TokenRequestEndpoint = "/session/token-request";
    private const string HeartbeatEndpoint = "/session/heartbeat";

    // GS error code Snowflake returns when the session token has expired.
    const string SessionExpiredCode = "390112";

    // GS error code Snowflake returns when the master token has also expired; the session cannot
    // be recovered by renewal — the user must authenticate again.
    const string MasterTokenExpiredCode = "390114";

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryExecutor"/> class.
    /// </summary>
    /// <param name="apiClient">The REST API client.</param>
    /// <param name="typeConverter">The Snowflake/Arrow type converter.</param>
    /// <param name="account">The Snowflake account identifier.</param>
    /// <param name="network">The network configuration.</param>
    /// <param name="logger">The ILogger instance for logging.</param>
    /// <param name="onConnectionFault">
    /// Invoked when a failure leaves the session unusable or in an unknown state — a transport-level
    /// error mid-request, a failed renewal, or a session-fatal GS code — so the owner (the pooled
    /// connection) can be flagged for discard instead of being reused. Ordinary SQL errors and
    /// caller cancellations do not trigger it.
    /// </param>
    public QueryExecutor(
        IRestApiClient apiClient,
        ITypeConverter typeConverter,
        string account,
        Configuration.NetworkConfig? network,
        ILogger<QueryExecutor> logger,
        Action onConnectionFault)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(typeConverter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(onConnectionFault);
        ArgumentException.ThrowIfNullOrEmpty(account);

        _apiClient = apiClient;
        _typeConverter = typeConverter;
        _logger = logger;
        _onConnectionFault = onConnectionFault;

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

        try
        {
            var response = await PostQueryWithRenewalAsync(request, describeOnly: false, authToken, cancellationToken).ConfigureAwait(false);

            if (!response.Success || response.Data == null)
                return CreateFailedResponseResult(response);

            var data = response.Data;
            _logger.LogDebug(
                "QueryResultFormat={QueryResultFormat}, HasRowSetBase64={HasRowSetBase64}, ChunkCount={ChunkCount}, HasRowSet={HasRowSet}, HasRowType={HasRowType}",
                data.QueryResultFormat,
                !string.IsNullOrEmpty(data.RowSetBase64),
                data.Chunks?.Count ?? 0,
                data.RowSet != null,
                data.RowType != null);

            if (HasArrowResult(data))
                return await CreateSuccessResultAsync(data, authToken, request.PrefetchConcurrency, cancellationToken).ConfigureAwait(false);

            // DML statements (INSERT/UPDATE/DELETE/MERGE) return a JSON summary row whose
            // columns are the affected-row counts (e.g. "number of rows inserted"), not Arrow.
            if (TryGetDmlAffectedRows(data, out long affectedRows))
                return CreateDmlResult(affectedRows);

            // Any other non-Arrow JSON result (DDL status messages such as
            // "Table X successfully created.", USE/ALTER SESSION, etc.) is a successful
            // statement that simply produced no Arrow result set.
            return CreateNoResultSuccess(data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new QueryResult
            {
                Status = QueryStatus.Cancelled
            };
        }
        catch (Exception ex)
        {
            return new QueryResult
            {
                Status = QueryStatus.Failed,
                Errors =
                [
                    new QueryError()
                    {
                        ErrorCode = "EXECUTION_ERROR",
                        Message = $"Query execution failed: {ex.Message}",
                        Exception = ex
                    }
                ]
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

    private static QueryResult CreateDmlResult(long affectedRows) =>
        new()
        {
            Status = QueryStatus.Success,
            RowCount = affectedRows
        };

    private static QueryResult CreateFailedResponseResult(ApiResponse<SnowflakeQueryResponse> response) =>
        new()
        {
            Status = QueryStatus.Failed,
            Errors =
            [
                new QueryError
                {
                    ErrorCode = response.Code ?? "UNKNOWN",
                    Message = response.Message ?? "Query execution failed."
                }
            ]
        };

    private static QueryResult CreateNoResultSuccess(SnowflakeQueryResponse data) =>
        new()
        {
            Status = QueryStatus.Success,
            RowCount = data.Returned ?? 0
        };

    private async Task<QueryResult> CreateSuccessResultAsync(
        SnowflakeQueryResponse data,
        AuthenticationToken authToken,
        int prefetchConcurrency,
        CancellationToken cancellationToken)
    {
        var arrayStream = await ChunkedArrowArrayStream.CreateAsync(
            _apiClient,
            authToken,
            data.RowSetBase64,
            data.Chunks,
            data.ChunkHeaders,
            data.Qrmk,
            cancellationToken,
            prefetchConcurrency).ConfigureAwait(false);

        // Apply Snowflake-specific result fixups (e.g. rescaling FIXED-with-scale integer
        // columns to Decimal128) before exposing the stream.
        var resultStream = new SnowflakeResultArrowStream(arrayStream);

        return new QueryResult
        {
            Status = QueryStatus.Success,
            ResultStream = resultStream,
            RowCount = data.Returned ?? 0
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
        var response = await PostQueryWithRenewalAsync(request, describeOnly: true, request.AuthToken, cancellationToken).ConfigureAwait(false);

        if (!response.Success || response.Data == null)
            throw new AdbcException($"Failed to describe statement: {response.Message ?? "Unknown error"}");

        return new PreparedStatement
        {
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

    private SnowflakeQueryRequestBody BuildQueryRequest(QueryRequest request, out string endpoint, bool describeOnly = false)
    {
        var queryRequest = RequestBuilder.BuildQueryRequest(
            request.Statement,
            request.Database,
            request.Schema,
            request.Warehouse,
            request.Role,
            (int)request.Timeout.TotalSeconds,
            request.Bindings,
            request.IsMultiStatement,
            describeOnly);

        // A caller-supplied request id lets the statement abort this exact request later; the
        // request_guid is per-attempt and is regenerated on the renewal retry.
        var requestId = string.IsNullOrEmpty(request.RequestId) ? Guid.NewGuid().ToString() : request.RequestId;
        var requestGuid = Guid.NewGuid().ToString();
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        endpoint = $"{_accountUrl}{QueryEndpoint}?requestId={requestId}&request_guid={requestGuid}&startTime={startTime}";
        var sessionId = request.AuthToken?.SessionId;
        if (!string.IsNullOrEmpty(sessionId))
            endpoint += $"&sid={sessionId}";
        return queryRequest;
    }

    /// <summary>
    /// Posts a query/describe request (renewing an expired session token and retrying once — see
    /// <see cref="PostQueryCoreAsync"/>) and classifies any failure for the pool: outcomes that leave
    /// the session unusable or in an unknown state fault the pooled connection so it is discarded
    /// instead of reused; a caller cancellation or an ordinary statement error does not.
    /// </summary>
    private async Task<ApiResponse<SnowflakeQueryResponse>> PostQueryWithRenewalAsync(
        QueryRequest request, bool describeOnly, AuthenticationToken authToken, CancellationToken cancellationToken)
    {
        ApiResponse<SnowflakeQueryResponse> response;
        try
        {
            response = await PostQueryCoreAsync(request, describeOnly, authToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled; the session itself is still good.
            throw;
        }
        catch
        {
            // A transport-level failure (network error, timeout, malformed response) or a failed
            // renewal: the session's state is unknown or unusable, so the pooled connection must
            // not be handed to the next caller.
            _onConnectionFault();
            throw;
        }

        // The session is fatally rejected: 390112 that couldn't be renewed (no master token, or the
        // renewed token was rejected again on retry), or 390114 (master token expired too).
        if (!IsSessionFatal(response)) 
            return response;
        
        _logger.LogDebug("Snowflake session is unrecoverable (code {Code}); faulting the connection.", response.Code);
        _onConnectionFault();

        return response;
    }

    /// <summary>
    /// Posts the query/describe request; on a session-expired response (390112) it renews the
    /// session token with the master token and retries once, rebuilding the request with a fresh
    /// request id so the retry is not treated as a duplicate of the rejected attempt.
    /// </summary>
    private async Task<ApiResponse<SnowflakeQueryResponse>> PostQueryCoreAsync(
        QueryRequest request, bool describeOnly, AuthenticationToken authToken, CancellationToken cancellationToken)
    {
        var body = BuildQueryRequest(request, out string endpoint, describeOnly);
        // The session token this attempt authenticates with; renewal only proceeds if it's still
        // current (so concurrent statements don't each renew after the same expiry).
        string? tokenUsed = authToken.SessionToken;
        var response = await _apiClient.PostAsync<SnowflakeQueryRequestBody, SnowflakeQueryResponse>(
            endpoint, body, authToken, cancellationToken).ConfigureAwait(false);

        if (!IsSessionExpired(response) || string.IsNullOrEmpty(authToken.MasterToken))
            return response;

        _logger.LogDebug("Snowflake session token expired (code {Code}); renewing and retrying.", response.Code);
        await RenewSessionCoreAsync(authToken, renewIfSessionTokenIs: tokenUsed, cancellationToken).ConfigureAwait(false);

        body = BuildQueryRequest(request, out endpoint, describeOnly);
        return await _apiClient.PostAsync<SnowflakeQueryRequestBody, SnowflakeQueryResponse>(
            endpoint, body, authToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when a response indicates the session token has expired (GS code 390112).
    /// </summary>
    internal static bool IsSessionExpired(ApiResponse<SnowflakeQueryResponse> response) =>
        response is { Success: false } && string.Equals(response.Code, SessionExpiredCode, StringComparison.Ordinal);

    /// <summary>
    /// True when a response indicates the session can no longer authenticate requests: the session
    /// token is expired (390112 — fatal here because renewal was either impossible or has already
    /// been attempted) or the master token is expired (390114).
    /// </summary>
    static bool IsSessionFatal(ApiResponse<SnowflakeQueryResponse> response) =>
        response is { Success: false } &&
        (string.Equals(response.Code, SessionExpiredCode, StringComparison.Ordinal) ||
         string.Equals(response.Code, MasterTokenExpiredCode, StringComparison.Ordinal));

    /// <summary>
    /// Renews an expired session token in place via <c>/session/token-request</c>. Snowflake issues
    /// a short-lived session token (~1h) backed by a longer master token (~4h); when the session
    /// token expires the still-valid master token mints a new one. The renewal request is itself
    /// authenticated with the master token.
    /// </summary>
    /// <inheritdoc/>
    public async Task HeartbeatAsync(AuthenticationToken authToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authToken);

        var requestId = Guid.NewGuid().ToString();
        var requestGuid = Guid.NewGuid().ToString();
        var endpoint = $"{_accountUrl}{HeartbeatEndpoint}?requestId={requestId}&request_guid={requestGuid}";

        var response = await _apiClient.PostAsync<EmptyRequestBody, SnowflakeQueryResponse>(
            endpoint, EmptyRequestBody.Instance, authToken, cancellationToken).ConfigureAwait(false);

        // The heartbeat keeps the session alive; if the session token has already expired the
        // heartbeat itself comes back 390112, so renew with the master token.
        if (IsSessionExpired(response))
        {
            if (!string.IsNullOrEmpty(authToken.MasterToken))
                await RenewSessionAsync(authToken, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!response.Success)
            throw new AdbcException($"Snowflake heartbeat failed (code {response.Code ?? "unknown"}).");
    }

    /// <inheritdoc/>
    public Task RenewSessionAsync(AuthenticationToken authToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authToken);
        // Proactive renewal (e.g. heartbeat): renew unconditionally.
        return RenewSessionCoreAsync(authToken, renewIfSessionTokenIs: null, cancellationToken);
    }

    /// <summary>
    /// Renews the session token under a per-connection lock so concurrent statements serialize.
    /// When <paramref name="renewIfSessionTokenIs"/> is non-null, the renewal is skipped if the
    /// session token has already changed (another caller renewed it after the same expiry) — the
    /// caller then just retries with the current token. Mirrors gosnowflake's renewal guard.
    /// </summary>
    private async Task RenewSessionCoreAsync(AuthenticationToken authToken, string? renewIfSessionTokenIs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(authToken.MasterToken))
            throw new AdbcException("Cannot renew the Snowflake session: no master token is available.");

        await _renewLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (renewIfSessionTokenIs != null && authToken.SessionToken != renewIfSessionTokenIs)
                return;

            var requestId = Guid.NewGuid().ToString();
            var requestGuid = Guid.NewGuid().ToString();
            var endpoint = $"{_accountUrl}{TokenRequestEndpoint}?requestId={requestId}&request_guid={requestGuid}";

            var body = new SnowflakeRenewSessionBody { OldSessionToken = authToken.SessionToken };
            // Authenticate the renewal with the master token by carrying it in the auth-header slot.
            var masterAuth = new AuthenticationToken { SessionToken = authToken.MasterToken };

            var response = await _apiClient.PostAsync<SnowflakeRenewSessionBody, SnowflakeRenewSessionData>(
                endpoint, body, masterAuth, cancellationToken).ConfigureAwait(false);

            if (!response.Success || string.IsNullOrEmpty(response.Data?.SessionToken))
            {
                // The server rejected the renewal, so the session cannot authenticate any further
                // requests — flag the pooled connection so it is discarded rather than reused.
                _onConnectionFault();
                throw new AdbcException($"Failed to renew the Snowflake session token (code {response.Code ?? "unknown"}).");
            }

            authToken.SessionToken = response.Data.SessionToken;
            if (!string.IsNullOrEmpty(response.Data.MasterToken))
                authToken.MasterToken = response.Data.MasterToken;
            // Renewal returns fresh session + master validities; roll both ceilings forward.
            if (response.Data.ValidityInSeconds > 0)
                authToken.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.Data.ValidityInSeconds);
            if (response.Data.MasterValidityInSeconds > 0)
                authToken.MasterExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.Data.MasterValidityInSeconds);
        }
        finally
        {
            _renewLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CancelQueryAsync(string requestId, AuthenticationToken authToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(authToken);

        // The abort request carries its own fresh requestId/guid; the running query is identified
        // by the requestId echoed in the body.
        var abortRequestId = Guid.NewGuid().ToString();
        var requestGuid = Guid.NewGuid().ToString();
        var endpoint = $"{_accountUrl}{AbortEndpoint}?requestId={abortRequestId}&request_guid={requestGuid}";

        var body = RequestBuilder.BuildCancelRequest(requestId);
        var response = await _apiClient.PostAsync<SnowflakeCancelRequestBody, SnowflakeQueryResponse>(
            endpoint, body, authToken, cancellationToken).ConfigureAwait(false);

        // A successful abort returns success; if the query already finished there is simply nothing
        // to cancel. Surface other failures so a genuinely broken abort isn't silently swallowed.
        if (!response.Success)
            throw new AdbcException($"Failed to cancel the Snowflake query (code {response.Code ?? "unknown"}).");
    }

}
