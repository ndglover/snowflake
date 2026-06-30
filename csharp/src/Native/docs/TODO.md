# Native C# Snowflake ADBC Driver — TODO

Issues to resolve before considering the driver production-ready. Tiered by impact.
Last reconciled 2026-06-29.

## Tier 1 — Blockers (correctness / leaks / missing core)

- [ ] **PooledConnection doesn't close the Snowflake session** — `Dispose()` sends no `DELETE /session`, so every discarded connection leaks a live server-side session until it times out (~4h). Under connection churn this hits account session limits. Close on the pool's discard path (`PooledConnection.Dispose`). *(in progress)*
- [ ] **Master-token expiry recovery** — Reactive renewal works (verified live), but once the master expires (idle > ~4h) the renewal POST returns **`390114`** and the query fails (confirmed live, 8h harness run). **Reference-aligned fix (NOT re-login):** both gosnowflake and connector-net just propagate the renewal error on master expiry — neither auto-re-logins. The reference recovery is (a) **auto-heartbeat** gated on `CLIENT_SESSION_KEEP_ALIVE` to *prevent* reaching expiry (background timer, renew-on-390112, endpoint already built), and (b) surface `390114` as an identifiable error so the app/pool reconnects (the pool should already evict the connection on release via `IsTokenExpired`). Also adopt gosnowflake's renewal **lock** + "renew only if the token is still the expired one" to dedupe concurrent renewals (covers the unsynchronized-token-mutation item below).
- [ ] **Query cancellation — `NotImplementedException`** (`QueryExecutor.CancelQueryAsync`) — no way to abort a runaway query server-side.

## Tier 2 — Important (robustness / correctness)

- [ ] **Transactions — `NotImplementedException`** (`SnowflakeConnection.Commit`/`Rollback`) — autocommit only. Blocker *if* consumers need explicit transactions (scope decision).
- [ ] **Bind-parameter type coverage** — `TypeConverter.ToBinding` maps only TEXT/FIXED/REAL/BOOLEAN; DATE/TIME/TIMESTAMP/DECIMAL/BINARY fall through to `Convert.ToString` as TEXT (silently wrong — a `byte[]` binds as `"System.Byte[]"`). Implement them or throw `NotSupportedException` instead of binding garbage.
- [ ] **SnowflakeDatabase doesn't dispose its HttpClient** — when it creates its own (not injected), track ownership and dispose it in `Dispose()`.

## Tier 3 — Hygiene / decisions

- [ ] **`AuthenticationService._tokenCache` is never populated** — dead code; implement caching or remove.
- [ ] **Login `ExpiresAt` tracks master (4h), not session (~1h)** — so `PooledConnection.IsTokenExpired` is inaccurate (reactive renewal masks it, but pool eviction is built on a wrong number). Capture the session `validityInSeconds`.
- [ ] **Inconsistent exception types across authenticators** — `BasicAuthenticator` throws `AdbcException`, others `InvalidOperationException`. Standardize on `AdbcException`.
- [ ] **`ssl_skip_verify` footgun** — wires `DangerousAcceptAnyServerCertificateValidator`; make it hard to enable accidentally (warn/log loudly; document test-only).
- [ ] **SsoAuthenticator hardcodes port 8080** — no fallback if in use; use an OS-assigned port or try several.
- [ ] **No consumer logging path** — `ILogger` is used internally but there's no easy way for a consumer to plug in logging (hit this with the session harness). Needed for prod diagnostics.
- [ ] **Secret-logging audit** — confirm no token is ever logged at any level.
- [ ] **GetParameterSchema — `NotImplementedException`** — by design (Snowflake doesn't report bind types); document it rather than leave it looking unfinished.

## Scope decisions (confirm whether in scope for v1)

- [ ] PUT/GET stage file transfer, multi-statement, async queries, stored-procedure result handling — currently unsupported.

## Resolved

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

- `SnowflakeConnection` comment typo (`AdbcDatabaseAdbcDatabase`) and `ConnectionPoolEntry` primary-ctor + mutable field — trivial, fold into the next pass over those files.
