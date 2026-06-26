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
/// Decorates a result stream to apply Snowflake-specific Arrow fixups, normalizing the encoding-
/// dependent shapes Snowflake puts on the wire into stable, value-independent Arrow types. The
/// Snowflake type is read from each field's <c>logicalType</c> metadata.
/// </summary>
/// <remarks>
/// <para><b>FIXED</b> (NUMBER/INT/DECIMAL) arrives as an integer sized to the values
/// (Int8/16/32/64), with the declared precision and scale in the metadata. It is normalized by the
/// declared precision (not the values, so the result schema is stable across runs and chunks):
/// <list type="bullet">
///   <item><c>scale &gt; 0</c> → <see cref="Decimal128Type"/> (e.g. 9.99 arrives as 999 scale=2);</item>
///   <item><c>scale == 0</c> → the narrowest integer guaranteed to hold the precision:
///     <see cref="Int32Type"/> (≤ 9), <see cref="Int64Type"/> (≤ 18), else
///     <see cref="Decimal128Type"/> (a NUMBER(38,0) can exceed Int64).</item>
/// </list></para>
/// <para><b>TIME</b> arrives as an integer of seconds-of-day × 10^scale; it is rescaled to
/// <see cref="Time64Type"/> nanoseconds.</para>
/// <para><b>TIMESTAMP_NTZ/LTZ/TZ</b> arrive either as a single integer (the timestamp in
/// 10^-scale units) or as a struct: <c>epoch</c> (seconds) plus, when the scale needs it, a
/// <c>fraction</c> (nanoseconds) and, for TZ, a <c>timezone</c> field. They are decoded to
/// <see cref="TimestampType"/> nanoseconds (NTZ has no zone; LTZ/TZ carry the UTC instant). The
/// per-row TZ offset is not representable in a single Arrow column, so it is dropped after being
/// applied to reach the UTC instant. Nanosecond timestamps cannot represent dates beyond ~2262.</para>
/// <para>Columns of any other type, and FIXED columns whose wire type already matches the target,
/// are passed through unchanged. Field metadata is preserved on rewritten columns.</para>
/// </remarks>
internal sealed class SnowflakeResultArrowStream : Ipc.IArrowArrayStream
{
    private const string LogicalTypeKey = "logicalType";
    private const string PrecisionKey = "precision";
    private const string ScaleKey = "scale";

    private const string FixedLogicalType = "FIXED";
    private const string TimeLogicalType = "TIME";
    private const string TimestampNtzLogicalType = "TIMESTAMP_NTZ";
    private const string TimestampLtzLogicalType = "TIMESTAMP_LTZ";
    private const string TimestampTzLogicalType = "TIMESTAMP_TZ";

    private const string EpochFieldName = "epoch";
    private const string FractionFieldName = "fraction";

    private const string Utc = "UTC";

    // Largest decimal precision guaranteed to fit each integer width: Int32 holds 9 full digits
    // (max 2,147,483,647), Int64 holds 18 (max 9,223,372,036,854,775,807).
    private const int MaxInt32Precision = 9;
    private const int MaxInt64Precision = 18;

    private const long NanosecondsPerSecond = 1_000_000_000L;

    private readonly Ipc.IArrowArrayStream _inner;
    private readonly List<ColumnTransform> _transforms;

    public Schema Schema { get; }

    public SnowflakeResultArrowStream(Ipc.IArrowArrayStream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        (_transforms, Schema) = Analyze(inner.Schema);
    }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        RecordBatch? batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        if (batch == null || _transforms.Count == 0)
            return batch;

        // Rebuild only the transformed columns; reuse the rest. The pass-through columns become
        // owned by the returned batch, so the source batch must NOT be disposed; the columns we
        // replace are disposed individually instead. _transforms is in ascending Index order.
        var columns = new IArrowArray[batch.ColumnCount];
        int next = 0;
        for (int i = 0; i < batch.ColumnCount; i++)
        {
            if (next < _transforms.Count && _transforms[next].Index == i)
            {
                IArrowArray source = batch.Column(i);
                columns[i] = _transforms[next].Convert(source);
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

    private static (List<ColumnTransform> Transforms, Schema Schema) Analyze(Schema schema)
    {
        var transforms = new List<ColumnTransform>();
        var fields = new List<Field>(schema.FieldsList.Count);

        for (int i = 0; i < schema.FieldsList.Count; i++)
        {
            Field field = schema.FieldsList[i];
            if (!TryGetLogicalType(field, out string logicalType, out int precision, out int scale))
            {
                fields.Add(field);
                continue;
            }

            int columnScale = scale;
            Field outField = field;

            switch (logicalType)
            {
                case FixedLogicalType when IsIntegerType(field.DataType):
                {
                    IArrowType target = FixedTargetType(precision, scale);
                    if (field.DataType.TypeId != target.TypeId)
                    {
                        transforms.Add(new ColumnTransform(i, source => ConvertFixed(source, target, columnScale)));
                        outField = new Field(field.Name, target, field.IsNullable, field.Metadata);
                    }
                    break;
                }

                case TimeLogicalType:
                {
                    var target = new Time64Type(TimeUnit.Nanosecond);
                    transforms.Add(new ColumnTransform(i, source => ConvertTime(source, target, columnScale)));
                    outField = new Field(field.Name, target, field.IsNullable, field.Metadata);
                    break;
                }

                case TimestampNtzLogicalType:
                case TimestampLtzLogicalType:
                case TimestampTzLogicalType:
                {
                    string? timezone = logicalType == TimestampNtzLogicalType ? null : Utc;
                    var target = new TimestampType(TimeUnit.Nanosecond, timezone);
                    transforms.Add(new ColumnTransform(i, source => ConvertTimestamp(source, target, columnScale)));
                    outField = new Field(field.Name, target, field.IsNullable, field.Metadata);
                    break;
                }
            }

            fields.Add(outField);
        }

        return (transforms, new Schema(fields, schema.Metadata));
    }

    private static bool TryGetLogicalType(Field field, out string logicalType, out int precision, out int scale)
    {
        logicalType = string.Empty;
        precision = 38;
        scale = 0;

        if (!field.HasMetadata
            || !field.Metadata.TryGetValue(LogicalTypeKey, out string? logical)
            || string.IsNullOrEmpty(logical))
        {
            return false;
        }

        logicalType = logical.ToUpperInvariant();

        if (field.Metadata.TryGetValue(PrecisionKey, out string? p)
            && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPrecision)
            && parsedPrecision > 0)
        {
            precision = parsedPrecision;
        }

        field.Metadata.TryGetValue(ScaleKey, out string? s);
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out scale);
        return true;
    }

    private static IArrowType FixedTargetType(int precision, int scale)
    {
        if (scale > 0)
            return new Decimal128Type(precision, scale);
        if (precision <= MaxInt32Precision)
            return Int32Type.Default;
        if (precision <= MaxInt64Precision)
            return Int64Type.Default;
        return new Decimal128Type(precision, scale);
    }

    private static IArrowArray ConvertFixed(IArrowArray source, IArrowType target, int scale) => target switch
    {
        Int32Type => ToInt32(source),
        Int64Type => ToInt64(source),
        Decimal128Type decimalType => RescaleToDecimal(source, decimalType, scale),
        _ => throw new NotSupportedException($"Unexpected target type {target} for a FIXED column.")
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

    private static Time64Array ConvertTime(IArrowArray source, Time64Type type, int scale)
    {
        long multiplier = PowerOfTen(9 - scale);
        (ArrowBuffer values, ArrowBuffer validity, int nullCount) =
            BuildLongColumn(source.Length, source.IsNull, i => ReadInteger(source, i) * multiplier);
        return new Time64Array(type, values, validity, source.Length, nullCount, 0);
    }

    private static TimestampArray ConvertTimestamp(IArrowArray source, TimestampType type, int scale)
    {
        long multiplier = PowerOfTen(9 - scale);

        Func<int, bool> isNull;
        Func<int, long> nanoseconds;

        if (source is StructArray structArray)
        {
            var structType = (StructType)structArray.Data.DataType;
            int epochIndex = Math.Max(0, FieldIndex(structType, EpochFieldName));
            int fractionIndex = FieldIndex(structType, FractionFieldName);

            var epoch = (Int64Array)structArray.Fields[epochIndex];
            Int32Array? fraction = fractionIndex >= 0 ? (Int32Array)structArray.Fields[fractionIndex] : null;

            isNull = structArray.IsNull;
            nanoseconds = fraction != null
                // epoch is seconds and fraction is the sub-second part in nanoseconds.
                ? i => epoch.Values[i] * NanosecondsPerSecond + (long)fraction.Values[i] * multiplier
                // no fraction field: epoch holds the whole timestamp in 10^-scale units.
                : i => epoch.Values[i] * multiplier;
        }
        else
        {
            // single integer: the whole timestamp in 10^-scale units.
            isNull = source.IsNull;
            nanoseconds = i => ReadInteger(source, i) * multiplier;
        }

        (ArrowBuffer values, ArrowBuffer validity, int nullCount) =
            BuildLongColumn(source.Length, isNull, nanoseconds);
        return new TimestampArray(type, values, validity, source.Length, nullCount, 0);
    }

    private static (ArrowBuffer Values, ArrowBuffer Validity, int NullCount) BuildLongColumn(
        int length, Func<int, bool> isNull, Func<int, long> value)
    {
        var values = new ArrowBuffer.Builder<long>(length);
        var validity = new ArrowBuffer.BitmapBuilder(length);
        int nullCount = 0;

        for (int i = 0; i < length; i++)
        {
            if (isNull(i))
            {
                values.Append(0L);
                validity.Append(false);
                nullCount++;
            }
            else
            {
                values.Append(value(i));
                validity.Append(true);
            }
        }

        return (values.Build(), validity.Build(), nullCount);
    }

    private static int FieldIndex(StructType type, string name)
    {
        for (int i = 0; i < type.Fields.Count; i++)
        {
            if (string.Equals(type.Fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static long PowerOfTen(int exponent)
    {
        long result = 1;
        for (int i = 0; i < exponent; i++)
            result *= 10;
        return result;
    }

    private static bool IsIntegerType(IArrowType type) =>
        type is Int8Type or Int16Type or Int32Type or Int64Type;

    private static long ReadInteger(IArrowArray array, int index) => array switch
    {
        Int8Array a => a.GetValue(index)!.Value,
        Int16Array a => a.GetValue(index)!.Value,
        Int32Array a => a.GetValue(index)!.Value,
        Int64Array a => a.GetValue(index)!.Value,
        _ => throw new NotSupportedException($"Unexpected array type {array.GetType().Name} for an integer column.")
    };

    private readonly record struct ColumnTransform(int Index, Func<IArrowArray, IArrowArray> Convert);
}
