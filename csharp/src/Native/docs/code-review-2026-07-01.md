# Native Driver Review — Dead Code, Cleanup, Performance

Date: 2026-07-01. Scope: `csharp/src/Native` (~7,800 lines), excluding gaps already tracked in
[TODO.md](TODO.md) (transactions, multi-row bind, pool-waiter wakeup, SSO port, observability,
pool statistics, scope-decision options).

Method: full read of the transport / chunk-streaming / result-decode hot paths plus the
connection/pool/statement layers; caller analysis (src + tests) for every suspect API. **No
profiler was run** — the performance findings are code-level analysis, ranked by expected impact;
the existing `BenchmarkTests` (Native vs Interop, 5-run protocol in
[benchmark-results.md](benchmark-results.md)) should be used to measure before/after any change.

---

## 1. Dead code — ✅ REMOVED (D1–D8, D10 swept 2026-07-01; ~930 lines incl. tests)

Extra finds during the sweep, also removed: `QueryRequest.Format` + the `ResultFormat` enum (never
read — the result format is forced via session params). D9 (`TokenType` removed; `AccessToken`
collapsed into `SessionToken`, keeping the login null-check) and D11
(`PreparedStatement.ParameterSchema` removed, rationale moved to the class doc) landed in the
step-6 pass.
`docs/design.md` still shows the original interface sketch including removed members — historical,
left as-is.

Original findings, confirmed by caller analysis. "Tests-only" = no production caller; the only
references were unit tests exercising the dead member itself.

| # | What | Where | Evidence |
|---|------|-------|----------|
| D1 | **`SemiStructuredConverter` — entire class** (~220 lines) | `Services/TypeConversion/SemiStructuredConverter.cs` | Zero references anywhere, src or tests. VARIANT/OBJECT/ARRAY values arrive through the normal Arrow path as strings; this JSON converter is never invoked. |
| D2 | **`TypeConverter.ConvertSnowflakeResultToArrow` + all `Build*Array` helpers** (~230 lines: `BuildArrowArray`, `BuildBooleanArray`, `BuildFixedArray`, `BuildInt32Array`, `BuildInt64Array`, `BuildDecimalArray`, `BuildFloatArray`, `BuildDoubleArray`, `BuildStringArray`, `BuildBinaryArray`, `BuildDateArray`, `BuildTimeArray`, `BuildTimestampArray`) | `TypeConverter.cs:162–488` | Tests-only. The JSON-rowset→Arrow path it implements is never used: query results always come back as Arrow (we force `DOTNET_QUERY_RESULT_FORMAT=ARROW`), and the JSON metadata path (`GetObjects`) reads string cells directly. |
| D3 | **`TypeConverter.ConvertArrowTypeToSnowflake`** | `TypeConverter.cs:112–159` | Tests-only. The Arrow→Snowflake *type-name* direction has no production caller (binding uses `ToBinding`; describe uses `ConvertSnowflakeTypeToArrow`). Removing it removes the "bind/describe symmetry" reference point we used when building `ToBinding` — the symmetry is now locked by `BindCases` tests instead, so this is safe. |
| D4 | **`QueryExecutor.ExecutePreparedStatementAsync`** + `IQueryExecutor` declaration | `QueryExecutor.cs:459–468` | Zero references. Also **broken**: it wraps each already-typed `SnowflakeBinding` inside a *new* TEXT binding (`new SnowflakeBinding(BindTypeNames.Text, kvp.Value)` where `kvp.Value` is itself a `SnowflakeBinding`) — it would double-encode if ever called. |
| D5 | **Duplicate `ParameterSet` class** (with dead `ParameterBatch` property) | `IQueryExecutor.cs:243` | Two `ParameterSet` classes exist. The live one is `Services/TypeConversion/ParameterSet.cs` (`Dictionary<string, SnowflakeBinding>`). The one in `IQueryExecutor.cs` (`Dictionary<string, object>` + `RecordBatch? ParameterBatch`) is referenced only by dead D4. Remove with D4. |
| D6 | **`IRestApiClient.GetAsync<T>` + implementation** | `IRestApiClient.cs:75`, `RestApiClient.cs:136–154` | Zero references. Every Snowflake API call we make is a POST; chunk downloads use `GetArrowStreamAsync`. |
| D7 | **`RequestBuilder.BuildMetadataRequest` + `SnowflakeMetadataRequestBody`** | `RequestBuilder.cs:116–137`, `SnowflakeRequestBodies.cs:108` | Tests-only. Metadata (`GetObjects`) is implemented via `INFORMATION_SCHEMA` SQL, not a metadata endpoint. |
| D8 | **`QueryResult.Metadata`**, **`QueryError.SqlState`/`LineNumber`/`ColumnNumber`**, **`QueryStatus.Queued`/`Running`** | `IQueryExecutor.cs` | Never read/assigned anywhere. `QueryResult.Metadata` also allocates a `Dictionary` per query result for nothing. |
| D9 | **`AuthenticationToken.TokenType`** | `AuthenticationToken.cs` | Written twice (`"Snowflake"`, `"Bearer"`), never read. **`AccessToken`** is near-dead: its only read is the fallback `token.SessionToken ?? token.AccessToken` in `RestApiClient.ConfigureRequest`, but login always sets both to the same value, so the fallback can never differ. Consider collapsing to `SessionToken` only. |
| D10 | **`SnowflakeQueryResponse.Total`, `.Parameters` (+ `NameValueParameter` class), `.SqlState`** | `SnowflakeQueryResponse.cs:35,50,56,104` | Deserialized on every query response, never read. Removing them also skips their parse cost. (Keep `Returned` — used for row counts.) |
| D11 | **`PreparedStatement.ParameterSchema`** | `IQueryExecutor.cs` | Always `null` by design (documented); only assigned `null`. Could be removed with a comment on `PreparedStatement` instead. Low priority. |

**Recommendation:** remove D1–D8 and D10 in one "dead code" commit (with their tests); D9/D11 in a
second, since D9 touches the token type used everywhere and deserves its own diff.

---

## 2. Performance — memory

Ranked by expected impact. M1 is the one with pathological worst-case behavior.

### M1 (High) — ✅ FIXED 2026-07-01 (with M3) — chunk prefetch back-pressure was broken
Reworked to a **sliding window**: a chunk holds its window slot from launch until the channel
accepts it (the semaphore released-at-download-completion is gone entirely), bounding resident
chunks to ~2× `prefetch_concurrency` regardless of consumer speed. M3 folded in: buffers are now
pre-sized from `ChunkInfo.UncompressedSize`. Verified by new offline tests
(`ChunkedArrowArrayStreamTests`): a stalled consumer sees ≤ window+channel+1 downloads (was: all),
order/completeness preserved, dispose-mid-flight unwinds promptly. **Fast-consumer benchmark
(5 runs, Release): 1M rows avg 2,290 ms vs 2,531 ms before — no regression; ~9% faster** (the
pre-sized buffers outweigh any head-of-line effect at real chunk-size distributions).

**Known trade-off for a *really* slow consumer (documented, accepted):** late chunks now download
when the consumer gets there, not eagerly upfront — so multi-hour consumption can hit **expired
presigned chunk URLs** (403, not retried as transient). The old unbounded design masked this by
downloading everything immediately (at unbounded memory cost). If hours-long streaming becomes a
real scenario, the fix is refreshing chunk URLs from the query-result endpoint on expiry.
Session-token expiry is *not* a chunk-path risk (S3 downloads authenticate via chunk headers/qrmk,
not the session token), and `HttpClient.Timeout` applies per download, unaffected by consumer speed.
Original finding below.
`ChunkedArrowArrayStream.StartPrefetchAsync` (`:114–132`) gates *download starts* on a
`SemaphoreSlim(prefetchConcurrency)`, but `DownloadChunkAsync` releases the permit in its
`finally` — i.e. **when the download completes**, not when the chunk is handed to the consumer.
Completed chunks then sit fully buffered in the `pendingChunks` array awaiting in-order handoff,
and the bounded channel only limits the *handed-off* chunks. So if the consumer reads slower than
the network downloads (a real app doing per-batch work — unlike our benchmark, which drains as
fast as possible), the launch loop keeps acquiring freed permits and **every chunk of the result
set ends up resident simultaneously** (for the 1M-row benchmark table: ~191 buffered chunks; for
a 100M-row result: gigabytes).

**Fix:** release the semaphore *after* the chunk is written to the channel (move `Release` out of
`DownloadChunkAsync` into the handoff loop, with careful release on the failure paths). Resident
chunks are then bounded by ~`prefetchConcurrency` (in-flight) + channel capacity ≈ 2× concurrency,
which is the intended design. This is a correctness-of-design fix, not a tuning knob.

### M2 (High) — ✅ FIXED 2026-07-01 — `ReadApiResponseAsync` materialized the whole JSON response as a string
Now `JsonSerializer.DeserializeAsync` straight off the decompressed response stream, with
`PostAsync` switched to `HttpCompletionOption.ResponseHeadersRead` so HttpClient doesn't pre-buffer
the body either (without that, stream deserialization would still read from a full in-memory copy).
H1 (response disposal) applied in the same change — required by `ResponseHeadersRead`. Verified:
136 unit + 23 live tests green, benchmark unchanged. Original finding below.
`RestApiClient.cs:176–185` reads the (decompressed) response into a `string` via `StreamReader`,
then `JsonSerializer.Deserialize`s the string. The first query response carries `rowSetBase64` —
often **megabytes** — so the payload exists simultaneously as: UTF-8 bytes → **UTF-16 string
(2× the bytes, straight onto the LOH)** → parsed DOM with the base64 `string` property → decoded
`byte[]`. The intermediate full-response string is pure waste.

**Fix:** `await JsonSerializer.DeserializeAsync<ApiResponse<T>>(stream, options, ct)` directly on
the response stream. Removes the `StreamReader`, the full-payload string, and one LOH allocation
per query. One-line change plus error-handling.

### M3 (Medium) — ✅ FIXED with M1 — chunk download buffers grew by doubling; size was known in advance
`DownloadChunkAsync` (`:184`) copies into `new MemoryStream()` with no capacity, so a multi-MB
chunk incurs log₂(size) grow-and-copy cycles and repeated LOH churn. Snowflake tells us the size:
`ChunkInfo.UncompressedSize` is deserialized (`SnowflakeQueryResponse.cs:77`) but never used —
only the URL string is plumbed into the prefetcher.

**Fix:** pass `ChunkInfo` (not just `Url`) through `CreateAsync`/`StartPrefetchAsync`/
`DownloadChunkAsync` and pre-size: `new MemoryStream(chunk.UncompressedSize)`. Optionally go
further with `ArrayPool`-backed buffers (e.g. `RecyclableMemoryStream`) so chunk buffers are
reused across the stream instead of allocated/collected per chunk — worthwhile because at
~16 MB/chunk these are all LOH objects.

### M4 (Low) — ✅ FIXED with C1 — decode builders not pre-sized
`SnowflakeResultArrowStream.ToInt32`/`ToInt64`/`RescaleToDecimal` (`:229–273`) use default-capacity
builders that grow by doubling, even though `source.Length` is known. (`BuildLongColumn` already
pre-sizes — these three predate it.) Pass the length to the builder/`Reserve`.

### M5 (Low) — Eager per-result allocations
`QueryResult` news up `Errors` (`List`) and `Metadata` (`Dictionary`) on every construction even
for successes; `Metadata` is dead (D8). Make `Errors` lazily allocated or assign shared empties.

---

## 3. Performance — CPU

### C1 (Medium-High) — ✅ FIXED 2026-07-01 (with M4) — per-row dynamic dispatch in the decode loops
Rewritten as proposed: one concrete-type dispatch **per column** (generic-math cores over
`ReadOnlySpan<T>`, JIT-specialized per width) replacing the per-row `ReadInteger` switch and
`Nullable<T>` round-trips; the per-row delegates in `BuildLongColumn` are gone (the helper itself
deleted); buffers pre-sized (M4, incl. `Decimal128Array.Builder.Reserve`); and all-valid columns
skip validity building entirely (`ArrowBuffer.Empty`), with null-ful columns cloning the bitmap
once via `CloneValidity`. Long-producing paths compute null slots branch-free (unchecked garbage,
masked by validity); the checked Int32 narrowing skips null slots so garbage can't spuriously
overflow. **New offline coverage** (`SnowflakeResultArrowStreamTests`, 7 tests): this layer
previously had live-only coverage — now value math + null handling for FIXED widen/rescale, TIME,
struct and single-int TIMESTAMP, and pass-through are pinned deterministically. Verified: 146 unit
+ 27 live `TypeDecodingTests` green. Benchmark: interleaved Native-vs-Interop control showed the
same ~5% relationship as before the change (environment drift dominates wall-clock; the decode CPU
delta is below network noise). Original finding below.
The column transforms run per row, per batch, on the hottest data path:
- `ReadInteger` (`SnowflakeResultArrowStream.cs:354`) does a **type-pattern-match per row** plus a
  `Nullable<T>` round-trip (`GetValue(i)!.Value`) — the source array type can't change mid-column,
  so this should be resolved **once per column** into a typed span read (`Int32Array.Values` etc.).
- `BuildLongColumn` (`:318`) invokes two delegates per row (`isNull`, `value`), each hiding an
  interface call; and it rebuilds the **validity bitmap bit-by-bit** even though the source array's
  null bitmap could be copied wholesale (buffer slice/copy) when `array.Offset == 0`, which is
  always true for freshly IPC-read batches.

**Fix shape:** per-column, switch once on the concrete array type, then run a tight loop over
`ReadOnlySpan<T>` values writing into a pre-sized `ArrowBuffer.Builder<long>`, and clone the
source validity buffer instead of re-appending bits. Same output, several× less per-row work.
Measure with the 1M benchmark — this only pays off on transformed columns (scaled NUMBER, TIME,
TIMESTAMP), so impact depends on the schema.

### C2 (Medium) — ✅ FIXED 2026-07-01 — reflection-based System.Text.Json for a small, fixed wire model
`SnowflakeJsonContext` (source-generated) now backs the `RestApiClient` path for the closed set of
query-protocol types; the heartbeat's untyped `new object()` body became a typed
`EmptyRequestBody`, and `SnowflakeBinding.Value` narrowed `object?`→`string?` (every producer was
already a string). The login path deliberately stays on reflection web-defaults (once per
connection; relies on case-insensitive matching). Original finding below.
Every request (`JsonContent.Create`) and response (`Deserialize`) uses reflection-based STJ.
The wire model is a closed set of ~15 types. A **source-generated `JsonSerializerContext`** cuts
serializer overhead and first-call warmup, eliminates reflection metadata allocations, and makes
the driver trimming/AOT-safe. Mechanical change; pairs naturally with M2.

### C3 (Low-Medium) — ✅ FIXED 2026-07-01 — `GeneratePoolKey` hashed on every acquire *and* release
`IPooledConnection.PoolKey` now carries the key computed once at creation; `ReleaseConnection` is a
dictionary lookup. Original finding below.
`ConnectionPoolManager.GeneratePoolKey` hashes the credential and joins ~13 fields each call, and
`ReleaseConnection` recomputes it for a connection the pool already keyed once. **Fix:** compute
once per acquire and carry the key on `IPooledConnection` (set at creation), so release is a
dictionary lookup with no hashing. Also avoids the transient key string churn per operation.

### C4 (Low) — ✅ FIXED 2026-07-01 — per-request header re-parsing
Accept / Accept-Encoding / User-Agent values are now `static readonly`. Original finding below.
`ConfigureRequest` (`RestApiClient.cs:187–198`) re-parses three `UserAgent` fragments and
constructs a new `MediaTypeWithQualityHeaderValue` per request. Cache them in `static readonly`
fields (`ProductInfoHeaderValue` / `MediaTypeWithQualityHeaderValue` are immutable-enough to share).

### C5 (Low, metadata path only) — `GetObjects` N+1 queries, sync-over-async, dictionary-per-row
`SnowflakeConnection.GetObjects.cs` issues one `INFORMATION_SCHEMA` query per catalog, then per
schema, then **per table** (columns + constraints) — a deep `GetObjects` on a 500-table schema is
~1,000 sequential round trips, each executed with `.GetAwaiter().GetResult()` (`:388,404`) and
materialized as a `Dictionary<string,string?>` per row (`:412`). Fine for small schemas; painful
for catalog tooling. Fix direction (when it matters): one query per *depth* with `IN`/pattern
filters and group client-side — this is how the Go driver keeps it flat. Flagging, not urgent.

---

## 4. Cleanup / hygiene (non-perf)

| # | Item | Where |
|---|------|-------|
| H1 | ✅ FIXED with M2 — **`HttpResponseMessage` never disposed** on the JSON path; now `using`-scoped in `PostAsync` (and `GetArrowStreamAsync`'s intentional non-disposal is documented at the call site). `ConfigureAwait(false)` also added throughout the file, and `DelayAsync` got its missing `private`. | `RestApiClient.cs` |
| H2 | ✅ FIXED (with a hard-won caveat) — the User-Agent now reports the real driver version, OS, and runtime (leading `.NET/{ver}` token preserved). **But `CLIENT_APP_VERSION` at login must stay `"3.1.0"`:** Snowflake gates server capabilities on the claimed client id+version, and a ".NET" client below the Arrow-capable connector-net version silently gets **JSON results** (confirmed live — switching it to our assembly version broke every result-stream test). Documented at the assignment. | `RestApiClient.cs`, `SnowflakeLoginClient.cs` |
| H3 | ✅ FIXED — `QueryError.Exception` carries the originating exception; `SnowflakeStatement.ToAdbcException` rethrows with it as `InnerException`, so the stack/inner chain survives. | `QueryExecutor.cs`, `SnowflakeStatement.cs` |
| H4 | ✅ FIXED — `TypeConverter.Shared` used at all three sites. | |
| H5 | ✅ FIXED (with M2) — `DelayAsync` is `private`; `_renewLock` non-disposal accepted (no wait handle allocated — benign). | |
| H6 | Retry parity note: gosnowflake appends `retryCount`/`clientStartTime` to retried query URLs so the server can distinguish retries; we resend the identical URL (same `requestId`, which Snowflake dedups — correct, just less observable). Optional parity tweak. | `RestApiClient.cs:200` |

Already tracked in TODO (not repeated here): pool statistics surface-or-remove, the
`AdbcDatabaseAdbcDatabase` typo, observability, SSO port, transactions, multi-row bind,
pool-waiter wakeup.

---

## 5. Order of attack — ✅ ALL STEPS COMPLETE (2026-07-01)

1. ✅ **Dead-code sweep** (D1–D8, D10 + extras; D9/D11 in step 6): ~950 lines gone.
2. ✅ **M2 + H1** (stream deserialization + response disposal).
3. ✅ **M1** (prefetch back-pressure, sliding window + slow-consumer tests).
4. ✅ **M3** (pre-sized chunk buffers).
5. ✅ **C1 + M4** (decode loop despecialization + new offline decode tests).
6. ✅ **C2, C3, C4, H2–H5, D9, D11.**

Verified after step 6: 146 unit + **77 live integration** tests green. User-measured benchmark:
native ≤ interop at 1,000 (0.7×) and 1,000,000 rows (0.86×). Remaining (deliberately not done):
**C5** (`GetObjects` N+1 — metadata path, flagged for when catalog tooling matters) and **H6**
(optional `retryCount` URL parity on retries). See also
[perf-explainer-2026-07-01.md](perf-explainer-2026-07-01.md) for a plain-language account of the
performance changes.
