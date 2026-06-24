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
/// Offline unit tests for <see cref="TypeConverter"/> (Snowflake &lt;-&gt; Arrow type mapping).
/// These require no Snowflake connection.
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

    [Fact]
    public void ConvertSnowflakeTypeToArrow_NumberWithoutScale_IsInt64()
    {
        IArrowType result = _converter.ConvertSnowflakeTypeToArrow(
            new SnowflakeDataType { TypeName = "NUMBER", Precision = 38, Scale = 0 });
        Assert.IsType<Int64Type>(result);
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
    public void ConvertSnowflakeTypeToArrow_TimestampTz_UsesProvidedTimezone()
    {
        var result = Assert.IsType<TimestampType>(_converter.ConvertSnowflakeTypeToArrow(
            new SnowflakeDataType { TypeName = "TIMESTAMP_TZ", Timezone = "America/New_York" }));
        Assert.Equal("America/New_York", result.Timezone);
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
    public void ConvertArrowBatchToParameters_UsesFirstRowValuesKeyedByFieldName()
    {
        var schema = new Schema(
            [new Field("A", Int64Type.Default, true), new Field("B", StringType.Default, true)],
            null);
        IArrowArray idArray = new Int64Array.Builder().Append(42).Build();
        IArrowArray nameArray = new StringArray.Builder().Append("hello").Build();
        using var batch = new RecordBatch(schema, [idArray, nameArray], 1);

        var result = _converter.ConvertArrowBatchToParameters(batch);

        Assert.Equal(2, result.Parameters.Count);
        Assert.Equal(42L, result.Parameters["A"]);
        Assert.Equal("hello", result.Parameters["B"]);
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

        using RecordBatch batch = _converter.ConvertSnowflakeResultToArrow(resultSet);

        Assert.Equal(2, batch.Length);
        Assert.Equal(2, batch.ColumnCount);
        Assert.Equal("ID", batch.Schema.FieldsList[0].Name);
        Assert.IsType<Int64Type>(batch.Schema.FieldsList[0].DataType);
        Assert.IsType<StringType>(batch.Schema.FieldsList[1].DataType);

        var idColumn = (Int64Array)batch.Column(0);
        Assert.Equal(1L, idColumn.GetValue(0));
        Assert.Equal(2L, idColumn.GetValue(1));

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
