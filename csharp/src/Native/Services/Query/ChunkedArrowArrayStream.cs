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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
using AdbcDrivers.Snowflake.Native.Services.Transport;
using Ipc = Apache.Arrow.Ipc;

using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Query;

internal sealed class ChunkedArrowArrayStream : Ipc.IArrowArrayStream
{
    private readonly IRestApiClient _apiClient;
    private readonly AuthenticationToken _authToken;
    private readonly Dictionary<string, string>? _chunkHeaders;
    private readonly string? _qrmk;
    private readonly Queue<string> _chunkUrls;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream _currentStream;
    private Ipc.ArrowStreamReader _currentReader;
    private bool _disposed;

    private ChunkedArrowArrayStream(
        IRestApiClient apiClient,
        AuthenticationToken authToken,
        Dictionary<string, string>? chunkHeaders,
        string? qrmk,
        Queue<string> chunkUrls,
        Stream currentStream,
        Ipc.ArrowStreamReader currentReader)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(authToken);
        ArgumentNullException.ThrowIfNull(chunkUrls);
        ArgumentNullException.ThrowIfNull(currentStream);
        ArgumentNullException.ThrowIfNull(currentReader);

        _apiClient = apiClient;
        _authToken = authToken;
        _chunkHeaders = chunkHeaders;
        _qrmk = qrmk;
        _chunkUrls = chunkUrls;
        _currentStream = currentStream;
        _currentReader = currentReader;
    }

    public static async Task<ChunkedArrowArrayStream> CreateAsync(
        IRestApiClient apiClient,
        AuthenticationToken authToken,
        string? rowSetBase64,
        List<ChunkInfo>? chunks,
        Dictionary<string, string>? chunkHeaders,
        string? qrmk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(authToken);

        var chunkUrls = new Queue<string>((chunks ?? []).Select(c => c.Url).Where(u => !string.IsNullOrWhiteSpace(u)));

        Stream stream;
        Ipc.ArrowStreamReader reader;

        if (!string.IsNullOrEmpty(rowSetBase64))
        {
            var arrowBytes = Convert.FromBase64String(rowSetBase64);
            stream = new MemoryStream(arrowBytes);
            reader = new Ipc.ArrowStreamReader(stream);
        }
        else if (chunkUrls.Count > 0)
        {
            var url = chunkUrls.Dequeue();
            stream = await apiClient.GetArrowStreamAsync(
                url,
                authToken,
                chunkHeaders,
                qrmk,
                cancellationToken).ConfigureAwait(false);
            reader = new Ipc.ArrowStreamReader(stream);
        }
        else
        {
            throw new InvalidOperationException("Arrow result format was requested, but neither rowsetBase64 nor chunks were present.");
        }

        return new ChunkedArrowArrayStream(
            apiClient,
            authToken,
            chunkHeaders,
            qrmk,
            chunkUrls,
            stream,
            reader);
    }

    public Schema Schema => _currentReader.Schema;

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = await _currentReader.ReadNextRecordBatchAsync(cancellationToken);
                if (batch != null)
                    return batch;

                if (_chunkUrls.Count == 0)
                    return null;

                DisposeCurrentReaderAndStream();

                var nextUrl = _chunkUrls.Dequeue();
                _currentStream = await _apiClient.GetArrowStreamAsync(
                    nextUrl,
                    _authToken,
                    _chunkHeaders,
                    _qrmk,
                    cancellationToken).ConfigureAwait(false);
                _currentReader = new Ipc.ArrowStreamReader(_currentStream);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeCurrentReaderAndStream();
        _gate.Dispose();
    }

    private void DisposeCurrentReaderAndStream()
    {
        try
        {
            _currentReader?.Dispose();
        }
        finally
        {
            _currentStream?.Dispose();
        }
    }
}
