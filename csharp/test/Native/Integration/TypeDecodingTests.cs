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
using Apache.Arrow.Types;

namespace AdbcDrivers.Snowflake.Native.Tests.Integration;

/// <summary>
/// Verifies the driver's <b>over-the-wire result decoding</b>: that a query result column for
/// each Snowflake data type is exposed as the correct Arrow type (and value). This is the
/// single home for "does the Arrow result decode correctly for every type" — distinct from
/// <see cref="ClientTests"/>, which tests the next layer down (Arrow → CLR value conversion).
///
/// The expected Arrow types match the driver's declared type mapping (the describe path /
/// TypeConverter). Uses pure literals, so it needs only a live account (no sample data, no
/// writable schema); set SNOWFLAKE_TEST_CONFIG_FILE.
/// </summary>
[Trait("Category", "Integration")]
public class TypeDecodingTests
{
    private readonly ITestOutputHelper _output;
    private readonly IntegrationTestConfiguration _testConfiguration;

    public TypeDecodingTests(ITestOutputHelper output)
    {
        _output = output;
        _testConfiguration = IntegrationTestingUtils.TestConfiguration;

        Skip.If(string.IsNullOrEmpty(_testConfiguration.Account),
            $"Cannot execute test configuration from environment variable `{IntegrationTestingUtils.SnowflakeTestConfigVariable}`");
    }

    /// <summary>
    /// Result-decode gaps that are not yet implemented. These are skipped (not failed) so the
    /// backlog is visible; remove an entry once the decoder handles that type and the assertion
    /// will start enforcing it. See <see cref="Services.Query.SnowflakeResultArrowStream"/>.
    /// </summary>
    private static readonly Dictionary<string, string> NotYetDecoded = new()
    {
        ["'12:34:56'::TIME"] = "TIME is not decoded to Time64 (returns the raw Int64).",
        ["'2020-01-01 12:00:00'::TIMESTAMP_NTZ"] = "TIMESTAMP is not decoded to TimestampType (returns the epoch/fraction struct).",
        ["'2020-01-01 12:00:00'::TIMESTAMP_LTZ"] = "TIMESTAMP is not decoded to TimestampType (returns the epoch/fraction struct).",
        ["'2020-01-01 12:00:00 +00:00'::TIMESTAMP_TZ"] = "TIMESTAMP is not decoded to TimestampType (returns the epoch/fraction struct).",
    };

    [SkippableTheory]
    [InlineData("TRUE::BOOLEAN", typeof(BooleanType))]
    // Scale-0 NUMBER is sized by its declared precision: ≤9 → Int32, ≤18 → Int64, else Decimal128.
    [InlineData("123::NUMBER(9,0)", typeof(Int32Type))]
    [InlineData("123::NUMBER(18,0)", typeof(Int64Type))]
    [InlineData("42::NUMBER(38,0)", typeof(Decimal128Type))]
    [InlineData("9.99::NUMBER(10,2)", typeof(Decimal128Type))]
    [InlineData("1.5::FLOAT", typeof(DoubleType))]
    [InlineData("'hello'::VARCHAR", typeof(StringType))]
    [InlineData("TO_BINARY('AB','HEX')", typeof(BinaryType))]
    [InlineData("'2020-01-01'::DATE", typeof(Date32Type))]
    [InlineData("'12:34:56'::TIME", typeof(Time64Type))]
    [InlineData("'2020-01-01 12:00:00'::TIMESTAMP_NTZ", typeof(TimestampType))]
    [InlineData("'2020-01-01 12:00:00'::TIMESTAMP_LTZ", typeof(TimestampType))]
    [InlineData("'2020-01-01 12:00:00 +00:00'::TIMESTAMP_TZ", typeof(TimestampType))]
    [InlineData("TO_VARIANT(1)", typeof(StringType))]
    [InlineData("OBJECT_CONSTRUCT('a', 1)", typeof(StringType))]
    // Snowflake returns semi-structured ARRAY/OBJECT/VARIANT as a JSON string.
    [InlineData("ARRAY_CONSTRUCT(1, 2)", typeof(StringType))]
    public async Task ResultColumn_HasExpectedArrowType(string sqlLiteral, Type expectedArrowType)
    {
        Skip.If(NotYetDecoded.TryGetValue(sqlLiteral, out string? reason), reason);

        using var connection = Connect();
        using var statement = connection.CreateStatement();
        statement.SqlQuery = $"SELECT {sqlLiteral} AS V";

        var result = await statement.ExecuteQueryAsync();
        Assert.NotNull(result.Stream);
        using var stream = result.Stream!;

        IArrowType actual = stream.Schema.FieldsList[0].DataType;
        _output.WriteLine($"{sqlLiteral} -> {actual.GetType().Name} (expected {expectedArrowType.Name})");
        Assert.IsType(expectedArrowType, actual);
    }

    [SkippableFact]
    public async Task ScaledNumber_DecodesValueWithScale()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        // Snowflake sends a scaled NUMBER as an integer (999) with the scale in field metadata;
        // the driver must rescale it to a Decimal128 so the real value (9.99) comes through.
        statement.SqlQuery = "SELECT 9.99::NUMBER(10,2) AS V";
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result.Stream);
        using var stream = result.Stream!;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        var column = Assert.IsType<Decimal128Array>(batch!.Column(0));
        Assert.Equal(9.99m, column.GetValue(0));
    }

    /// <summary>
    /// Proves Snowflake reports the column's <b>declared</b> precision distinctly in the Arrow
    /// field metadata (9 vs 18 vs 38), independent of the values. This is the foundation for
    /// precision-driven sizing of scale-0 NUMBER (precision ≤ 9 → Int32, ≤ 18 → Int64, else
    /// Decimal128): without a reliable declared precision that sizing would be unsafe.
    /// </summary>
    [SkippableTheory]
    [InlineData("123::NUMBER(9,0)", 9)]
    [InlineData("123::NUMBER(18,0)", 18)]
    [InlineData("123::NUMBER(38,0)", 38)]
    public async Task ScaleZeroNumber_ReportsDeclaredPrecisionInMetadata(string sqlLiteral, int expectedPrecision)
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();
        statement.SqlQuery = $"SELECT {sqlLiteral} AS V";

        var result = await statement.ExecuteQueryAsync();
        Assert.NotNull(result.Stream);
        using var stream = result.Stream!;

        Field field = stream.Schema.FieldsList[0];
        Assert.True(field.HasMetadata, "FIXED column should carry Snowflake field metadata.");
        Assert.True(field.Metadata.TryGetValue("logicalType", out string? logicalType));
        Assert.Equal("FIXED", logicalType);
        Assert.True(field.Metadata.TryGetValue("precision", out string? precision),
            "FIXED column metadata should include 'precision'.");

        _output.WriteLine($"{sqlLiteral} -> precision={precision} (expected {expectedPrecision})");
        Assert.Equal(expectedPrecision, int.Parse(precision!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task ScaleZeroNumber_LargeValue_RoundTripsAsDecimal128()
    {
        using var connection = Connect();
        using var statement = connection.CreateStatement();

        // 20 digits — larger than Int64.MaxValue (9,223,372,036,854,775,807), so sizing scale-0
        // NUMBER(38,0) to Int64 would overflow. It must decode to Decimal128 with the value intact.
        const string literal = "12345678901234567890";
        statement.SqlQuery = $"SELECT {literal}::NUMBER(38,0) AS V";
        var result = await statement.ExecuteQueryAsync();

        Assert.NotNull(result.Stream);
        using var stream = result.Stream!;
        var batch = await stream.ReadNextRecordBatchAsync();

        Assert.NotNull(batch);
        var column = Assert.IsType<Decimal128Array>(batch!.Column(0));
        Assert.Equal(decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture), column.GetValue(0));
    }

    private SnowflakeConnection Connect()
    {
        var driver = IntegrationTestingUtils.GetSnowflakeAdbcDriver(_testConfiguration, out var parameters);
        var database = driver.Open(parameters);
        var connection = database.Connect(new Dictionary<string, string>());
        return (SnowflakeConnection)connection;
    }
}
