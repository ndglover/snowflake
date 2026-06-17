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

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Query;

internal sealed class SnowflakeQueryResponse
{
    [JsonPropertyName("queryId")]
    public string? QueryId { get; set; }

    [JsonPropertyName("sqlState")]
    public string? SqlState { get; set; }

    [JsonPropertyName("rowtype")]
    public List<RowType>? RowType { get; set; }

    [JsonPropertyName("rowset")]
    public List<List<string>>? RowSet { get; set; }

    [JsonPropertyName("rowsetBase64")]
    public string? RowSetBase64 { get; set; }

    [JsonPropertyName("queryResultFormat")]
    public string? QueryResultFormat { get; set; }

    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("returned")]
    public long? Returned { get; set; }

    [JsonPropertyName("parameters")]
    public List<NameValueParameter>? Parameters { get; set; }

    [JsonPropertyName("chunks")]
    public List<ChunkInfo>? Chunks { get; set; }

    [JsonPropertyName("chunkHeaders")]
    public Dictionary<string, string>? ChunkHeaders { get; set; }

    [JsonPropertyName("qrmk")]
    public string? Qrmk { get; set; }
}

internal sealed class ChunkInfo
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("uncompressedSize")]
    public int UncompressedSize { get; set; }

    [JsonPropertyName("compressedSize")]
    public int CompressedSize { get; set; }
}

internal sealed class RowType
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("length")]
    public int? Length { get; set; }

    [JsonPropertyName("precision")]
    public int? Precision { get; set; }

    [JsonPropertyName("scale")]
    public int? Scale { get; set; }

    [JsonPropertyName("nullable")]
    public bool? Nullable { get; set; }
}

internal sealed class NameValueParameter
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}
