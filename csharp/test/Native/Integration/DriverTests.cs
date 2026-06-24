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
using Xunit;
using Xunit.Abstractions;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Tests.Integration;

/// <summary>
/// Driver-level baseline tests for the native Snowflake driver, mirroring the
/// Interop <c>DriverTests</c>. The data-path tests exercise the implemented
/// surface (connect / execute query / execute update). The metadata tests call
/// methods that are currently <see cref="NotImplementedException"/> stubs in the
/// driver; they are expected to FAIL until those methods are implemented, and
/// that red list is the implementation backlog.
///
/// Requires a live Snowflake instance; set SNOWFLAKE_TEST_CONFIG_FILE.
/// </summary>
[Trait("Category", "Integration")]
public class DriverTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IntegrationTestConfiguration _testConfiguration;

    public DriverTests(ITestOutputHelper output)
    {
        _output = output;
        _testConfiguration = IntegrationTestingUtils.TestConfiguration;

        Skip.If(string.IsNullOrEmpty(_testConfiguration.Account),
            $"Cannot execute test configuration from environment variable `{IntegrationTestingUtils.SnowflakeTestConfigVariable}`");
    }

    // ---- Implemented surface (expected to pass) ----

    [SkippableFact]
    public void CanConnect()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());

        Assert.NotNull(connection);
    }

    [SkippableFact]
    public async Task CanExecuteQuery()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = _testConfiguration.Query;
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        Assert.True(batch.Length > 0);
        _output.WriteLine($"Query returned a batch of {batch.Length} rows, {batch.ColumnCount} columns");

        if (_testConfiguration.ExpectedResultsCount > 0)
        {
            Assert.Equal(_testConfiguration.ExpectedResultsCount, batch.Length);
        }
    }

    [SkippableFact]
    public void CanGetQuerySchema()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = _testConfiguration.Query;
        var result = statement.ExecuteQuery();

        Assert.NotNull(result.Stream);
        using var stream = result.Stream;
        int columnCount = stream.Schema.FieldsList.Count;
        _output.WriteLine($"Schema has {columnCount} columns");

        if (_testConfiguration.Metadata.ExpectedColumnCount > 0)
        {
            Assert.Equal(_testConfiguration.Metadata.ExpectedColumnCount, columnCount);
        }
    }

    [SkippableFact]
    public void CanExecuteUpdate()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        string table = string.Format(
            "{0}.{1}.NATIVE_UPD_{2}",
            _testConfiguration.Metadata.Catalog,
            _testConfiguration.Metadata.Schema,
            Guid.NewGuid().ToString("N"));

        statement.SqlQuery = $"CREATE TEMPORARY TABLE {table} (id INT)";
        statement.ExecuteUpdate();

        statement.SqlQuery = $"INSERT INTO {table} (id) VALUES (1), (2)";
        var result = statement.ExecuteUpdate();

        statement.SqlQuery = $"DELETE FROM {table} WHERE id IN (1,2)";
        var result2 = statement.ExecuteUpdate();

        _output.WriteLine($"Insert reported {result.AffectedRows}, delete reported {result2.AffectedRows} affected rows");
        // DML reports the affected-row count parsed from the JSON RowSet
        // (or -1 if a driver/server version cannot determine it). Both statements
        // affect 2 rows while the payload's Returned count is 1.
        Assert.True(result.AffectedRows == 2 || result.AffectedRows == -1);
        Assert.True(result2.AffectedRows == 2 || result2.AffectedRows == -1);
    }

    // ---- Not-yet-implemented surface (expected to FAIL == backlog) ----

    [SkippableFact]
    public void CanGetInfo()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());

        using var stream = connection.GetInfo(new List<AdbcInfoCode>());
        Assert.NotNull(stream);
    }

    [SkippableFact]
    public void CanGetTableTypes()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());

        using var stream = connection.GetTableTypes();
        Assert.NotNull(stream);
    }

    [SkippableFact]
    public void CanGetTableSchema()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());

        Schema schema = connection.GetTableSchema(
            _testConfiguration.Metadata.Catalog,
            _testConfiguration.Metadata.Schema,
            _testConfiguration.Metadata.Table);

        Assert.NotNull(schema);
    }

    [SkippableFact]
    public void CanGetObjectsAll()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());

        using var stream = connection.GetObjects(
            AdbcConnection.GetObjectsDepth.All,
            _testConfiguration.Metadata.Catalog,
            _testConfiguration.Metadata.Schema,
            _testConfiguration.Metadata.Table,
            null,
            null);

        Assert.NotNull(stream);
    }

    public void Dispose()
    {
    }
}
