using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apache.Arrow;
using AdbcDrivers.Snowflake.Native.Services.Authentication;
using AdbcDrivers.Snowflake.Native.Services.Transport;
using Ipc = Apache.Arrow.Ipc;

namespace AdbcDrivers.Snowflake.Native.Services.Query;

internal sealed class ChunkedArrowArrayStream : Ipc.IArrowArrayStream
{
    private readonly Channel<PrefetchedChunk> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _prefetchTask;
    private Ipc.ArrowStreamReader? _currentReader;
    private Stream? _currentStream;
    private bool _disposed;

    public Schema Schema { get; }

    private ChunkedArrowArrayStream(
        Schema schema,
        Ipc.ArrowStreamReader firstReader,
        Stream firstStream,
        Channel<PrefetchedChunk> channel,
        CancellationTokenSource cts,
        Task prefetchTask)
    {
        Schema = schema;
        _currentReader = firstReader;
        _currentStream = firstStream;
        _channel = channel;
        _cts = cts;
        _prefetchTask = prefetchTask;
    }

    public static async Task<ChunkedArrowArrayStream> CreateAsync(
        IRestApiClient apiClient,
        AuthenticationToken authToken,
        string? rowSetBase64,
        List<ChunkInfo>? chunks,
        Dictionary<string, string>? chunkHeaders,
        string? qrmk,
        CancellationToken cancellationToken,
        int prefetchConcurrency = 10)
    {
        var chunkUrls = (chunks ?? []).Select(c => c.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

        Stream firstStream;
        Ipc.ArrowStreamReader firstReader;

        if (!string.IsNullOrEmpty(rowSetBase64))
        {
            var arrowBytes = Convert.FromBase64String(rowSetBase64);
            firstStream = new MemoryStream(arrowBytes);
            firstReader = new Ipc.ArrowStreamReader(firstStream);
        }
        else if (chunkUrls.Count > 0)
        {
            var url = chunkUrls[0];
            chunkUrls.RemoveAt(0);
            firstStream = await apiClient.GetArrowStreamAsync(url, authToken, chunkHeaders, qrmk, cancellationToken).ConfigureAwait(false);
            firstReader = new Ipc.ArrowStreamReader(firstStream);
        }
        else
        {
            throw new InvalidOperationException("Arrow result format was requested, but neither rowsetBase64 nor chunks were present.");
        }

        var schema = firstReader.Schema;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bufferSize = Math.Min(chunkUrls.Count, prefetchConcurrency);
        var channel = Channel.CreateBounded<PrefetchedChunk>(new BoundedChannelOptions(Math.Max(bufferSize, 1))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        Task prefetchTask;
        if (chunkUrls.Count > 0)
        {
            prefetchTask = StartPrefetchAsync(apiClient, authToken, chunkHeaders, qrmk, chunkUrls, channel, cts, prefetchConcurrency);
        }
        else
        {
            // No external chunks (the whole result is inline in rowsetBase64). Complete the
            // channel now so that, once the inline batch is consumed, ReadNextRecordBatchAsync's
            // WaitToReadAsync returns false instead of blocking forever waiting for a writer.
            channel.Writer.TryComplete();
            prefetchTask = Task.CompletedTask;
        }

        return new ChunkedArrowArrayStream(schema, firstReader, firstStream, channel, cts, prefetchTask);
    }

    private static Task StartPrefetchAsync(
        IRestApiClient apiClient,
        AuthenticationToken authToken,
        Dictionary<string, string>? chunkHeaders,
        string? qrmk,
        List<string> chunkUrls,
        Channel<PrefetchedChunk> channel,
        CancellationTokenSource cts,
        int maxConcurrency)
    {
        return Task.Run(async () =>
        {
            var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var pendingChunks = new Task<PrefetchedChunk>?[chunkUrls.Count];

            try
            {
                for (int i = 0; i < chunkUrls.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                    var url = chunkUrls[i];
                    pendingChunks[i] = DownloadChunkAsync(apiClient, authToken, chunkHeaders, qrmk, url, semaphore, cts.Token);
                }

                for (int i = 0; i < pendingChunks.Length; i++)
                {
                    var chunk = await pendingChunks[i]!.ConfigureAwait(false);
                    await channel.Writer.WriteAsync(chunk, cts.Token).ConfigureAwait(false);
                    pendingChunks[i] = null; // handed off; the consumer now owns disposal
                }

                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                cts.Cancel();
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // Stop the sibling downloads promptly rather than leaving them running
                // orphaned, and surface the failure to the consumer.
                cts.Cancel();
                channel.Writer.TryComplete(ex);
            }
            finally
            {
                // Settle any downloads not handed to the consumer (siblings still in flight
                // after a failure/cancellation), disposing the buffers they produced so they
                // don't leak and their exceptions don't go unobserved. Only after every
                // download has completed is it safe to dispose the semaphore -- doing so
                // earlier would race the in-flight Release() calls.
                foreach (var pending in pendingChunks)
                {
                    if (pending is null) continue;
                    try { (await pending.ConfigureAwait(false)).Stream.Dispose(); }
                    catch { /* cancelled or failed download -- nothing to dispose */ }
                }

                semaphore.Dispose();
            }
        });
    }

    private static async Task<PrefetchedChunk> DownloadChunkAsync(
        IRestApiClient apiClient,
        AuthenticationToken authToken,
        Dictionary<string, string>? chunkHeaders,
        string? qrmk,
        string url,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        try
        {
            // GetArrowStreamAsync returns as soon as the HTTP headers arrive (and only
            // wraps the live network/gzip stream). Fully buffer the chunk here so the
            // expensive part -- the body transfer + decompression -- happens in parallel
            // across the prefetch workers, not serially on the consumer thread. The
            // consumer then just does CPU-bound Arrow decode from memory.
            using var netStream = await apiClient.GetArrowStreamAsync(url, authToken, chunkHeaders, qrmk, cancellationToken).ConfigureAwait(false);
            var buffer = new MemoryStream();
            await netStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            return new PrefetchedChunk(buffer);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async ValueTask<RecordBatch?> ReadNextRecordBatchAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_currentReader != null)
            {
                var batch = await _currentReader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
                if (batch != null)
                    return batch;
                DisposeCurrentReaderAndStream();
            }

            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            if (!_channel.Reader.TryRead(out var nextChunk))
                return null;

            _currentStream = nextChunk.Stream;
            _currentReader = new Ipc.ArrowStreamReader(_currentStream);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        DisposeCurrentReaderAndStream();
        while (_channel.Reader.TryRead(out var chunk))
            chunk.Stream.Dispose();
        try { _prefetchTask.GetAwaiter().GetResult(); } catch { }
        _cts.Dispose();
    }

    private void DisposeCurrentReaderAndStream()
    {
        try { _currentReader?.Dispose(); } finally { _currentStream?.Dispose(); }
        _currentReader = null;
        _currentStream = null;
    }

    private readonly record struct PrefetchedChunk(Stream Stream);
}
