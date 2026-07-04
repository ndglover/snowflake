# What the 2026-07-01 performance changes actually did

A short explainer for the four changes behind the current benchmark numbers
(native now ≤ interop at 1,000 and 1,000,000 rows). Details/verification in
[code-review-2026-07-01.md](code-review-2026-07-01.md).

## The result path, in one line

```
query POST → JSON response (schema + first rows + chunk URLs)
           → parallel chunk downloads (S3, gzip)
           → Arrow decode → Snowflake type fixups → consumer
```

The changes attacked each stage's dominant waste: allocation on the JSON stage,
scheduling on the download stage, and per-row overhead on the fixup stage.

## 1. JSON responses: deserialize from the stream (`RestApiClient`)

Before: the whole response body was read into a **string**, then parsed. The first query
response embeds the initial rows as multi-megabyte base64, so every query allocated the payload
twice (UTF-8 → UTF-16 string ≈ 2× bytes, straight onto the large-object heap) before parsing.
Also, `HttpClient` was silently buffering the entire body a *third* time before we read it,
because the request used the default completion option.

After: `HttpCompletionOption.ResponseHeadersRead` + `JsonSerializer.DeserializeAsync(stream)` —
bytes flow network → decompressor → parser with no intermediate copies. Less allocation means
less GC, which is what actually shows up at the 1M-row scale.

## 2. Chunk downloads: sliding window (`ChunkedArrowArrayStream`)

Before: a semaphore capped how many downloads *started* together, but a slot was freed the moment
a download **finished** — so with a consumer any slower than the network, every chunk of the
result set could end up buffered in memory at once (unbounded).

After: a chunk holds its window slot from launch until the consumer-side channel accepts it.
Resident chunks are hard-bounded at ~2× `prefetch_concurrency`; a slow consumer now back-pressures
the downloads. Trade-off: with hours-long consumption, late chunks download late and their
presigned URLs could expire — accepted and documented; the old design only avoided this by
buffering everything.

## 3. Chunk buffers: pre-sized (`DownloadChunkAsync`)

Before: each chunk was copied into a `MemoryStream()` that grows by doubling — a multi-MB chunk
re-allocates and re-copies its buffer several times, all on the large-object heap.

After: Snowflake tells us the size up front (`ChunkInfo.uncompressedSize`, previously parsed and
ignored), so the buffer is allocated once at exactly the right size.

## 4. Type fixups: per-column dispatch instead of per-row (`SnowflakeResultArrowStream`)

This layer rewrites Snowflake's wire shapes into stable Arrow types (FIXED integers → the
precision-declared type, TIME/TIMESTAMP → nanoseconds). It runs **per row × per transformed
column**, so per-row overhead multiplies by millions.

Before, each row paid: a type-pattern-match (`ReadInteger`'s switch), a `Nullable<T>` round-trip
(`GetValue(i)!.Value`), one or two delegate invocations, an unsized builder append, and a
bit-by-bit validity append.

After: the array's concrete type is resolved **once per column**, then a tight loop runs over the
raw value span (`ReadOnlySpan<T>`, generic-math cores the JIT specializes per width — no boxing,
no dispatch, no nullable). Output buffers are pre-sized. Columns with no nulls skip validity
construction entirely; null-carrying columns clone the bitmap in one pass. Null slots in the
long-producing paths are computed branch-free (their garbage is masked by the validity bitmap);
only the checked Int32-narrowing path skips them, so garbage can't spuriously overflow.

## Why the 100-row case doesn't move

Small results are dominated by connect + login + one HTTPS round trip; decode and chunk scheduling
are irrelevant at that size. The remaining gap to interop there is connection-establishment
overhead (login handshake, first-request warmup), not the data path.

## Measurement honesty

Wall-clock over a live network has heavy run-to-run variance (the unchanged Go driver drifted
2.0–2.9s across the same day). Cross-checks used: interleaved native/interop runs (same minutes,
same environment) and 5-run averages. Correctness is pinned by 146 offline + 27 live decode tests,
including new deterministic null/value-math coverage added with change 4.
