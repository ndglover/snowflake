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

using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Apache.Arrow.Adbc.Tracing;
using Apache.Arrow.Ipc;

namespace AdbcDrivers.Snowflake.Native;

/// <summary>
/// Wraps a result stream so each batch read is spanned on the statement's trace — making
/// fetch time (chunk downloads included) visible separately from execution time. One span
/// per batch, tagged with the batch's row count.
/// </summary>
internal sealed class SnowflakeTracingReader : TracingReader
{
    private readonly SnowflakeStatement _statement;
    private readonly IArrowArrayStream _inner;

    public SnowflakeTracingReader(SnowflakeStatement statement, IArrowArrayStream inner)
        : base(statement)
    {
        _statement = statement;
        _inner = inner;
    }

    public override string AssemblyName => _statement.AssemblyName;

    public override string AssemblyVersion => _statement.AssemblyVersion;

    public override Schema Schema => _inner.Schema;

    public override async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        return await this.TraceActivityAsync(async activity =>
        {
            RecordBatch? batch = await _inner.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch != null)
                activity?.SetTag(SemanticConventions.Db.Response.ReturnedRows, batch.Length);
            return batch;
        }, activityName: "ReadNextRecordBatch").ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
