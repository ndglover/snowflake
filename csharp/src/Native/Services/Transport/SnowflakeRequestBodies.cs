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

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AdbcDrivers.Snowflake.Native.Services.Transport;

/// <summary>
/// Request body for the query-execution endpoint. <c>describeOnly</c> compiles the statement
/// and returns its result metadata (rowtype) without executing it.
/// </summary>
internal sealed class SnowflakeQueryRequestBody
{
    [JsonPropertyName("sqlText")]
    public required string SqlText { get; init; }

    [JsonPropertyName("asyncExec")]
    public bool AsyncExec { get; init; }

    [JsonPropertyName("describeOnly")]
    public bool DescribeOnly { get; init; }

    /// <summary>Session-level settings (see <see cref="SessionParameterNames"/>).</summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Parameters { get; init; }

    /// <summary>Positional bind variables keyed "1", "2", ... matching the '?' placeholders.</summary>
    [JsonPropertyName("bindings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, SnowflakeBinding>? Bindings { get; init; }
}

/// <summary>
/// A single bound parameter value with its Snowflake data type (see <see cref="BindTypeNames"/>).
/// </summary>
internal sealed class SnowflakeBinding
{
    public SnowflakeBinding(string type, object? value)
    {
        Type = type;
        Value = value;
    }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("value")]
    public object? Value { get; }
}

/// <summary>
/// Request body for cancelling a running query.
/// </summary>
internal sealed class SnowflakeCancelRequestBody
{
    [JsonPropertyName("queryId")]
    public required string QueryId { get; init; }
}

/// <summary>
/// Request body for a metadata listing request.
/// </summary>
internal sealed class SnowflakeMetadataRequestBody
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("database")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Database { get; init; }

    [JsonPropertyName("schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Schema { get; init; }

    [JsonPropertyName("table")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Table { get; init; }

    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Column { get; init; }

    [JsonPropertyName("tableTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? TableTypes { get; init; }
}
