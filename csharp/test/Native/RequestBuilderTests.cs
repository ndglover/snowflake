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
using AdbcDrivers.Snowflake.Native.Services.Transport;
using Xunit;

namespace AdbcDrivers.Snowflake.Native.Tests;

/// <summary>
/// Offline unit tests for <see cref="RequestBuilder"/> request-body construction.
/// </summary>
public class RequestBuilderTests
{
    private static Dictionary<string, object> BuildQuery(
        string statement,
        string? database = null,
        string? schema = null,
        string? warehouse = null,
        string? role = null,
        int? timeout = null,
        Dictionary<string, object>? parameters = null,
        bool isMultiStatement = false,
        bool describeOnly = false)
        => (Dictionary<string, object>)RequestBuilder.BuildQueryRequest(
            statement, database, schema, warehouse, role, timeout, parameters, isMultiStatement, describeOnly);

    [Fact]
    public void BuildQueryRequest_SetsCoreFieldsAndRequestsArrow()
    {
        Dictionary<string, object> request = BuildQuery("SELECT 1");

        Assert.Equal("SELECT 1", request["sqlText"]);
        Assert.False((bool)request["asyncExec"]);
        Assert.False((bool)request["describeOnly"]);
        Assert.False(request.ContainsKey("bindings"));

        var parameters = (Dictionary<string, string>)request["parameters"];
        Assert.Equal("ARROW", parameters["DOTNET_QUERY_RESULT_FORMAT"]);
    }

    [Fact]
    public void BuildQueryRequest_IncludesSessionParameters()
    {
        Dictionary<string, object> request = BuildQuery(
            "SELECT 1", database: "DB", schema: "SC", warehouse: "WH", role: "R", timeout: 30);

        var parameters = (Dictionary<string, string>)request["parameters"];
        Assert.Equal("DB", parameters["DATABASE"]);
        Assert.Equal("SC", parameters["SCHEMA"]);
        Assert.Equal("WH", parameters["WAREHOUSE"]);
        Assert.Equal("R", parameters["ROLE"]);
        Assert.Equal("30", parameters["STATEMENT_TIMEOUT_IN_SECONDS"]);
    }

    [Fact]
    public void BuildQueryRequest_DescribeOnly_SetsFlag()
    {
        Dictionary<string, object> request = BuildQuery("SELECT 1", describeOnly: true);
        Assert.True((bool)request["describeOnly"]);
    }

    [Fact]
    public void BuildQueryRequest_WithBindings_IncludesThem()
    {
        var bindings = new Dictionary<string, object> { ["1"] = "x" };
        Dictionary<string, object> request = BuildQuery("SELECT ?", parameters: bindings);

        Assert.True(request.ContainsKey("bindings"));
        Assert.Same(bindings, request["bindings"]);
    }

    [Fact]
    public void BuildQueryRequest_MultiStatement_SetsCount()
    {
        Dictionary<string, object> request = BuildQuery("SELECT 1; SELECT 2", isMultiStatement: true);
        var parameters = (Dictionary<string, string>)request["parameters"];
        Assert.Equal("0", parameters["MULTI_STATEMENT_COUNT"]);
    }

    [Fact]
    public void BuildQueryRequest_EmptyStatement_Throws()
    {
        Assert.Throws<ArgumentException>(() => RequestBuilder.BuildQueryRequest(string.Empty));
    }

    [Fact]
    public void BuildCancelRequest_SetsQueryId()
    {
        var request = (Dictionary<string, object>)RequestBuilder.BuildCancelRequest("query-123");
        Assert.Equal("query-123", request["queryId"]);
    }

    [Fact]
    public void BuildCancelRequest_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => RequestBuilder.BuildCancelRequest(string.Empty));
    }

    [Fact]
    public void BuildMetadataRequest_SetsTypeAndFilters()
    {
        var request = (Dictionary<string, object>)RequestBuilder.BuildMetadataRequest(
            "tables", databasePattern: "DB", schemaPattern: "SC", tablePattern: "T",
            columnPattern: "C", tableTypes: new[] { "TABLE", "VIEW" });

        Assert.Equal("tables", request["type"]);
        Assert.Equal("DB", request["database"]);
        Assert.Equal("SC", request["schema"]);
        Assert.Equal("T", request["table"]);
        Assert.Equal("C", request["column"]);
        Assert.Equal(new[] { "TABLE", "VIEW" }, (string[])request["tableTypes"]);
    }

    [Fact]
    public void BuildMetadataRequest_EmptyType_Throws()
    {
        Assert.Throws<ArgumentException>(() => RequestBuilder.BuildMetadataRequest(string.Empty));
    }
}
