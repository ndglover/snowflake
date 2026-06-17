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

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Transport;

/// <summary>
/// Builds requests for Snowflake SQL API.
/// </summary>
internal class RequestBuilder
{
    /// <summary>
    /// Builds a query execution request.
    /// </summary>
    /// <param name="statement">The SQL statement to execute.</param>
    /// <param name="database">The database name (optional).</param>
    /// <param name="schema">The schema name (optional).</param>
    /// <param name="warehouse">The warehouse name (optional).</param>
    /// <param name="role">The role name (optional).</param>
    /// <param name="timeout">The query timeout in seconds (optional).</param>
    /// <param name="parameters">Query parameters (optional).</param>
    /// <param name="isMultiStatement">Whether this is a multi-statement query.</param>
    /// <param name="describeOnly">Whether to only describe (compile) the statement and return its metadata without executing it.</param>
    /// <returns>A query execution request object.</returns>
    public static object BuildQueryRequest(
        string statement,
        string? database = null,
        string? schema = null,
        string? warehouse = null,
        string? role = null,
        int? timeout = null,
        Dictionary<string, object>? parameters = null,
        bool isMultiStatement = false,
        bool describeOnly = false)
    {
        if (string.IsNullOrEmpty(statement))
            throw new ArgumentException("Statement cannot be null or empty.", nameof(statement));

        // Snowflake v1 API format (reference: snowflake-connector-net).
        // describeOnly=true compiles the statement and returns its result metadata
        // (rowtype) without executing it -- this is how the internal protocol "prepares".
        var request = new Dictionary<string, object>
        {
            ["sqlText"] = statement,
            ["asyncExec"] = false,
            ["describeOnly"] = describeOnly
        };

        // Build parameters dictionary for session-level settings
        var sessionParams = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(database))
            sessionParams["DATABASE"] = database;

        if (!string.IsNullOrEmpty(schema))
            sessionParams["SCHEMA"] = schema;

        if (!string.IsNullOrEmpty(warehouse))
            sessionParams["WAREHOUSE"] = warehouse;

        if (!string.IsNullOrEmpty(role))
            sessionParams["ROLE"] = role;

        if (timeout.HasValue && timeout.Value > 0)
            sessionParams["STATEMENT_TIMEOUT_IN_SECONDS"] = timeout.Value.ToString();

        // Request Arrow format for query results
        sessionParams["DOTNET_QUERY_RESULT_FORMAT"] = "ARROW";

        if (isMultiStatement)
            sessionParams["MULTI_STATEMENT_COUNT"] = "0"; // 0 means variable number of statements

        if (sessionParams.Count > 0)
            request["parameters"] = sessionParams;

        if (parameters != null && parameters.Count > 0)
        {
            request["bindings"] = parameters;
        }

        return request;
    }

    /// <summary>
    /// Builds a query cancellation request.
    /// </summary>
    /// <param name="queryId">The query ID to cancel.</param>
    /// <returns>A query cancellation request object.</returns>
    public static object BuildCancelRequest(string queryId)
    {
        if (string.IsNullOrEmpty(queryId))
            throw new ArgumentException("Query ID cannot be null or empty.", nameof(queryId));

        return new Dictionary<string, object>
        {
            ["queryId"] = queryId
        };
    }

    /// <summary>
    /// Builds a metadata request.
    /// </summary>
    /// <param name="metadataType">The type of metadata to retrieve.</param>
    /// <param name="databasePattern">The database pattern filter (optional).</param>
    /// <param name="schemaPattern">The schema pattern filter (optional).</param>
    /// <param name="tablePattern">The table pattern filter (optional).</param>
    /// <param name="columnPattern">The column pattern filter (optional).</param>
    /// <param name="tableTypes">The table types filter (optional).</param>
    /// <returns>A metadata request object.</returns>
    public static object BuildMetadataRequest(
        string metadataType,
        string? databasePattern = null,
        string? schemaPattern = null,
        string? tablePattern = null,
        string? columnPattern = null,
        string[]? tableTypes = null)
    {
        if (string.IsNullOrEmpty(metadataType))
            throw new ArgumentException("Metadata type cannot be null or empty.", nameof(metadataType));

        var request = new Dictionary<string, object>
        {
            ["type"] = metadataType
        };

        if (!string.IsNullOrEmpty(databasePattern))
            request["database"] = databasePattern;

        if (!string.IsNullOrEmpty(schemaPattern))
            request["schema"] = schemaPattern;

        if (!string.IsNullOrEmpty(tablePattern))
            request["table"] = tablePattern;

        if (!string.IsNullOrEmpty(columnPattern))
            request["column"] = columnPattern;

        if (tableTypes != null && tableTypes.Length > 0)
            request["tableTypes"] = tableTypes;

        return request;
    }
}
