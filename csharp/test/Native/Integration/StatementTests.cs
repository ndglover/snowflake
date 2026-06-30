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
using Xunit;
using Xunit.Abstractions;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Tests.Integration;

/// <summary>
/// Statement-level baseline tests for the native Snowflake driver, mirroring the
/// Interop <c>StatementTests</c>. Covers the implemented statement surface:
/// execute query, execute update, prepare, and parameter-schema guarding.
///
/// Requires a live Snowflake instance; set SNOWFLAKE_TEST_CONFIG_FILE.
/// </summary>
[Trait("Category", "Integration")]
public class StatementTests
{
    private readonly ITestOutputHelper _output;
    private readonly IntegrationTestConfiguration _testConfiguration;

    public StatementTests(ITestOutputHelper output)
    {
        _output = output;
        _testConfiguration = IntegrationTestingUtils.TestConfiguration;

        Skip.If(string.IsNullOrEmpty(_testConfiguration.Account),
            $"Cannot execute test configuration from environment variable `{IntegrationTestingUtils.SnowflakeTestConfigVariable}`");
    }

    [SkippableFact]
    public async Task CanExecuteQuery()
    {
        // Given a simple two-column query
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1 AS X, 'two' AS Y";

        // When it is executed and the first batch is read
        var result = await statement.ExecuteQueryAsync();
        Assert.NotNull(result.Stream);
        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();

        // Then the batch has the expected shape
        Assert.NotNull(batch);
        Assert.Equal(2, batch.ColumnCount);
        Assert.Equal(1, batch.Length);
    }

    [SkippableFact]
    public void ExecuteUpdateOnSelectReturnsNoRowCount()
    {
        // Given a statement holding a SELECT (which affects no rows)
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1";

        // When it is run as an update
        var result = statement.ExecuteUpdate();

        // Then the driver reports -1 (unknown) per the ADBC contract
        _output.WriteLine($"ExecuteUpdate on SELECT reported {result.AffectedRows} affected rows");
        Assert.Equal(-1, result.AffectedRows);
    }

    [SkippableFact]
    public void GetParameterSchema_IsNotSupported()
    {
        // Given a fresh statement
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        // When / Then GetParameterSchema throws: Snowflake's protocol does not report
        // bind-parameter types, so it is unsupported regardless of whether Prepare was called.
        Assert.Throws<NotImplementedException>(statement.GetParameterSchema);
    }

    [SkippableFact]
    public async Task CanPrepareAndExecute()
    {
        // Given a prepared statement
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT 1 AS X";
        statement.Prepare();

        // When it is executed and the first batch is read
        var result = await statement.ExecuteQueryAsync();
        Assert.NotNull(result.Stream);
        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();

        // Then it returns the single row
        Assert.NotNull(batch);
        Assert.Equal(1, batch.Length);
    }

    [SkippableTheory]
    [MemberData(nameof(BindCases.LiveNames), MemberType = typeof(BindCases))]
    public Task CanBindParameter(string caseName)
    {
        // Each bindable Arrow type from the shared BindCases table, bound and compared by the
        // real server. The row returns only if Snowflake accepted the wire format and the value
        // matched the predicate, so this confirms the encoding end to end.
        var bindCase = BindCases.Get(caseName);
        return AssertBoundValueMatches(bindCase.LivePredicate!, bindCase.BuildArray());
    }

    /// <summary>
    /// Binds one parameter and runs <c>SELECT 'ok' WHERE &lt;predicate&gt;</c>: the row only returns
    /// if Snowflake accepted the bind wire format and the bound value compared equal. The bind field
    /// is derived from the array's own type, so a case only has to supply the array and predicate.
    /// </summary>
    private async Task AssertBoundValueMatches(string predicate, IArrowArray value)
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();
        statement.SqlQuery = $"SELECT 'ok' AS V WHERE {predicate}";
        var schema = new Schema([new Field("p", value.Data.DataType, true)], null);
        using var batch = new RecordBatch(schema, [value], value.Length);

        statement.Bind(batch, schema);
        var result = await statement.ExecuteQueryAsync();
        Assert.NotNull(result.Stream);
        using var stream = result.Stream!;
        var read = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(read);
        Assert.Equal(1, read!.Length);
        Assert.Equal("ok", ((StringArray)read.Column(0)).GetString(0));
    }

    [SkippableFact]
    public async Task CanCancelRunningQuery()
    {
        // Given a query that runs long enough to be cancelled mid-flight
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT SYSTEM$WAIT(10)";

        // When it is started on a background thread and cancelled after it has begun running
        var queryTask = Task.Run(statement.ExecuteQuery);
        await Task.Delay(1000);
        statement.Cancel();

        // Then the running query terminates with a Snowflake cancellation error instead of completing
        var ex = await Assert.ThrowsAsync<AdbcException>(async () => await queryTask);
        _output.WriteLine($"Cancelled query failed with: {ex.Message}");
        Assert.Contains("cancel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void ExecuteUpdateOnDmlReturnsAffectedRowCount()
    {
        // Given a temporary table (requires a writable metadata.catalog / metadata.schema)
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

        // When two rows are inserted and then deleted
        statement.SqlQuery = $"INSERT INTO {table} (id) VALUES (1), (2)";
        var result = statement.ExecuteUpdate();
        statement.SqlQuery = $"DELETE FROM {table} WHERE id IN (1,2)";
        var result2 = statement.ExecuteUpdate();

        // Then each reports its affected-row count, parsed from the JSON RowSet (or -1 if a
        // driver/server version cannot determine it). Both affect 2 rows while the payload's
        // Returned count is 1.
        _output.WriteLine($"Insert reported {result.AffectedRows}, delete reported {result2.AffectedRows} affected rows");
        Assert.True(result.AffectedRows == 2 || result.AffectedRows == -1);
        Assert.True(result2.AffectedRows == 2 || result2.AffectedRows == -1);
    }
}
