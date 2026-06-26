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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Types;
using Ipc = Apache.Arrow.Ipc;

namespace AdbcDrivers.Snowflake.Native.Services.Query;

/// <summary>
/// Decorates a result stream to apply Snowflake-specific Arrow fixups.
/// </summary>
/// <remarks>
/// Snowflake encodes a fixed-point NUMBER (<c>logicalType=FIXED</c>) as an integer column sized
/// to the values (Int8/16/32/64), with the declared precision and scale carried in the field
/// metadata. This stream normalizes those columns to a stable, value-independent Arrow type:
/// <list type="bullet">
///   <item><c>scale &gt; 0</c> → <see cref="Decimal128Type"/> with that scale (e.g. 9.99 arrives
///     as the integer 999 with scale=2 and is rescaled to 9.99);</item>
///   <item><c>scale == 0</c> → the narrowest integer type guaranteed to hold the declared
///     precision: <see cref="Int32Type"/> (precision ≤ 9), <see cref="Int64Type"/> (precision ≤
///     18), otherwise <see cref="Decimal128Type"/> (a NUMBER(38,0) can exceed Int64).</item>
/// </list>
/// Sizing by the <i>declared</i> precision (not the values) keeps the result schema stable across
/// runs and chunks. Non-FIXED columns, and FIXED columns whose wire type already matches the
/// target, are passed through unchanged. Field metadata is preserved on rewritten columns.
/// </remarks>
internal sealed class SnowflakeResultArrowStream : Ipc.IArrowArrayStream
{
    private const string FixedLogicalType = "FIXED";

    // Largest decimal precision guaranteed to fit each integer width: Int32 holds 9 full digits
    // (max 2,147,483,647), Int64 holds 18 (max 9,223,372,036,854,775,807).
    private const int MaxInt32Precision = 9;
    private const int MaxInt64Precision = 18;

    private readonly Ipc.IArrowArrayStream _inner;
    private readonly List<FixedColumn> _fixedColumns;

    public Schema Schema { get; }

    public SnowflakeResultArrowStream(Ipc.IArrowArrayStream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        (_fixedColumns, Schema) = Analyze(inner.Schema);
    }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        RecordBatch? batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        if (batch == null || _fixedColumns.Count == 0)
            return batch;

        // Rebuild only the FIXED columns that need normalizing; reuse the rest. The pass-through
        // columns become owned by the returned batch, so the source batch must NOT be disposed;
        // the integer columns we replace are disposed individually instead.
        var columns = new IArrowArray[batch.ColumnCount];
        int next = 0;
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            if (next < _fixedColumns.Count && _fixedColumns[next].Index == i)
            {
                IArrowArray source = batch.Column(i);
                columns[i] = ConvertColumn(source, _fixedColumns[next]);
                source.Dispose();
                next++;
            }
            else
            {
                columns[i] = batch.Column(i);
            }
        }

        return new RecordBatch(Schema, columns, batch.Length);
    }

    public void Dispose() => _inner.Dispose();

    private static (List<FixedColumn> Fixed, Schema Schema) Analyze(Schema schema)
    {
        var fixedColumns = new List<FixedColumn>();
        var fields = new List<Field>(schema.FieldsList.Count);

        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            Field field = schema.FieldsList[i];
            if (IsIntegerType(field.DataType)
                && TryGetFixedScale(field, out int precision, out int scale))
            {
                IArrowType target = TargetTypeFor(precision, scale);
                if (field.DataType.TypeId == target.TypeId)
                {
                    // Wire type already matches the target (e.g. NUMBER(18,0) arrived as Int64);
                    // pass through, keeping the original field and its metadata.
                    fields.Add(field);
                }
                else
                {
                    fixedColumns.Add(new FixedColumn(i, target, scale));
                    fields.Add(new Field(field.Name, target, field.IsNullable, field.Metadata));
                }
            }
            else
            {
                fields.Add(field);
            }
        }

        return (fixedColumns, new Schema(fields, schema.Metadata));
    }

    private static IArrowType TargetTypeFor(int precision, int scale)
    {
        if (scale > 0)
            return new Decimal128Type(precision, scale);
        if (precision <= MaxInt32Precision)
            return Int32Type.Default;
        if (precision <= MaxInt64Precision)
            return Int64Type.Default;
        return new Decimal128Type(precision, scale);
    }

    private static IArrowArray ConvertColumn(IArrowArray source, FixedColumn column) => column.TargetType switch
    {
        Int32Type => ToInt32(source),
        Int64Type => ToInt64(source),
        Decimal128Type decimalType => RescaleToDecimal(source, decimalType, column.Scale),
        _ => throw new NotSupportedException($"Unexpected target type {column.TargetType} for a FIXED column.")
    };

    private static Int32Array ToInt32(IArrowArray source)
    {
        var builder = new Int32Array.Builder();
        for (int i = 0; i < source.Length; i++)
        {
            if (source.IsNull(i))
                builder.AppendNull();
            else
                builder.Append(checked((int)ReadInteger(source, i)));
        }

        return builder.Build();
    }

    private static Int64Array ToInt64(IArrowArray source)
    {
        var builder = new Int64Array.Builder();
        for (int i = 0; i < source.Length; i++)
        {
            if (source.IsNull(i))
                builder.AppendNull();
            else
                builder.Append(ReadInteger(source, i));
        }

        return builder.Build();
    }

    private static bool TryGetFixedScale(Field field, out int precision, out int scale)
    {
        precision = 38;
        scale = 0;

        if (!field.HasMetadata
            || !field.Metadata.TryGetValue("logicalType", out string? logical)
            || !string.Equals(logical, FixedLogicalType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (field.Metadata.TryGetValue("precision", out string? p)
            && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPrecision)
            && parsedPrecision > 0)
        {
            precision = parsedPrecision;
        }

        field.Metadata.TryGetValue("scale", out string? s);
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out scale);
        return true;
    }

    private static bool IsIntegerType(IArrowType type) =>
        type is Int8Type or Int16Type or Int32Type or Int64Type;

    private static Decimal128Array RescaleToDecimal(IArrowArray source, Decimal128Type type, int scale)
    {
        decimal scaleFactor = 1m;
        for (int k = 0; k < scale; k++)
            scaleFactor *= 10m;

        var builder = new Decimal128Array.Builder(type);
        for (int i = 0; i < source.Length; i++)
        {
            if (source.IsNull(i))
                builder.AppendNull();
            else
                builder.Append(scale > 0 ? ReadInteger(source, i) / scaleFactor : ReadInteger(source, i));
        }

        return builder.Build();
    }

    private static long ReadInteger(IArrowArray array, int index) => array switch
    {
        Int8Array a => a.GetValue(index)!.Value,
        Int16Array a => a.GetValue(index)!.Value,
        Int32Array a => a.GetValue(index)!.Value,
        Int64Array a => a.GetValue(index)!.Value,
        _ => throw new NotSupportedException($"Unexpected array type {array.GetType().Name} for a FIXED column.")
    };

    private readonly record struct FixedColumn(int Index, IArrowType TargetType, int Scale);
}
