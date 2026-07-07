# Performance Benchmark: Native C# vs Interop (Go) Driver

Date: 2026-07-01 — re-run after the auth / pool / token-lifecycle / option-naming updates.
Supersedes the 2026-06-19 baseline (which predated chunk prefetching).

## Setup

- **Hardware:** Intel Core Ultra 9 285H (16 cores / 16 threads), 32 GB RAM, Windows 11.
- **Network:** internet downlink measured at ~200–240 Mbps (50 MB probe transfers via a CDN speed
  endpoint, measured 2026-07-07); default warehouse.
- **Query:** configured via `query` in the config JSON; result set has **8 columns**.
- **Build:** .NET 8.0, **Release**, `net8.0` target for both drivers (apples-to-apples).
- **Go driver:** loaded via the Interop CGo wrapper (`driverPath` in config).
- **TLS:** `tls_skip_verify` enabled on both.
- **Measurement:** end-to-end **execute + stream all Arrow batches** — identical harness; only the
  `[NATIVE]`/`[INTEROP]` label and the driver under test differ.

## Results (5 runs each, milliseconds)

### Native C# driver
| Run | 100 | 1,000 | 1,000,000 |
|-----|-----|-------|-----------|
| 1   | 260 | 224   | 2185      |
| 2   | 189 | 223   | 2618      |
| 3   | 132 | 220   | 2728      |
| 4   | 163 | 214   | 2484      |
| 5   | 294 | 318   | 2639      |
| **Avg** | **208** | **240** | **2531** |

### Interop (Go via CGo) driver
| Run | 100  | 1,000 | 1,000,000 |
|-----|------|-------|-----------|
| 1   | 98   | 269   | 2585      |
| 2   | 80   | 241   | 2216      |
| 3   | 102  | 249   | 2362      |
| 4   | 89   | 232   | 2291      |
| 5   | 592* | 235   | 2047      |
| **Avg** | **192** | **245** | **2300** |

\* Outlier (warehouse/connection cold-start); the median 100-row is ~98 ms.

## Summary
| Rows | Native avg | Interop avg | Winner |
|------|-----------|-------------|--------|
| 100 | 208 ms | 192 ms (~98 median) | Interop / ~equal |
| 1,000 | 240 ms | 245 ms | ~equal |
| 1,000,000 | 2,531 ms | 2,300 ms | Interop (~9% faster) |

## Update 2026-07-06 — after the full performance pass

Re-measured (5-run means) after the decode-loop rewrite, prefetch back-pressure fix, stream JSON
deserialization, and source-generated serializer landed:

| Rows | Native avg | Interop avg | Native / Interop |
|------|-----------|-------------|------------------|
| 100 | 144 ms | 78 ms | 1.84× |
| 1,000 | 206 ms | 296 ms | **0.70×** |
| 1,000,000 | 2,491 ms | 2,912 ms | **0.86×** |

Native now leads at 1,000 and 1,000,000 rows; the 100-row gap is connection-establishment
overhead (login handshake), not the data path. These supersede the 2026-07-01 tables below for
"current state"; the older tables remain as the pre-optimization record.

## Key findings

1. **Correctness is identical** — both drivers return exactly the requested rows, **8 columns**, and
   the same batch shape (Native 191 / Interop 192 batches at 1M). No result divergence.
2. **Roughly at parity** — within ~10% across all sizes. Equal at 1,000 rows; Interop marginally ahead
   at 100 and 1M.
3. **Native's 1M case improved markedly since the last baseline** (3,613 → 2,531 ms) thanks to **chunk
   prefetching**, which is now implemented (the prior baseline explicitly noted it was missing). That
   closed the earlier gap. (Interop's numbers also shifted vs 2026-06-19, but that's environmental —
   warehouse warmth / network — since the Go driver is unchanged; only the *same-session* Native-vs-Interop
   comparison above is reliable.)
4. **Proxy bypass matters** for S3 chunk downloads — see below.

## Network considerations

Ensure these are in `no_proxy` / `NO_PROXY` for accurate benchmarks:
- `.privatelink.snowflakecomputing.com` (Snowflake API)
- `.amazonaws.com` (S3 chunk storage)

## How to run

Prerequisites: set `SNOWFLAKE_TEST_CONFIG_FILE` to a config JSON with a `query` (and, for Interop,
`driverPath` pointing at the built Go shared library). Both benchmarks are the `BenchmarkTests`
theory (`BaselineQueryPerformance` at limits 100 / 1,000 / 1,000,000).

### Native
```bash
dotnet test csharp/test/Native/AdbcDrivers.Snowflake.Native.Tests.csproj -c Release --framework net8.0 \
  --filter "FullyQualifiedName~BenchmarkTests" --logger "console;verbosity=detailed"
```

### Interop
```bash
dotnet test csharp/test/Interop/AdbcDrivers.Snowflake.Interop.Tests.csproj -c Release --framework net8.0 \
  --filter "FullyQualifiedName~BenchmarkTests" --logger "console;verbosity=detailed"
```
