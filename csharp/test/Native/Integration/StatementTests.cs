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
/// Statement-level baseline tests for the native Snowflake driver, mirroring the
/// Interop <c>StatementTests</c>. Covers the implemented statement surface:
/// execute query, execute update, prepare, and parameter-schema guarding.
///
/// Requires a live Snowflake instance; set SNOWFLAKE_TEST_CONFIG_FILE.
/// </summary>
[Trait("Category", "Integration")]
public class StatementTests : IDisposable
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
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = "SELECT 1 AS X, 'two' AS Y";
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result.Stream);
        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        Assert.Equal(2, batch.ColumnCount);
        Assert.Equal(1, batch.Length);
    }

    [SkippableFact]
    public void ExecuteUpdateOnSelectReturnsNoRowCount()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = "SELECT 1";
        var result = statement.ExecuteUpdate();

        // A SELECT affects no rows; the driver reports -1 (unknown) per the ADBC contract.
        _output.WriteLine($"ExecuteUpdate on SELECT reported {result.AffectedRows} affected rows");
        Assert.Equal(-1, result.AffectedRows);
    }

    [SkippableFact]
    public void GetParameterSchema_IsNotSupported()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        // Snowflake's protocol does not report bind-parameter types, so GetParameterSchema
        // is unsupported regardless of whether the statement has been prepared.
        Assert.Throws<NotImplementedException>(statement.GetParameterSchema);
    }

    [SkippableFact]
    public async Task CanPrepareAndExecute()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = "SELECT 1 AS X";
        statement.Prepare();

        var result = await statement.ExecuteQueryAsync();
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);
        Assert.Equal(1, batch.Length);
    }

    public void Dispose()
    {
    }
}
