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
using System.Linq;
using AdbcDrivers.Snowflake.Native;
using Xunit;
using Xunit.Abstractions;

using Apache.Arrow;
using Apache.Arrow.Adbc;
using Apache.Arrow.Types;

namespace AdbcDrivers.Snowflake.Native.Tests;

/// <summary>
/// Content-asserting integration tests against Snowflake's shared, read-only
/// <c>SNOWFLAKE_SAMPLE_DATA</c> database (the TPC-H SF1 schema). Unlike the baseline
/// <see cref="DriverTests"/> (which mostly assert non-null), these verify real, stable
/// values — the TPC-H reference data has fixed cardinalities and contents in every
/// account, so the assertions below hold anywhere the sample share is mounted (the
/// default). This is the closest we get to "verify for real".
///
/// Requires a live Snowflake instance with a usable warehouse; set
/// SNOWFLAKE_TEST_CONFIG_FILE. Each test is a <see cref="SkippableFact"/> that no-ops
/// when no account is configured.
/// </summary>
public class SampleDataTests : IDisposable
{
    // SNOWFLAKE_SAMPLE_DATA is shared into every account by default and is read-only,
    // so these identifiers and the row counts below are stable reference points.
    private const string SampleDb = "SNOWFLAKE_SAMPLE_DATA";
    private const string SampleSchema = "TPCH_SF1";

    private readonly ITestOutputHelper _output;
    private readonly SnowflakeTestConfiguration _testConfiguration;

    public SampleDataTests(ITestOutputHelper output)
    {
        _output = output;
        _testConfiguration = SnowflakeTestingUtils.TestConfiguration;

        Skip.If(string.IsNullOrEmpty(_testConfiguration.Account),
            $"Cannot execute test configuration from environment variable `{SnowflakeTestingUtils.SnowflakeTestConfigVariable}`");
    }

    // ---- Data path: known row counts and contents ----

    [SkippableFact]
    public void Query_Region_ReturnsFiveKnownRows()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        // TPC-H REGION always has exactly 5 rows with these names.
        statement.SqlQuery = $"SELECT R_NAME FROM {SampleDb}.{SampleSchema}.REGION ORDER BY R_NAME";
        var result = statement.ExecuteQuery();

        List<string?> names = ReadStringColumn(result, columnIndex: 0);

        Assert.Equal(5, names.Count);
        Assert.Equal(
            new[] { "AFRICA", "AMERICA", "ASIA", "EUROPE", "MIDDLE EAST" },
            names);
    }

    [SkippableFact]
    public void Query_Nation_ReturnsTwentyFiveRowsWithFourColumns()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        statement.SqlQuery = $"SELECT * FROM {SampleDb}.{SampleSchema}.NATION";
        var result = statement.ExecuteQuery();

        long rows = SumRows(result, out int columnCount);

        Assert.Equal(25, rows);
        Assert.Equal(4, columnCount);
    }

    [SkippableFact]
    public void Query_Customer_StreamsAllChunks()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        // 150,000 rows in SF1 — large enough to be returned as multiple Arrow chunks,
        // so reaching the exact total proves the chunk-download + streaming path works
        // end to end, not just the first inline batch.
        statement.SqlQuery = $"SELECT C_CUSTKEY FROM {SampleDb}.{SampleSchema}.CUSTOMER";
        var result = statement.ExecuteQuery();

        long rows = SumRows(result, out int columnCount, out int batchCount);
        _output.WriteLine($"CUSTOMER streamed {rows} rows across {batchCount} batch(es)");

        Assert.Equal(150_000, rows);
        Assert.Equal(1, columnCount);
    }

    [SkippableFact]
    public void Query_InvalidSql_ThrowsAdbcException()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        statement.SqlQuery = $"SELECT * FROM {SampleDb}.{SampleSchema}.NO_SUCH_TABLE_XYZ";

        Assert.Throws<AdbcException>(() => statement.ExecuteQuery());
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
    public void GetTableTypes_ReturnsTableAndView()
    {
        using var connection = Connect();

        using var stream = connection.GetTableTypes();
        List<string?> types = ReadAllStringColumn(stream, columnIndex: 0);

        Assert.Contains("TABLE", types);
        Assert.Contains("VIEW", types);
    }

    [SkippableFact]
    public void GetInfo_ReportsSnowflakeVendorName()
    {
        using var connection = Connect();

        using var stream = connection.GetInfo(new List<AdbcInfoCode> { AdbcInfoCode.VendorName });
        RecordBatch? batch = stream.ReadNextRecordBatchAsync().Result;

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
    public void GetObjects_All_ReturnsNationColumns()
    {
        using var connection = Connect();

        using var stream = connection.GetObjects(
            AdbcConnection.GetObjectsDepth.All,
            SampleDb,
            SampleSchema,
            "NATION",
            null,
            null);

        RecordBatch? batch = stream.ReadNextRecordBatchAsync().Result;
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
    public void GetObjects_Catalogs_ContainsSampleDatabase()
    {
        using var connection = Connect();

        using var stream = connection.GetObjects(
            AdbcConnection.GetObjectsDepth.Catalogs,
            SampleDb,
            null,
            null,
            null,
            null);

        RecordBatch? batch = stream.ReadNextRecordBatchAsync().Result;
        Assert.NotNull(batch);

        using (batch)
        {
            var catalogNames = (StringArray)batch.Column(0);
            Assert.True(IndexOf(catalogNames, SampleDb) >= 0);
        }
    }

    // ---- Helpers ----

    private SnowflakeConnection Connect()
    {
        var driver = SnowflakeTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        var database = driver.Open(parameters);
        var connection = database.Connect(new Dictionary<string, string>());
        return (SnowflakeConnection)connection;
    }

    private static long SumRows(Apache.Arrow.Adbc.QueryResult result, out int columnCount) =>
        SumRows(result, out columnCount, out _);

    private static long SumRows(Apache.Arrow.Adbc.QueryResult result, out int columnCount, out int batchCount)
    {
        Assert.NotNull(result.Stream);
        long total = 0;
        columnCount = 0;
        batchCount = 0;

        using var stream = result.Stream;
        while (true)
        {
            RecordBatch? batch = stream.ReadNextRecordBatchAsync().Result;
            if (batch == null)
                break;

            using (batch)
            {
                columnCount = batch.ColumnCount;
                total += batch.Length;
                batchCount++;
            }
        }

        return total;
    }

    private static List<string?> ReadStringColumn(Apache.Arrow.Adbc.QueryResult result, int columnIndex)
    {
        Assert.NotNull(result.Stream);
        using var stream = result.Stream;
        return ReadAllStringColumn(stream, columnIndex);
    }

    private static List<string?> ReadAllStringColumn(Apache.Arrow.Ipc.IArrowArrayStream stream, int columnIndex)
    {
        var values = new List<string?>();
        while (true)
        {
            RecordBatch? batch = stream.ReadNextRecordBatchAsync().Result;
            if (batch == null)
                break;

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

    public void Dispose()
    {
    }
}
