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
using System.Text.Json;
using Apache.Arrow.Types;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.TypeConversion;

/// <summary>
/// Handles conversion of Snowflake semi-structured data types (VARIANT, OBJECT, ARRAY).
/// </summary>
internal class SemiStructuredConverter
{
    /// <summary>
    /// Converts a Snowflake VARIANT value to an Arrow representation.
    /// </summary>
    /// <param name="variantValue">The VARIANT value as JSON string.</param>
    /// <returns>The value in Arrow-compatible format.</returns>
    public object? ConvertVariant(string? variantValue)
    {
        if (string.IsNullOrEmpty(variantValue))
            return null;

        try
        {
            // Parse JSON and determine the appropriate Arrow representation
            using var document = JsonDocument.Parse(variantValue);
            return ConvertJsonElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse VARIANT value: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts a Snowflake OBJECT value to an Arrow struct.
    /// </summary>
    /// <param name="objectValue">The OBJECT value as JSON string.</param>
    /// <returns>A dictionary representing the object structure.</returns>
    public Dictionary<string, object?>? ConvertObject(string? objectValue)
    {
        if (string.IsNullOrEmpty(objectValue))
            return null;

        try
        {
            using var document = JsonDocument.Parse(objectValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Expected JSON object.");

            var result = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = ConvertJsonElement(property.Value);
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse OBJECT value: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts a Snowflake ARRAY value to an Arrow list.
    /// </summary>
    /// <param name="arrayValue">The ARRAY value as JSON string.</param>
    /// <returns>A list representing the array elements.</returns>
    public List<object?>? ConvertArray(string? arrayValue)
    {
        if (string.IsNullOrEmpty(arrayValue))
            return null;

        try
        {
            using var document = JsonDocument.Parse(arrayValue);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Expected JSON array.");

            var result = new List<object?>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                result.Add(ConvertJsonElement(element));
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse ARRAY value: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds an Arrow struct type from a Snowflake OBJECT schema.
    /// </summary>
    /// <param name="sampleObject">A sample object to infer the schema from.</param>
    /// <returns>An Arrow struct type.</returns>
    public StructType BuildStructTypeFromObject(Dictionary<string, object?> sampleObject)
    {
        if (sampleObject == null)
            throw new ArgumentNullException(nameof(sampleObject));

        var fields = sampleObject.Select(kvp =>
        {
            var fieldType = InferArrowType(kvp.Value);
            return new Field(kvp.Key, fieldType, nullable: true);
        }).ToList();

        return new StructType(fields);
    }

    /// <summary>
    /// Builds an Arrow list type from a Snowflake ARRAY schema.
    /// </summary>
    /// <param name="sampleArray">A sample array to infer the schema from.</param>
    /// <returns>An Arrow list type.</returns>
    public ListType BuildListTypeFromArray(List<object?> sampleArray)
    {
        if (sampleArray == null)
            throw new ArgumentNullException(nameof(sampleArray));

        // Infer element type from first non-null element
        IArrowType elementType = StringType.Default; // Default to string

        foreach (var element in sampleArray)
        {
            if (element != null)
            {
                elementType = InferArrowType(element);
                break;
            }
        }

        return new ListType(elementType);
    }

    private object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => throw new NotSupportedException($"JSON value kind {element.ValueKind} is not supported.")
        };
    }

    private IArrowType InferArrowType(object? value)
    {
        return value switch
        {
            null => StringType.Default, // Default to string for null
            bool => BooleanType.Default,
            long or int or short or byte => Int64Type.Default,
            double or float or decimal => DoubleType.Default,
            string => StringType.Default,
            List<object?> => new ListType(StringType.Default),
            Dictionary<string, object?> dict => BuildStructTypeFromObject(dict),
            _ => StringType.Default
        };
    }

    /// <summary>
    /// Converts an Arrow value back to Snowflake JSON format.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>A JSON string representation.</returns>
    public string? ConvertToSnowflakeJson(object? value)
    {
        if (value == null)
            return null;

        try
        {
            return JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = false
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to serialize value to JSON: {ex.Message}", ex);
        }
    }
}
