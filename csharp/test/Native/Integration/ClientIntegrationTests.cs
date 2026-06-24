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
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

using Apache.Arrow;

namespace AdbcDrivers.Snowflake.Native.Tests.Integration;

/// <summary>
/// Integration tests that execute real queries against Snowflake.
/// </summary>
[Trait("Category", "Integration")]
public class ClientIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly IntegrationTestConfiguration _testConfiguration;

    public ClientIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testConfiguration = IntegrationTestingUtils.TestConfiguration;

        Skip.If(
            string.IsNullOrWhiteSpace(_testConfiguration.Account),
            $"Cannot execute test configuration from environment variable `{IntegrationTestingUtils.SnowflakeTestConfigVariable}`");
    }

    // Note: the parameterized query-throughput benchmark lives in BenchmarkTests
    // (BaselineQueryPerformance), mirroring the Interop project's BenchmarkTests so the
    // native and Go drivers can be compared directly.

    [SkippableFact]
    public async Task CanExecuteConfiguredQueryAsync()
    {
        Skip.If(string.IsNullOrWhiteSpace(_testConfiguration.Query), "No query configured in test configuration");

        var (totalRows, batchCount, schema, elapsed) = await ExecuteQueryAndReadResultsAsync(_testConfiguration.Query!);

        _output.WriteLine($"Query: {_testConfiguration.Query}");
        _output.WriteLine($"Fetched {totalRows} rows in {batchCount} batches with {schema.FieldsList.Count} columns in {elapsed.TotalMilliseconds:F0} ms.");

        foreach (var field in schema.FieldsList)
        {
            _output.WriteLine($"  {field.Name}: {field.DataType}");
        }
    }

    private async Task<(long TotalRows, int BatchCount, Schema Schema, System.TimeSpan Elapsed)> ExecuteQueryAndReadResultsAsync(string query)
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);

        using var database = driver.Open(parameters);
        using var connection = database.Connect(new Dictionary<string, string>());
        using var statement = connection.CreateStatement();

        statement.SqlQuery = query;
        var stopwatch = Stopwatch.StartNew();

        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result);
        Assert.NotNull(result.Stream);

        using var stream = result.Stream;
        var schema = stream.Schema;
        long totalRows = 0;
        int batchCount = 0;

        Assert.NotNull(schema);
        Assert.True(schema.FieldsList.Count > 0);

        RecordBatch? batch;
        while ((batch = await stream.ReadNextRecordBatchAsync()) != null)
        {
            using (batch)
            {
                batchCount++;
                totalRows += batch.Length;
            }
        }

        stopwatch.Stop();

        Assert.True(batchCount > 0, "Expected at least one batch");
        Assert.True(totalRows > 0, "Expected at least one row");

        return (totalRows, batchCount, schema, stopwatch.Elapsed);
    }
}
