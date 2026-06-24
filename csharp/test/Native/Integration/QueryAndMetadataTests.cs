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
using Apache.Arrow.Types;

namespace AdbcDrivers.Snowflake.Native.Tests.Integration;

/// <summary>
/// End-to-end tests of the driver's <b>query execution and metadata</b> functionality:
/// running queries, streaming results (including multi-chunk), error propagation, and the
/// metadata methods (GetObjects / GetTableSchema / GetTableTypes / GetInfo).
///
/// These run against the shared, read-only <c>SNOWFLAKE_SAMPLE_DATA</c> (TPC-H SF1) dataset.
/// That dependency is deliberate: these tests need persistent catalog/table structure (for
/// GetObjects) and a large table (to force multi-chunk streaming) — things a session-scoped
/// temporary table can't provide. The dataset's fixed cardinalities/contents also let the
/// tests assert real, stable values rather than just non-null. The sample share is mounted
/// in every account by default, so no setup is required.
///
/// Requires a live Snowflake instance with a usable warehouse; set
/// SNOWFLAKE_TEST_CONFIG_FILE. Each test is a <see cref="SkippableFact"/> that no-ops
/// when no account is configured.
/// </summary>
[Trait("Category", "Integration")]
public class QueryAndMetadataTests
{
    // SNOWFLAKE_SAMPLE_DATA is shared into every account by default and is read-only,
    // so these identifiers and the row counts below are stable reference points.
    private const string SampleDb = "SNOWFLAKE_SAMPLE_DATA";
    private const string SampleSchema = "TPCH_SF1";

    private readonly ITestOutputHelper _output;
    private readonly IntegrationTestConfiguration _testConfiguration;

    public QueryAndMetadataTests(ITestOutputHelper output)
    {
        _output = output;
        _testConfiguration = IntegrationTestingUtils.TestConfiguration;

        Skip.If(string.IsNullOrEmpty(_testConfiguration.Account),
            $"Cannot execute test configuration from environment variable `{IntegrationTestingUtils.SnowflakeTestConfigVariable}`");
    }

    // ---- Data path: known row counts and contents ----

    [SkippableFact]
    public async Task Query_Region_ReturnsFiveKnownRows()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        // TPC-H REGION always has exactly 5 rows with these names.
        statement.SqlQuery = $"SELECT R_NAME FROM {SampleDb}.{SampleSchema}.REGION ORDER BY R_NAME";
        var result = await statement.ExecuteQueryAsync();

        List<string?> names = await ReadStringColumnAsync(result, columnIndex: 0);

        Assert.Equal(5, names.Count);
        Assert.Equal(
            new[] { "AFRICA", "AMERICA", "ASIA", "EUROPE", "MIDDLE EAST" },
            names);
    }

    [SkippableFact]
    public async Task Query_Nation_ReturnsTwentyFiveRowsWithFourColumns()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        statement.SqlQuery = $"SELECT * FROM {SampleDb}.{SampleSchema}.NATION";
        var result = await statement.ExecuteQueryAsync();

        (long rows, int columnCount, _) = await ReadAllAsync(result);

        Assert.Equal(25, rows);
        Assert.Equal(4, columnCount);
    }

    [SkippableFact]
    public async Task Query_Customer_StreamsAllChunks()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        // 150,000 rows in SF1 — large enough to be returned as multiple Arrow chunks,
        // so reaching the exact total proves the chunk-download + streaming path works
        // end to end, not just the first inline batch.
        statement.SqlQuery = $"SELECT C_CUSTKEY FROM {SampleDb}.{SampleSchema}.CUSTOMER";
        var result = await statement.ExecuteQueryAsync();

        (long rows, int columnCount, int batchCount) = await ReadAllAsync(result);
        _output.WriteLine($"CUSTOMER streamed {rows} rows across {batchCount} batch(es)");

        Assert.Equal(150_000, rows);
        Assert.Equal(1, columnCount);
    }

    [SkippableFact]
    public async Task Query_ScaledNumber_DecodesWithScale()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        // Snowflake sends a scaled NUMBER as an integer (999) with the scale in field metadata;
        // the driver must rescale it to a Decimal128 so the real value (9.99) comes through.
        statement.SqlQuery = "SELECT 9.99::NUMBER(10,2) AS P";
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result.Stream);
        using var stream = result.Stream!;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        var column = Assert.IsType<Decimal128Array>(batch!.Column(0));
        Assert.Equal(9.99m, column.GetValue(0));
    }

    [SkippableFact]
    public void Query_InvalidSql_ThrowsAdbcException()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        statement.SqlQuery = $"SELECT * FROM {SampleDb}.{SampleSchema}.NO_SUCH_TABLE_XYZ";

        Assert.Throws<AdbcException>(statement.ExecuteQuery);
    }

    // ---- Metadata: deterministic schema and object content ----

    [SkippableFact]
    public void GetTableSchema_Nation_HasExpectedColumnsAndTypes()
    {
        using var connection = Connect();

        Schema schema = connection.GetTableSchema(SampleDb, SampleSchema, "NATION");

        // The describe path maps Snowflake types through TypeConverter, so the result
        // is deterministic: NUMBER(38,0) -> Int64, VARCHAR -> Utf8 string.
        Assert.Equal(4, schema.FieldsList.Count);

        Assert.Equal("N_NATIONKEY", schema.FieldsList[0].Name);
        Assert.IsType<Int64Type>(schema.FieldsList[0].DataType);

        Assert.Equal("N_NAME", schema.FieldsList[1].Name);
        Assert.IsType<StringType>(schema.FieldsList[1].DataType);

        Assert.Equal("N_REGIONKEY", schema.FieldsList[2].Name);
        Assert.IsType<Int64Type>(schema.FieldsList[2].DataType);

        Assert.Equal("N_COMMENT", schema.FieldsList[3].Name);
        Assert.IsType<StringType>(schema.FieldsList[3].DataType);
    }

    [SkippableFact]
    public async Task GetTableTypes_ReturnsTableAndView()
    {
        using var connection = Connect();

        using var stream = connection.GetTableTypes();
        List<string?> types = await ReadAllStringColumnAsync(stream, columnIndex: 0);

        Assert.Contains("TABLE", types);
        Assert.Contains("VIEW", types);
    }

    [SkippableFact]
    public async Task GetInfo_ReportsSnowflakeVendorName()
    {
        using var connection = Connect();

        using var stream = connection.GetInfo(new List<AdbcInfoCode> { AdbcInfoCode.VendorName });
        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        using (batch)
        {
            var infoValue = (DenseUnionArray)batch.Column(1);
            // All GetInfo values are emitted on the string_value branch (type id 0).
            var stringValues = (StringArray)infoValue.Fields[0];
            Assert.Equal("Snowflake", stringValues.GetString(0));
        }
    }

    [SkippableFact]
    public async Task GetObjects_All_ReturnsNationColumns()
    {
        using var connection = Connect();

        using var stream = connection.GetObjects(
            AdbcConnection.GetObjectsDepth.All,
            SampleDb,
            SampleSchema,
            "NATION",
            null,
            null);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);

        using (batch)
        {
            // catalog_name -> [db_schemas] -> [tables] -> [columns]
            var catalogNames = (StringArray)batch.Column(0);
            int ci = IndexOf(catalogNames, SampleDb);
            Assert.True(ci >= 0, $"{SampleDb} not found in catalog list");

            var dbSchemasList = (ListArray)batch.Column(1);
            var schemas = (StructArray)dbSchemasList.GetSlicedValues(ci);
            var schemaNames = (StringArray)schemas.Fields[0];
            int si = IndexOf(schemaNames, SampleSchema);
            Assert.True(si >= 0, $"{SampleSchema} not found under {SampleDb}");

            var tablesList = (ListArray)schemas.Fields[1];
            var tables = (StructArray)tablesList.GetSlicedValues(si);
            var tableNames = (StringArray)tables.Fields[0];
            var tableTypes = (StringArray)tables.Fields[1];
            int ti = IndexOf(tableNames, "NATION");
            Assert.True(ti >= 0, "NATION not found in table list");
            Assert.Equal("TABLE", tableTypes.GetString(ti));

            var columnsList = (ListArray)tables.Fields[2];
            var columns = (StructArray)columnsList.GetSlicedValues(ti);
            var columnNames = (StringArray)columns.Fields[0];

            var actual = new List<string?>();
            for (int i = 0; i < columnNames.Length; i++)
                actual.Add(columnNames.GetString(i));

            Assert.Equal(4, actual.Count);
            Assert.Contains("N_NATIONKEY", actual);
            Assert.Contains("N_NAME", actual);
            Assert.Contains("N_REGIONKEY", actual);
            Assert.Contains("N_COMMENT", actual);
        }
    }

    [SkippableFact]
    public async Task GetObjects_Catalogs_ContainsSampleDatabase()
    {
        using var connection = Connect();

        using var stream = connection.GetObjects(
            AdbcConnection.GetObjectsDepth.Catalogs,
            SampleDb,
            null,
            null,
            null,
            null);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);

        using (batch)
        {
            var catalogNames = (StringArray)batch.Column(0);
            Assert.True(IndexOf(catalogNames, SampleDb) >= 0);
        }
    }

    [SkippableFact]
    public async Task GetObjects_MaliciousCatalogPattern_IsTreatedAsLiteralNotSql()
    {
        using var connection = Connect();

        // If the pattern were interpolated into SQL, the trailing "OR DATABASE_NAME='...'"
        // would break out of the ILIKE literal and the real sample DB would come back. With
        // server-side binding it is a literal pattern that matches no database, so the
        // catalog list must be empty -- and must NOT contain SNOWFLAKE_SAMPLE_DATA.
        string payload = $"x' OR DATABASE_NAME='{SampleDb}";

        using var stream = connection.GetObjects(
            AdbcConnection.GetObjectsDepth.Catalogs,
            payload,
            null,
            null,
            null,
            null);

        RecordBatch? batch = await stream.ReadNextRecordBatchAsync();
        Assert.NotNull(batch);

        using (batch)
        {
            var catalogNames = (StringArray)batch.Column(0);
            _output.WriteLine($"Injection payload returned {catalogNames.Length} catalog(s)");
            Assert.True(IndexOf(catalogNames, SampleDb) < 0,
                "SQL injection succeeded: the malicious pattern returned the real database.");
            Assert.Equal(0, catalogNames.Length);
        }
    }

    // ---- Helpers ----

    private SnowflakeConnection Connect()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        var database = driver.Open(parameters);
        var connection = database.Connect(new Dictionary<string, string>());
        return (SnowflakeConnection)connection;
    }

    /// <summary>Reads the whole result stream, returning total rows, column count, and batch count.</summary>
    private static async Task<(long Rows, int ColumnCount, int BatchCount)> ReadAllAsync(Apache.Arrow.Adbc.QueryResult result)
    {
        Assert.NotNull(result.Stream);
        long total = 0;
        int columnCount = 0;
        int batchCount = 0;

        using var stream = result.Stream;
        while (await stream.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                columnCount = batch.ColumnCount;
                total += batch.Length;
                batchCount++;
            }
        }

        return (total, columnCount, batchCount);
    }

    private static async Task<List<string?>> ReadStringColumnAsync(Apache.Arrow.Adbc.QueryResult result, int columnIndex)
    {
        Assert.NotNull(result.Stream);
        using var stream = result.Stream;
        return await ReadAllStringColumnAsync(stream, columnIndex);
    }

    private static async Task<List<string?>> ReadAllStringColumnAsync(Apache.Arrow.Ipc.IArrowArrayStream stream, int columnIndex)
    {
        var values = new List<string?>();
        while (await stream.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var column = (StringArray)batch.Column(columnIndex);
                for (int i = 0; i < column.Length; i++)
                    values.Add(column.GetString(i));
            }
        }

        return values;
    }

    private static int IndexOf(StringArray array, string value)
    {
        for (int i = 0; i < array.Length; i++)
        {
            if (string.Equals(array.GetString(i), value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
