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
using Apache.Arrow.Types;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.TypeConversion;

/// <summary>
/// Implements type conversion between Snowflake and Arrow formats.
/// </summary>
internal class TypeConverter : ITypeConverter
{
    /// <inheritdoc/>
    public IArrowType ConvertSnowflakeTypeToArrow(SnowflakeDataType snowflakeType)
    {
        if (snowflakeType == null)
            throw new ArgumentNullException(nameof(snowflakeType));

        return snowflakeType.TypeCode switch
        {
            SnowflakeTypeCode.Boolean => BooleanType.Default,

            SnowflakeTypeCode.Integer => Int64Type.Default,

            SnowflakeTypeCode.Number => snowflakeType.Scale.GetValueOrDefault(0) == 0
                ? Int64Type.Default
                : new Decimal128Type(
                    snowflakeType.Precision.GetValueOrDefault(38),
                    snowflakeType.Scale.GetValueOrDefault(0)),

            SnowflakeTypeCode.Float => FloatType.Default,

            SnowflakeTypeCode.Double => DoubleType.Default,

            SnowflakeTypeCode.Varchar => StringType.Default,

            SnowflakeTypeCode.Binary => BinaryType.Default,

            SnowflakeTypeCode.Date => Date32Type.Default,

            SnowflakeTypeCode.Time => Time64Type.Nanosecond,

            SnowflakeTypeCode.Timestamp or
            SnowflakeTypeCode.TimestampNtz => new TimestampType(TimeUnit.Nanosecond, timezone: (string?)null),

            SnowflakeTypeCode.TimestampLtz => new TimestampType(TimeUnit.Nanosecond, timezone: "UTC"),

            SnowflakeTypeCode.TimestampTz => new TimestampType(TimeUnit.Nanosecond,
                timezone: snowflakeType.Timezone ?? "UTC"),

            SnowflakeTypeCode.Variant or
            SnowflakeTypeCode.Object => StringType.Default, // JSON as string

            SnowflakeTypeCode.Array => new ListType(StringType.Default), // Array of JSON strings

            SnowflakeTypeCode.Geography or
            SnowflakeTypeCode.Geometry => StringType.Default, // GeoJSON as string

            _ => throw new NotSupportedException($"Snowflake type {snowflakeType.TypeName} is not supported.")
        };
    }

    /// <inheritdoc/>
    public SnowflakeDataType ConvertArrowTypeToSnowflake(IArrowType arrowType)
    {
        if (arrowType == null)
            throw new ArgumentNullException(nameof(arrowType));

        return arrowType switch
        {
            BooleanType => new SnowflakeDataType { TypeName = "BOOLEAN" },

            Int8Type or Int16Type or Int32Type or Int64Type or UInt8Type or UInt16Type or UInt32Type or UInt64Type
                => new SnowflakeDataType { TypeName = "NUMBER", Precision = 38, Scale = 0 },

            FloatType => new SnowflakeDataType { TypeName = "FLOAT" },

            DoubleType => new SnowflakeDataType { TypeName = "DOUBLE" },

            Decimal128Type decimal128 => new SnowflakeDataType
            {
                TypeName = "NUMBER",
                Precision = decimal128.Precision,
                Scale = decimal128.Scale
            },

            Decimal256Type decimal256 => new SnowflakeDataType
            {
                TypeName = "NUMBER",
                Precision = decimal256.Precision,
                Scale = decimal256.Scale
            },

            StringType => new SnowflakeDataType { TypeName = "VARCHAR" },

            BinaryType => new SnowflakeDataType { TypeName = "BINARY" },

            Date32Type or Date64Type => new SnowflakeDataType { TypeName = "DATE" },

            Time32Type or Time64Type => new SnowflakeDataType { TypeName = "TIME" },

            TimestampType timestamp when timestamp.Timezone == null => new SnowflakeDataType { TypeName = "TIMESTAMP_NTZ" },
            TimestampType timestamp when timestamp.Timezone == "UTC" => new SnowflakeDataType { TypeName = "TIMESTAMP_LTZ" },
            TimestampType timestamp => new SnowflakeDataType { TypeName = "TIMESTAMP_TZ", Timezone = timestamp.Timezone },

            ListType => new SnowflakeDataType { TypeName = "ARRAY" },

            StructType => new SnowflakeDataType { TypeName = "OBJECT" },

            _ => throw new NotSupportedException($"Arrow type {arrowType.Name} is not supported.")
        };
    }

    /// <inheritdoc/>
    public RecordBatch ConvertSnowflakeResultToArrow(SnowflakeResultSet resultSet)
    {
        if (resultSet == null)
            throw new ArgumentNullException(nameof(resultSet));

        if (resultSet.Columns.Length == 0)
            throw new ArgumentException("Result set must have at least one column.", nameof(resultSet));

        // Build schema
        var fields = resultSet.Columns.Select(col =>
            new Field(col.Name, ConvertSnowflakeTypeToArrow(col.DataType), col.DataType.IsNullable)
        ).ToArray();

        var schema = new Schema(fields, null);

        // Build arrays for each column
        var arrays = new IArrowArray[resultSet.Columns.Length];

        for (int colIndex = 0; colIndex < resultSet.Columns.Length; colIndex++)
        {
            var column = resultSet.Columns[colIndex];
            var columnData = resultSet.Rows.Select(row => row[colIndex]).ToArray();

            arrays[colIndex] = BuildArrowArray(column.DataType, columnData);
        }

        return new RecordBatch(schema, arrays, resultSet.Rows.Length);
    }

    /// <inheritdoc/>
    public ParameterSet ConvertArrowBatchToParameters(RecordBatch batch)
    {
        if (batch == null)
            throw new ArgumentNullException(nameof(batch));

        var parameters = new Dictionary<string, object?>();

        for (int i = 0; i < batch.Schema.FieldsList.Count; i++)
        {
            var field = batch.Schema.FieldsList[i];
            var array = batch.Column(i);

            if (batch.Length > 0)
            {
                parameters[field.Name] = GetValueFromArray(array, 0);
            }
        }

        return new ParameterSet { Parameters = parameters };
    }

    private IArrowArray BuildArrowArray(SnowflakeDataType dataType, object?[] values)
    {
        return dataType.TypeCode switch
        {
            SnowflakeTypeCode.Boolean => BuildBooleanArray(values),
            SnowflakeTypeCode.Integer => BuildInt64Array(values),
            SnowflakeTypeCode.Number => dataType.Scale.GetValueOrDefault(0) == 0
                ? BuildInt64Array(values)
                : BuildDecimalArray(values, dataType),
            SnowflakeTypeCode.Float => BuildFloatArray(values),
            SnowflakeTypeCode.Double => BuildDoubleArray(values),
            SnowflakeTypeCode.Varchar => BuildStringArray(values),
            SnowflakeTypeCode.Binary => BuildBinaryArray(values),
            SnowflakeTypeCode.Date => BuildDateArray(values),
            SnowflakeTypeCode.Time => BuildTimeArray(values),
            SnowflakeTypeCode.Timestamp or
            SnowflakeTypeCode.TimestampNtz or
            SnowflakeTypeCode.TimestampLtz or
            SnowflakeTypeCode.TimestampTz => BuildTimestampArray(values),
            SnowflakeTypeCode.Variant or
            SnowflakeTypeCode.Object or
            SnowflakeTypeCode.Array => BuildStringArray(values), // JSON as string
            _ => throw new NotSupportedException($"Type {dataType.TypeName} is not supported for array building.")
        };
    }

    private IArrowArray BuildBooleanArray(object?[] values)
    {
        var builder = new BooleanArray.Builder();
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else
                builder.Append(Convert.ToBoolean(value));
        }
        return builder.Build();
    }

    private IArrowArray BuildInt64Array(object?[] values)
    {
        var builder = new Int64Array.Builder();
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else
                builder.Append(Convert.ToInt64(value));
        }
        return builder.Build();
    }

    private IArrowArray BuildDecimalArray(object?[] values, SnowflakeDataType dataType)
    {
        var precision = dataType.Precision.GetValueOrDefault(38);
        var scale = dataType.Scale.GetValueOrDefault(0);
        var builder = new Decimal128Array.Builder(new Decimal128Type(precision, scale));

        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else
                builder.Append(Convert.ToDecimal(value));
        }
        return builder.Build();
    }

    private IArrowArray BuildFloatArray(object?[] values)
    {
        var builder = new FloatArray.Builder();
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else
                builder.Append(Convert.ToSingle(value));
        }
        return builder.Build();
    }

    private IArrowArray BuildDoubleArray(object?[] values)
    {
        var builder = new DoubleArray.Builder();
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else
                builder.Append(Convert.ToDouble(value));
        }
        return builder.Build();
    }

    private IArrowArray BuildStringArray(object?[] values)
    {
        var builder = new StringArray.Builder();
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else
                builder.Append(value.ToString());
        }
        return builder.Build();
    }

    private IArrowArray BuildBinaryArray(object?[] values)
    {
        var builder = new BinaryArray.Builder();
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else if (value is byte[] bytes)
                builder.Append(bytes);
            else
                throw new InvalidOperationException($"Cannot convert {value.GetType()} to binary.");
        }
        return builder.Build();
    }

    private IArrowArray BuildDateArray(object?[] values)
    {
        var builder = new Date32Array.Builder();
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else if (value is DateTime dateTime)
                builder.Append(dateTime);
            else if (value is DateTimeOffset dateTimeOffset)
                builder.Append(dateTimeOffset.DateTime);
            else
                builder.Append(Convert.ToDateTime(value));
        }
        return builder.Build();
    }

    private IArrowArray BuildTimeArray(object?[] values)
    {
        var builder = new Time64Array.Builder(Time64Type.Nanosecond);
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else if (value is TimeSpan timeSpan)
                builder.Append(timeSpan.Ticks * 100); // Convert to nanoseconds
            else
                throw new InvalidOperationException($"Cannot convert {value.GetType()} to time.");
        }
        return builder.Build();
    }

    private IArrowArray BuildTimestampArray(object?[] values)
    {
        var builder = new TimestampArray.Builder(new TimestampType(TimeUnit.Nanosecond, timezone: (string?)null));
        foreach (var value in values)
        {
            if (value == null)
                builder.AppendNull();
            else if (value is DateTime dateTime)
                builder.Append(dateTime);
            else if (value is DateTimeOffset dateTimeOffset)
                builder.Append(dateTimeOffset);
            else
                builder.Append(Convert.ToDateTime(value));
        }
        return builder.Build();
    }

    private object? GetValueFromArray(IArrowArray array, int index)
    {
        if (array.IsNull(index))
            return null;

        return array switch
        {
            BooleanArray boolArray => boolArray.GetValue(index),
            Int32Array int32Array => int32Array.GetValue(index),
            Int64Array int64Array => int64Array.GetValue(index),
            FloatArray floatArray => floatArray.GetValue(index),
            DoubleArray doubleArray => doubleArray.GetValue(index),
            StringArray stringArray => stringArray.GetString(index),
            BinaryArray binaryArray => binaryArray.GetBytes(index).ToArray(),
            Date32Array dateArray => dateArray.GetDateTime(index),
            TimestampArray timestampArray => timestampArray.GetTimestamp(index),
            _ => throw new NotSupportedException($"Array type {array.GetType()} is not supported.")
        };
    }
}
