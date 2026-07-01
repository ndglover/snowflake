# Native C# Snowflake ADBC Driver — TODO

Issues to resolve before considering the driver production-ready. Tiered by impact.
Last reconciled 2026-07-01.

## Tier 1 — Blockers (correctness / leaks / missing core)

_None outstanding._

## Tier 2 — Important (robustness / correctness)

- [ ] **Transactions — `NotImplementedException`** (`SnowflakeConnection.Commit`/`Rollback`) — autocommit only. Blocker *if* consumers need explicit transactions (scope decision).
- [ ] **Multi-row / array bind (`executemany`) not supported** — `ConvertArrowBatchToParameters` binds only **row 0** of a bound `RecordBatch` (`ToBinding(column, 0)`); extra rows are silently dropped. Snowflake's bind protocol supports array bindings (value-per-row), so a consumer binding a multi-row batch for a batch insert would silently lose data. Bind every row (array bindings) or reject multi-row batches loudly.
- [ ] **Pool waiters aren't woken when a connection is released to idle** — `ReleaseConnection` releases the `CapacitySemaphore` permit only when a connection is *discarded* (invalid/faulted); a healthy connection returned to the idle stack keeps holding its permit. So a caller blocked in `AcquireConnectionAsync`'s `WaitAsync` isn't unblocked by a release-to-idle — it waits out the full `AcquireTimeout` and fails even though an idle connection is available (only a *fresh* acquire reuses it via the fast path). The `AcquireTimeout` backstop stops the old infinite hang, but under contention this wastes the wait. Fix needs a semaphore-accounting rework (permits count active connections only and are released on every return, or signal waiters directly on release).

## Tier 3 — Hygiene / decisions

- [ ] **Login `ExpiresAt` tracks master (4h), not session (~1h)** — so `PooledConnection.IsTokenExpired` is inaccurate (reactive renewal masks it, but pool eviction is built on a wrong number). Capture the session `validityInSeconds`.
- [ ] **`tls_skip_verify` footgun** — wires `DangerousAcceptAnyServerCertificateValidator`; make it hard to enable accidentally (warn/log loudly; document test-only). (Option key itself is now correct — see option-name audit.)
- [ ] **Option-name conformance audit vs the ADBC Snowflake driver reference** — verified against the [driver docs](https://arrow.apache.org/adbc/current/driver/snowflake.html), gosnowflake `Config`, and connector-net's connection-properties reference. **Done:** TLS-skip → `…client_option.tls_skip_verify`; query timeout → `…client_option.request_timeout` (+ new `…client_option.login_timeout` bounding the auth round trip); chunk prefetch → `adbc.snowflake.rpc.prefetch_concurrency`; pool keys moved to our own `adbc.snowflake.pool.*` namespace (`max_size`/`idle_timeout`/`acquire_timeout`/`max_lifetime`); `no_proxy` kept as `adbc.snowflake.sql.client_option.no_proxy`. `keep_session_alive_heartbeat_frequency` + clamp already match gosnowflake. **Remaining — missing official options as scope calls:** `region`, `client_timeout`/`jwt_expire_timeout`, `okta_url`, `ocsp_fail_open_mode`, ingest (`adbc.snowflake.statement.ingest_*`), `use_high_precision`, `max_timestamp_precision`, `adbc.rpc.result_queue_size`; also `enable_compression` is still a bare non-standard key.
- [ ] **SsoAuthenticator hardcodes port 8080** — no fallback if in use; use an OS-assigned port or try several.
- [ ] **`AdbcConnection.SetOption`/`GetOption` are inert** — `SnowflakeConnection.SetOption` just stashes into a dict; nothing reads it. Blocks the mutable-after-connect half of the ADBC connection options. **(P1 done:** `adbc.connection.catalog`/`db_schema` are honored at Connect, mapped to the current database/schema, canonical winning over the `adbc.snowflake.sql.*` alias.**) P2:** make `catalog`/`db_schema` settable/gettable on a live connection — needs decoupling the connection's *current* catalog/schema from the *establishment* db/schema that feeds `GeneratePoolKey` + `PooledConnection.Config` (today one shared `ConnectionConfig` instance is both), and deciding pool-reuse semantics (reset session state on checkout vs match on current). `autocommit`/`readonly`/`isolation` are the transactions item (Tier 2).
- [ ] **No consumer logging path** — `ILogger` is used internally but there's no easy way for a consumer to plug in logging (hit this with the session harness). Needed for prod diagnostics.
- [ ] **Pool statistics are computed but never surfaced** — `ConnectionPoolManager.GetStatisticsAsync`/`PoolStatistics` and the counters behind them (`_totalConnectionsCreated/Closed/Reuses`, `PendingRequests`) are maintained everywhere but have no caller (a conventional pool-metrics surface carried over, not wired up). Review alongside the logging/observability work: either expose them (metrics/`ILogger`/an ADBC option) or remove the DTO + counters as dead weight.
- [ ] **Secret-logging audit** — confirm no token is ever logged at any level.
- [ ] **Public methods should surface `AdbcException`** — the executive paths already wrap internal exceptions, and the public not-implemented sites (`Commit`/`Rollback`/`GetParameterSchema`) now throw `AdbcException.NotImplemented`. Stragglers: the metadata methods (`GetTableSchema`/`GetObjects`/`GetInfo`) still throw raw `InvalidOperationException("Connection is not properly initialized")` guards (never fire in practice, but they'd leak the wrong type). Fold those into `AdbcException` for a clean "all public failures are `AdbcException`" contract.

## Scope decisions (confirm whether in scope for v1)

- [ ] PUT/GET stage file transfer, multi-statement, async queries, stored-procedure result handling — currently unsupported.

## Resolved

- [x] **Master-token expiry recovery (auto-heartbeat)** — opt-in via `CLIENT_SESSION_KEEP_ALIVE` (`adbc.snowflake.sql.client_option.keep_session_alive`, frequency clamped to [15m, 1h], default 1h). The pool's background loop heartbeats due **idle** connections (`POST /session/heartbeat`, renew-on-390112), which re-mints both the session and master tokens and rolls the ~4h master ceiling forward. Idle-only so a heartbeat never races the reactive renewal of an in-flight query. All pool timekeeping (idle/lifetime/expiry/heartbeat) runs on an injectable `TimeProvider`, so the loop is unit-tested with `FakeTimeProvider`. Verified live: 60m heartbeats kept an idle session alive **13h — 3× past the original 4h master window** (harness run, 2026-06-30). Renewal is lock-guarded ("renew only if the token is still the expired one") to dedupe concurrent renewals.
- [x] **Query cancellation** — `QueryExecutor.CancelQueryAsync` now aborts via `POST /queries/v1/abort-request`, keyed on the original **requestId** (not queryId) and authenticated with the session token — matching gosnowflake/connector-net. `QueryRequest.RequestId` lets a statement submit with a known id; `SnowflakeStatement.Cancel()` (overriding `AdbcStatement.Cancel`) aborts the in-flight request from another thread. Verified live: `StatementTests.CanCancelRunningQuery` cancels a `SYSTEM$WAIT` query and gets back a Snowflake cancellation error.
- [x] **Bind-parameter type coverage** — `ToBinding` is keyed off the Arrow array type with correct Snowflake bind formats: DATE = ms since epoch, TIME = ns of day, TIMESTAMP_NTZ/LTZ = ns since epoch, BINARY = lower hex, DECIMAL → FIXED string. It now covers the same scalar set as `ConvertArrowTypeToSnowflake` (describe): Bool, Int8/16/32/64, UInt8/16/32/64, Float, Double, Decimal128/256, String, Binary, Date32/64, Time32/64, Timestamp NTZ/LTZ. Typed nulls keep their column bind type; unmapped types (List/Struct, etc.) throw `NotSupportedException`. Bind cases live in one table (`test/Native/BindCases.cs`) consumed by both the offline format theory (`TypeConverterTests`) and the live round-trip theory (`StatementTests.CanBindParameter`). Known limitation: only row 0 of a bound batch is used — no array/`executemany` binding yet.
- [x] **SnowflakeDatabase disposes its HttpClient** — tracks `_ownsHttpClient` (true only when it created the client, not when injected) and disposes it in `Dispose()` — after the pool, so in-flight session-closes still have a live client.
- [x] **Renewal not synchronized** — renewal now runs under a per-connection `SemaphoreSlim` and skips if the session token already changed (gosnowflake's "renew only if still the expired token" guard), so concurrent statements don't double-renew.
- [x] **Orphaned server sessions** — `PooledConnection.Dispose` now best-effort closes the session (`POST /session?delete=true`) via a closer wired from `SnowflakeDatabase` through the pool; fires when the pool discards a connection (incl. pool/database dispose).
- [x] **SnowflakeStatement double-wraps exceptions** — added `catch (AdbcException) { throw; }` in `ExecuteQueryAsync` (matching `ExecuteUpdateAsync`).
- [x] **401-on-token-expiry concern** — resolved by evidence: an 8h-idle harness run showed both session-expiry (390112) and master-expiry (390114) come back as **HTTP 200 + GS code in the body**, so `EnsureSuccessStatusCode` doesn't pre-empt detection. (A 401 path may still exist for other auth failures, but not for the token-expiry/renewal flow.)
- [x] **RequestBuilder returns a typed model** — now returns `SnowflakeQueryRequestBody`/etc.
- [x] **ClientTests / ClientIntegrationTests overlap** — `ClientIntegrationTests` deleted; ClientTests rewritten to the ADO.NET client layer.
- [x] **`TypeConversion.ParameterSet` nullability** — now `Dictionary<string, SnowflakeBinding>` (positional bind keys + typed values).
- [x] **Reactive session renewal** — implemented (`QueryExecutor.PostQueryWithRenewalAsync`) and verified live over 6+ hours.
- [x] **GetObjects SQL injection** — switched to bind parameters.
- [x] **Result type decoding** — precision-driven NUMBER, scaled decimal, TIME, TIMESTAMP; describe↔result reconciled.

## Notes

- `SnowflakeConnection` comment typo (`AdbcDatabaseAdbcDatabase`, `SnowflakeConnection.cs:98`) — trivial, fold into the next pass over that file.
