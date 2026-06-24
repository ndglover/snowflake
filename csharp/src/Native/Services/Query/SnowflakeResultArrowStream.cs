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
/// Snowflake encodes a scaled NUMBER (<c>logicalType=FIXED</c> with <c>scale &gt; 0</c>) as a
/// small integer column with the scale carried in the field metadata, so the raw value is
/// unscaled (e.g. 9.99 arrives as the integer 999 with scale=2). This stream rewrites those
/// columns to <see cref="Decimal128Type"/> with the correct scale so consumers see the real
/// value. Columns with scale 0 (and all non-FIXED columns) are passed through unchanged.
/// </remarks>
internal sealed class SnowflakeResultArrowStream : Ipc.IArrowArrayStream
{
    private const string FixedLogicalType = "FIXED";

    private readonly Ipc.IArrowArrayStream _inner;
    private readonly List<ScaledColumn> _scaledColumns;

    public Schema Schema { get; }

    public SnowflakeResultArrowStream(Ipc.IArrowArrayStream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        (_scaledColumns, Schema) = Analyze(inner.Schema);
    }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        RecordBatch? batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        if (batch == null || _scaledColumns.Count == 0)
            return batch;

        // Rebuild only the scaled-decimal columns; reuse the rest. The pass-through columns
        // become owned by the returned batch, so the source batch must NOT be disposed; the
        // integer columns we replace are disposed individually instead.
        var columns = new IArrowArray[batch.ColumnCount];
        int next = 0;
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            if (next < _scaledColumns.Count && _scaledColumns[next].Index == i)
            {
                IArrowArray source = batch.Column(i);
                columns[i] = RescaleToDecimal(source, _scaledColumns[next].Type);
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

    private static (List<ScaledColumn> Scaled, Schema Schema) Analyze(Schema schema)
    {
        var scaled = new List<ScaledColumn>();
        var fields = new List<Field>(schema.FieldsList.Count);

        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            Field field = schema.FieldsList[i];
            if (IsIntegerType(field.DataType)
                && TryGetFixedScale(field, out int precision, out int scale)
                && scale > 0)
            {
                var type = new Decimal128Type(precision, scale);
                scaled.Add(new ScaledColumn(i, type));
                fields.Add(new Field(field.Name, type, field.IsNullable));
            }
            else
            {
                fields.Add(field);
            }
        }

        return (scaled, new Schema(fields, schema.Metadata));
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

    private static Decimal128Array RescaleToDecimal(IArrowArray source, Decimal128Type type)
    {
        decimal scaleFactor = 1m;
        for (int k = 0; k < type.Scale; k++)
            scaleFactor *= 10m;

        var builder = new Decimal128Array.Builder(type);
        for (int i = 0; i < source.Length; i++)
        {
            if (source.IsNull(i))
                builder.AppendNull();
            else
                builder.Append(ReadInteger(source, i) / scaleFactor);
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

    private readonly record struct ScaledColumn(int Index, Decimal128Type Type);
}
