# Gap Analysis: C# Native Driver vs Go Driver

This document identifies features and capabilities present in the Go Snowflake ADBC driver (`go/`) that are missing from the C# Native driver (`csharp/src/Native/`).

## 1. Connection Options

The Go driver supports ~30+ connection options. The C# driver covers the basics but is missing:

| Option | Go Key | Status |
|--------|--------|--------|
| Region | `adbc.snowflake.sql.region` | ❌ Missing |
| Login Timeout | `adbc.snowflake.sql.client_option.login_timeout` | ❌ Missing |
| Request Timeout | `adbc.snowflake.sql.client_option.request_timeout` | ❌ Missing |
| JWT Expire Timeout | `adbc.snowflake.sql.client_option.jwt_expire_timeout` | ❌ Missing |
| Client Timeout | `adbc.snowflake.sql.client_option.client_timeout` | ❌ Missing |
| High Precision | `adbc.snowflake.sql.client_option.use_high_precision` | ❌ Missing |
| Max Timestamp Precision | `adbc.snowflake.sql.client_option.max_timestamp_precision` | ❌ Missing |
| Stream Retry | `adbc.snowflake.sql.client_option.stream_retry_enabled` | ❌ Missing |
| Application Name | `adbc.snowflake.sql.client_option.app_name` | ❌ Missing |
| TLS Skip Verify | `adbc.snowflake.sql.client_option.tls_skip_verify` | ❌ Missing |
| OCSP Fail Open | `adbc.snowflake.sql.client_option.ocsp_fail_open_mode` | ❌ Missing |
| Okta URL | `adbc.snowflake.sql.client_option.okta_url` | ❌ Missing |
| Keep Session Alive | `adbc.snowflake.sql.client_option.keep_session_alive` | ❌ Missing |
| JWT Private Key File | `adbc.snowflake.sql.client_option.jwt_private_key` | ❌ Missing |
| Disable Telemetry | `adbc.snowflake.sql.client_option.disable_telemetry` | ❌ Missing |
| Log Tracing | `adbc.snowflake.sql.client_option.tracing` | ❌ Missing |
| Client Config File | `adbc.snowflake.sql.client_option.config_file` | ❌ Missing |
| MFA Token Cache | `adbc.snowflake.sql.client_option.cache_mfa_token` | ❌ Missing |
| Store Temp Credentials | `adbc.snowflake.sql.client_option.store_temp_creds` | ❌ Missing |
| WIF Identity Provider | `adbc.snowflake.sql.client_option.identity_provider` | ❌ Missing |
| URI Parsing | `snowflake://` scheme DSN | ❌ Missing |
| Session Parameters | Arbitrary key pass-through | ❌ Missing |

### Currently Supported in C#
- Account, Host, Port, Protocol, Database, Schema, Warehouse, Role
- SSL Skip Verify, No Proxy, Connection Timeout, Compression
- Connection pooling options (max pool size, idle timeout, max lifetime)

## 2. Authentication

| Auth Type | Go | C# |
|-----------|:--:|:--:|
| Username/Password (`auth_snowflake`) | ✅ | ✅ |
| OAuth (`auth_oauth`) | ✅ | ✅ |
| JWT/Key Pair (`auth_jwt`) | ✅ | ✅ |
| External Browser SSO (`auth_ext_browser`) | ✅ | ✅ |
| Generic SSO | ✅ | ✅ |
| MFA (`auth_mfa`) | ✅ | ❌ |
| Programmatic Access Token (`auth_pat`) | ✅ | ❌ |
| Workload Identity Federation (`auth_wif`) | ✅ | ❌ |
| Native Okta URL (`auth_okta`) | ✅ | ❌ |

## 3. Statement Features

| Feature | Go | C# |
|---------|:--:|:--:|
| SQL Query Execution | ✅ | ✅ |
| Prepared Statements | ✅ | ✅ |
| Parameter Binding (Bind) | ✅ | ✅ |
| GetParameterSchema | ✅ | ✅ |
| Bulk Ingestion | ✅ | ❌ |
| BindStream (streaming params) | ✅ | ❌ |
| ExecuteSchema | ✅ | ❌ |
| Query Tag | ✅ | ❌ |
| Result Queue Size | ✅ | ❌ |
| Prefetch Concurrency | ✅ | ❌ |
| Ingest Writer/Upload/Copy Concurrency | ✅ | ❌ |
| Ingest Compression (snappy, gzip, brotli, zstd, lz4) | ✅ | ❌ |
| Ingest Target File Size | ✅ | ❌ |
| Ingest Geo Type handling | ✅ | ❌ |
| High Precision override (statement level) | ✅ | ❌ |

### Bulk Ingestion Detail
The Go driver has a full pipeline: Arrow → Parquet → Temporary Stage → COPY INTO, with:
- Four ingest modes: Create, Append, Replace, CreateAppend
- Configurable parallelism for writing, uploading, and copying
- Compression codec selection and level
- GeoArrow → GEOGRAPHY/GEOMETRY with COPY transforms
- Row count verification

The C# driver has no bulk ingestion support.

## 4. Metadata (GetObjects)

| Feature | Go | C# |
|---------|:--:|:--:|
| GetObjects (all depths) | ✅ | ✅ |
| GetTableSchema | ✅ | ✅ |
| GetTableTypes | ✅ | ✅ |
| GetInfo | ✅ | ✅ |
| GetStatistics | ✅ | ❌ |
| GetStatisticNames | ✅ | ❌ |
| SetCurrentCatalog / GetCurrentCatalog | ✅ | ❌ |
| SetCurrentDbSchema / GetCurrentDbSchema | ✅ | ❌ |
| Autocommit Control | ✅ | ❌ |
| Commit / Rollback | ✅ | ❌ (stubs throw NotImplementedException) |
| Optimised direct-path queries | ✅ | ❌ (N+1 queries) |
| Parallel metadata fetching | ✅ | ❌ |

### GetStatistics Metrics (Go)
- Row count, bytes, retention time, active bytes
- Time travel bytes, failsafe bytes, clustering depth

## 5. Type Support

| Type / Feature | Go | C# |
|----------------|:--:|:--:|
| NUMBER → Int64/Decimal128 | ✅ | ✅ |
| FLOAT/DOUBLE | ✅ | ✅ |
| VARCHAR | ✅ | ✅ |
| BINARY | ✅ | ✅ |
| BOOLEAN | ✅ | ✅ |
| DATE → Date32 | ✅ | ✅ |
| TIME → Time32/Time64 (variable unit) | ✅ | ⚠️ (Time64 nanosecond only) |
| TIMESTAMP_NTZ | ✅ | ✅ |
| TIMESTAMP_LTZ | ✅ | ✅ |
| TIMESTAMP_TZ (struct decode) | ✅ | ⚠️ (basic support) |
| VARIANT/OBJECT → String | ✅ | ✅ |
| ARRAY → String with extension metadata | ✅ | ⚠️ (List of String, no extension) |
| GEOGRAPHY → GeoArrow WKB | ✅ | ❌ (returns GeoJSON string) |
| GEOMETRY → GeoArrow WKB | ✅ | ❌ (returns GeoJSON string) |
| High Precision toggle (Decimal128 for all) | ✅ | ❌ |
| Timestamp overflow protection | ✅ | ❌ |
| Microsecond fallback | ✅ | ❌ |
| JSON result fallback | ✅ | ❌ |

## 6. Result Streaming

| Feature | Go | C# |
|---------|:--:|:--:|
| Sequential chunk fetching | ✅ | ✅ |
| Parallel prefetching (configurable concurrency) | ✅ | ❌ |
| Stream retry with re-download | ✅ | ❌ |
| Record transformation pipeline | ✅ | ❌ |
| ConcatReader for bound params | ✅ | ❌ |
| JSON result fallback | ✅ | ❌ |
| Geo column detection from data (EWKB peek) | ✅ | ❌ |
| Row count validation | ✅ | ❌ |
| Configurable buffer/queue size | ✅ | ❌ |

## Summary of Major Gaps

1. **Bulk Ingestion** — Entire feature missing. High impact for data loading use cases.
2. **Parallel Result Streaming** — C# fetches sequentially; Go prefetches 5 batches concurrently.
3. **Transaction Support** — Commit/Rollback/Autocommit are stubs.
4. **GetStatistics** — No storage/performance metrics available.
5. **Additional Auth Methods** — MFA, PAT, WIF not supported.
6. **GeoArrow Support** — GEOGRAPHY/GEOMETRY returned as string, not WKB with extension metadata.
7. **Timestamp Precision Control** — No overflow protection or microsecond fallback.
8. **Connection Options** — ~20 options missing (timeouts, telemetry, session params, etc.).
