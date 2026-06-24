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
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native;
using Apache.Arrow.Ipc;
using Xunit;
using Xunit.Abstractions;

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.Tests;

namespace AdbcDrivers.Snowflake.Native.Tests.Integration;

/// <summary>
/// Integration tests for the native Snowflake ADBC driver.
/// These tests require a real Snowflake instance and credentials.
/// 
/// Set the SNOWFLAKE_TEST_CONFIG_FILE environment variable to point to a JSON config file.
/// The config file should use the same format as the Interop Snowflake tests.
/// </summary>
[Trait("Category", "Integration")]
public class ClientTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IntegrationTestConfiguration _testConfiguration;

    public ClientTests(ITestOutputHelper output)
    {
        _output = output;
        _testConfiguration = IntegrationTestingUtils.TestConfiguration;

        Skip.If(string.IsNullOrEmpty(_testConfiguration.Account),
            $"Cannot execute test configuration from environment variable `{IntegrationTestingUtils.SnowflakeTestConfigVariable}`");
    }

    [SkippableFact]
    public async Task CanClientExecuteQuery()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = _testConfiguration.Query ?? "SELECT 1 as TESTCOL";
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        Assert.True(batch.Length > 0);

        _output.WriteLine($"Query returned {batch.Length} rows with {batch.ColumnCount} columns");
    }

    [SkippableFact]
    public async Task CanClientExecuteQueryAsync()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using SnowflakeDatabase database = (SnowflakeDatabase)driver.Open(parameters);
        using var connection = await database.ConnectAsync(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = _testConfiguration.Query ?? "SELECT 1 as TESTCOL";
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        Assert.True(batch.Length > 0);

        _output.WriteLine($"Query returned {batch.Length} rows with {batch.ColumnCount} columns");
    }

    [SkippableFact]
    public async Task CanClientExecuteQuery_WithAsyncConnect()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using SnowflakeDatabase database = (SnowflakeDatabase)driver.Open(parameters);
        using var connection = await database.ConnectAsync(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = _testConfiguration.Query ?? "SELECT 1 as TESTCOL";
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        Assert.True(batch.Length > 0);

        _output.WriteLine($"Query returned {batch.Length} rows with {batch.ColumnCount} columns");
    }

    [SkippableFact]
    public void CanClientExecuteUpdate()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        // Simple query that doesn't modify data
        statement.SqlQuery = "SELECT 1";
        var result = statement.ExecuteUpdate();

        Assert.NotNull(result);
        // ExecuteUpdate returns -1 for SELECT statements
        Assert.True(result.AffectedRows >= -1);

        _output.WriteLine($"Update affected {result.AffectedRows} rows");
    }

    [SkippableFact]
    public async Task CanClientExecuteUpdate_WithAsyncConnect()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using SnowflakeDatabase database = (SnowflakeDatabase)driver.Open(parameters);
        using var connection = await database.ConnectAsync(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        // Simple query that doesn't modify data
        statement.SqlQuery = "SELECT 1";
        var result = statement.ExecuteUpdate();

        Assert.NotNull(result);
        // ExecuteUpdate returns -1 for SELECT statements
        Assert.True(result.AffectedRows >= -1);

        _output.WriteLine($"Update affected {result.AffectedRows} rows");
    }

    [SkippableFact]
    public void CanClientGetSchema()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = _testConfiguration.Query ?? "SELECT 1 as TESTCOL";
        var result = statement.ExecuteQuery();

        Assert.NotNull(result);
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var schema = stream.Schema;

        Assert.NotNull(schema);
        Assert.True(schema.FieldsList.Count > 0);

        _output.WriteLine($"Schema has {schema.FieldsList.Count} fields");
        foreach (var field in schema.FieldsList)
        {
            _output.WriteLine($"  {field.Name}: {field.DataType}");
        }
    }

    [SkippableFact]
    public async Task CanClientGetSchema_WithAsyncConnect()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using SnowflakeDatabase database = (SnowflakeDatabase)driver.Open(parameters);
        using var connection = await database.ConnectAsync(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = _testConfiguration.Query ?? "SELECT 1 as TESTCOL";
        var result = statement.ExecuteQuery();

        Assert.NotNull(result);
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var schema = stream.Schema;

        Assert.NotNull(schema);
        Assert.True(schema.FieldsList.Count > 0);

        _output.WriteLine($"Schema has {schema.FieldsList.Count} fields");
        foreach (var field in schema.FieldsList)
        {
            _output.WriteLine($"  {field.Name}: {field.DataType}");
        }
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}
