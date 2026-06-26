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
using AdbcDrivers.Snowflake.Native.Services.TypeConversion;
using Xunit;

using Apache.Arrow;
using Apache.Arrow.Types;

namespace AdbcDrivers.Snowflake.Native.Tests;

/// <summary>
/// Offline unit tests for <see cref="TypeConverter"/> — the <b>describe/metadata mapping</b>
/// (layer 1 of 3, see below). It translates a Snowflake type <i>name</i> to an Arrow type and
/// back, maps an Arrow batch to bind parameters, and builds an Arrow batch from a JSON row set.
/// This is the path behind GetTableSchema/describe; being a pure function over type names, it
/// needs no connection.
///
/// The driver has three type-fidelity layers, each tested in its own place:
/// <list type="number">
///   <item><b>this</b> — Snowflake type name ⇄ Arrow (describe path, offline);</item>
///   <item><see cref="Integration.TypeDecodingTests"/> — Snowflake result <i>wire</i> → Arrow,
///     over a live query (the source of truth for what a SELECT actually returns);</item>
///   <item><c>Integration.ClientTests.Reader_ConvertsColumnTypesToClr</c> — Arrow → CLR via the
///     ADO.NET client.</item>
/// </list>
/// Layers 1 and 2 are distinct code paths and can differ — e.g. NUMBER(38,0) maps to Int64 here
/// but decodes to Decimal128 off the wire.
/// </summary>
[Trait("Category", "Unit")]
public class TypeConverterTests
{
    private readonly TypeConverter _converter = new();

    // ---- ConvertSnowflakeTypeToArrow ----

    [Theory]
    [InlineData("BOOLEAN", typeof(BooleanType))]
    [InlineData("INTEGER", typeof(Int64Type))]
    [InlineData("INT", typeof(Int64Type))]
    [InlineData("BIGINT", typeof(Int64Type))]
    [InlineData("FLOAT", typeof(FloatType))]
    [InlineData("DOUBLE", typeof(DoubleType))]
    [InlineData("REAL", typeof(DoubleType))]
    [InlineData("VARCHAR", typeof(StringType))]
    [InlineData("STRING", typeof(StringType))]
    [InlineData("TEXT", typeof(StringType))]
    [InlineData("BINARY", typeof(BinaryType))]
    [InlineData("DATE", typeof(Date32Type))]
    [InlineData("TIME", typeof(Time64Type))]
    [InlineData("VARIANT", typeof(StringType))]
    [InlineData("OBJECT", typeof(StringType))]
    [InlineData("ARRAY", typeof(ListType))]
    [InlineData("GEOGRAPHY", typeof(StringType))]
    [InlineData("GEOMETRY", typeof(StringType))]
    public void ConvertSnowflakeTypeToArrow_MapsScalarTypes(string typeName, Type expectedArrowType)
    {
        IArrowType result = _converter.ConvertSnowflakeTypeToArrow(new SnowflakeDataType { TypeName = typeName });
        Assert.IsType(expectedArrowType, result);
    }

    [Theory]
    [InlineData(9, typeof(Int32Type))]
    [InlineData(18, typeof(Int64Type))]
    [InlineData(38, typeof(Decimal128Type))]
    public void ConvertSnowflakeTypeToArrow_ScaleZeroNumber_SizedByPrecision(int precision, Type expectedArrowType)
    {
        // Matches the result decoder: a scale-0 NUMBER is sized by its declared precision so the
        // described schema agrees with what a query returns.
        IArrowType result = _converter.ConvertSnowflakeTypeToArrow(
            new SnowflakeDataType { TypeName = "NUMBER", Precision = precision, Scale = 0 });
        Assert.IsType(expectedArrowType, result);
    }

    [Fact]
    public void ConvertSnowflakeTypeToArrow_NumberWithScale_IsDecimal128()
    {
        IArrowType result = _converter.ConvertSnowflakeTypeToArrow(
            new SnowflakeDataType { TypeName = "NUMBER", Precision = 18, Scale = 2 });
        var decimalType = Assert.IsType<Decimal128Type>(result);
        Assert.Equal(18, decimalType.Precision);
        Assert.Equal(2, decimalType.Scale);
    }

    [Fact]
    public void ConvertSnowflakeTypeToArrow_Array_IsListOfString()
    {
        IArrowType result = _converter.ConvertSnowflakeTypeToArrow(new SnowflakeDataType { TypeName = "ARRAY" });
        var listType = Assert.IsType<ListType>(result);
        Assert.IsType<StringType>(listType.ValueDataType);
    }

    [Theory]
    [InlineData("TIMESTAMP_NTZ", null)]
    [InlineData("TIMESTAMP", null)]
    [InlineData("DATETIME", null)]
    [InlineData("TIMESTAMP_LTZ", "UTC")]
    public void ConvertSnowflakeTypeToArrow_Timestamp_HasExpectedTimezone(string typeName, string? expectedTimezone)
    {
        var result = Assert.IsType<TimestampType>(
            _converter.ConvertSnowflakeTypeToArrow(new SnowflakeDataType { TypeName = typeName }));
        Assert.Equal(TimeUnit.Nanosecond, result.Unit);
        Assert.Equal(expectedTimezone, result.Timezone);
    }

    [Fact]
    public void ConvertSnowflakeTypeToArrow_TimestampTz_IsTaggedUtc()
    {
        // TIMESTAMP_TZ decodes to its UTC instant (a single Arrow column cannot carry a per-row
        // offset), so the described type matches the result: Timestamp[ns] tagged "UTC", whatever
        // the column's own timezone.
        var result = Assert.IsType<TimestampType>(_converter.ConvertSnowflakeTypeToArrow(
            new SnowflakeDataType { TypeName = "TIMESTAMP_TZ", Timezone = "America/New_York" }));
        Assert.Equal("UTC", result.Timezone);
    }

    [Fact]
    public void ConvertSnowflakeTypeToArrow_UnknownType_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => _converter.ConvertSnowflakeTypeToArrow(new SnowflakeDataType { TypeName = "NONSENSE" }));
    }

    [Fact]
    public void ConvertSnowflakeTypeToArrow_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _converter.ConvertSnowflakeTypeToArrow(null!));
    }

    // ---- ConvertArrowTypeToSnowflake ----

    [Fact]
    public void ConvertArrowTypeToSnowflake_MapsScalarTypes()
    {
        Assert.Equal("BOOLEAN", _converter.ConvertArrowTypeToSnowflake(BooleanType.Default).TypeName);
        Assert.Equal("FLOAT", _converter.ConvertArrowTypeToSnowflake(FloatType.Default).TypeName);
        Assert.Equal("DOUBLE", _converter.ConvertArrowTypeToSnowflake(DoubleType.Default).TypeName);
        Assert.Equal("VARCHAR", _converter.ConvertArrowTypeToSnowflake(StringType.Default).TypeName);
        Assert.Equal("BINARY", _converter.ConvertArrowTypeToSnowflake(BinaryType.Default).TypeName);
        Assert.Equal("DATE", _converter.ConvertArrowTypeToSnowflake(Date32Type.Default).TypeName);
        Assert.Equal("ARRAY", _converter.ConvertArrowTypeToSnowflake(new ListType(StringType.Default)).TypeName);
        Assert.Equal("OBJECT", _converter.ConvertArrowTypeToSnowflake(
            new StructType([new Field("x", Int32Type.Default, true)])).TypeName);
    }

    [Fact]
    public void ConvertArrowTypeToSnowflake_Integer_IsNumber38_0()
    {
        SnowflakeDataType result = _converter.ConvertArrowTypeToSnowflake(Int32Type.Default);
        Assert.Equal("NUMBER", result.TypeName);
        Assert.Equal(38, result.Precision);
        Assert.Equal(0, result.Scale);
    }

    [Fact]
    public void ConvertArrowTypeToSnowflake_Decimal_PreservesPrecisionAndScale()
    {
        SnowflakeDataType result = _converter.ConvertArrowTypeToSnowflake(new Decimal128Type(20, 4));
        Assert.Equal("NUMBER", result.TypeName);
        Assert.Equal(20, result.Precision);
        Assert.Equal(4, result.Scale);
    }

    [Theory]
    [InlineData(null, "TIMESTAMP_NTZ")]
    [InlineData("UTC", "TIMESTAMP_LTZ")]
    [InlineData("America/New_York", "TIMESTAMP_TZ")]
    public void ConvertArrowTypeToSnowflake_Timestamp_MapsByTimezone(string? timezone, string expectedTypeName)
    {
        SnowflakeDataType result = _converter.ConvertArrowTypeToSnowflake(new TimestampType(TimeUnit.Nanosecond, timezone));
        Assert.Equal(expectedTypeName, result.TypeName);
    }

    [Fact]
    public void ConvertArrowTypeToSnowflake_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _converter.ConvertArrowTypeToSnowflake(null!));
    }

    // ---- ConvertArrowBatchToParameters ----

    [Fact]
    public void ConvertArrowBatchToParameters_KeysBindingsPositionallyWithTypes()
    {
        // Given a two-column Arrow batch (an Int64 and a string)
        var schema = new Schema(
            [new Field("A", Int64Type.Default, true), new Field("B", StringType.Default, true)],
            null);
        IArrowArray idArray = new Int64Array.Builder().Append(42).Build();
        IArrowArray nameArray = new StringArray.Builder().Append("hello").Build();
        using var batch = new RecordBatch(schema, [idArray, nameArray], 1);

        // When it is converted to bind parameters
        var result = _converter.ConvertArrowBatchToParameters(batch);

        // Then bindings are keyed by 1-based placeholder position (matching '?'), not by column
        // name, with the Snowflake bind type mapped from the Arrow type.
        Assert.Equal(2, result.Parameters.Count);
        Assert.Equal("FIXED", result.Parameters["1"].Type);
        Assert.Equal("42", result.Parameters["1"].Value);
        Assert.Equal("TEXT", result.Parameters["2"].Type);
        Assert.Equal("hello", result.Parameters["2"].Value);
    }

    [Fact]
    public void ConvertArrowBatchToParameters_EmptyBatch_ReturnsNoParameters()
    {
        var schema = new Schema([new Field("A", Int64Type.Default, true)], null);
        IArrowArray empty = new Int64Array.Builder().Build();
        using var batch = new RecordBatch(schema, [empty], 0);

        ParameterSet result = _converter.ConvertArrowBatchToParameters(batch);

        Assert.Empty(result.Parameters);
    }

    [Fact]
    public void ConvertArrowBatchToParameters_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _converter.ConvertArrowBatchToParameters(null!));
    }

    // ---- ConvertSnowflakeResultToArrow ----

    [Fact]
    public void ConvertSnowflakeResultToArrow_BuildsSchemaAndValues()
    {
        // Given a JSON result set with two typed columns and two rows (one with a null)
        var resultSet = new SnowflakeResultSet
        {
            Columns =
            [
                new SnowflakeColumnMetadata { Name = "ID", DataType = new SnowflakeDataType { TypeName = "NUMBER", Precision = 38, Scale = 0 } },
                new SnowflakeColumnMetadata { Name = "NAME", DataType = new SnowflakeDataType { TypeName = "VARCHAR", IsNullable = true } }
            ],
            Rows =
            [
                [1L, "a"],
                [2L, null]
            ]
        };

        // When it is converted to an Arrow batch
        using RecordBatch batch = _converter.ConvertSnowflakeResultToArrow(resultSet);

        // Then the schema and values match
        Assert.Equal(2, batch.Length);
        Assert.Equal(2, batch.ColumnCount);
        Assert.Equal("ID", batch.Schema.FieldsList[0].Name);
        // NUMBER(38,0) sizes to Decimal128 (precision-driven), and the value array matches.
        Assert.IsType<Decimal128Type>(batch.Schema.FieldsList[0].DataType);
        Assert.IsType<StringType>(batch.Schema.FieldsList[1].DataType);

        var idColumn = (Decimal128Array)batch.Column(0);
        Assert.Equal(1m, idColumn.GetValue(0));
        Assert.Equal(2m, idColumn.GetValue(1));

        var nameColumn = (StringArray)batch.Column(1);
        Assert.Equal("a", nameColumn.GetString(0));
        Assert.True(nameColumn.IsNull(1));
    }

    [Fact]
    public void ConvertSnowflakeResultToArrow_NoColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() => _converter.ConvertSnowflakeResultToArrow(new SnowflakeResultSet()));
    }

    [Fact]
    public void ConvertSnowflakeResultToArrow_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _converter.ConvertSnowflakeResultToArrow(null!));
    }
}
