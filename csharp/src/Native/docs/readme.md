# Native C# Snowflake Driver for Apache Arrow ADBC

A from-scratch C# implementation of an [ADBC](https://arrow.apache.org/adbc/) driver for
Snowflake. Unlike the Interop package (which loads the Go driver through a native library), this
driver talks to Snowflake's REST API directly from managed code and returns results as Apache
Arrow record batches.

- **Target framework:** .NET 8.0
- **Assembly / package:** `AdbcDrivers.Snowflake.Native`
- **Result format:** Arrow end-to-end (Snowflake's Arrow wire format, streamed in chunks with
  bounded parallel prefetch)

## Quick start (ADBC API)

Connections are configured with an ADBC parameter dictionary:

```csharp
using AdbcDrivers.Snowflake.Native;

var parameters = new Dictionary<string, string>
{
    ["adbc.snowflake.sql.account"] = "myorg-myaccount",
    ["username"] = "MYUSER",
    ["password"] = "...",
    ["adbc.snowflake.sql.warehouse"] = "COMPUTE_WH",
    ["adbc.snowflake.sql.db"] = "MYDB",
    ["adbc.snowflake.sql.schema"] = "PUBLIC",
};

var driver = new SnowflakeDriver();
using var database = driver.Open(parameters);
using var connection = database.Connect(new Dictionary<string, string>());
using var statement = connection.CreateStatement();

statement.SqlQuery = "SELECT N_NATIONKEY, N_NAME FROM SNOWFLAKE_SAMPLE_DATA.TPCH_SF1.NATION";
var result = statement.ExecuteQuery();          // or await statement.ExecuteQueryAsync()

using var stream = result.Stream!;              // IArrowArrayStream
while (await stream.ReadNextRecordBatchAsync() is { } batch)
{
    using (batch)
    {
        // process the Arrow RecordBatch
    }
}
```

DML goes through `ExecuteUpdate()` (returns the affected-row count); a long-running query can be
aborted from another thread with `statement.Cancel()`.

### Bind parameters

`?` placeholders bind positionally from a **single-row** Arrow batch:

```csharp
statement.SqlQuery = "SELECT * FROM ORDERS WHERE O_ORDERKEY = ? AND O_ORDERDATE > ?";
var schema = new Schema(
    [new Field("k", Int64Type.Default, true), new Field("d", Date32Type.Default, true)], null);
using var batch = new RecordBatch(schema,
    [
        new Int64Array.Builder().Append(42).Build(),
        new Date32Array.Builder().Append(new DateTime(2024, 1, 1)).Build(),
    ], 1);
statement.Bind(batch, schema);
```

Supported bind types: Boolean, Int8–64 / UInt8–64, Float/Double, Decimal128/256, String, Binary,
Date32/64, Time32/64, Timestamp. Multi-row batches throw `NotSupportedException` (array binding /
`executemany` is not implemented yet).

### Authentication

Select with `adbc.snowflake.sql.auth_type`:

| `auth_type` | Method | Additional keys |
|---|---|---|
| `snowflake` (default) | Username/password | `username`, `password` |
| `snowflake_jwt` / `jwt` | RSA key pair | `…client_option.jwt_private_key_pkcs8_value` (+ `_password` for encrypted keys) |
| `oauth` | OAuth 2.0 access token | `…client_option.auth_token` |
| `externalbrowser` | Browser-based SSO | — |

### ADO.NET client

The driver also works behind the `Apache.Arrow.Adbc.Client` `DbConnection` layer:

```csharp
using AdbcClient = Apache.Arrow.Adbc.Client;

using var connection = new AdbcClient.AdbcConnection(
    new SnowflakeDriver(), parameters, new Dictionary<string, string>());
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = "SELECT 1";
using var reader = command.ExecuteReader();
```

Note: `NUMBER(38,0)` surfaces through the client as `System.Data.SqlTypes.SqlDecimal`
(a CLR `decimal` cannot hold 38 digits); narrower precisions surface as `int`/`long`.

## Connection options

Keys follow the [ADBC Snowflake driver reference](https://arrow.apache.org/adbc/current/driver/snowflake.html)
where an official key exists; pool keys are this driver's own (`adbc.snowflake.pool.*`).

| Key | Meaning | Default |
|-----|---------|---------|
| `adbc.snowflake.sql.account` | Account identifier (**required**) | — |
| `username` / `password` | Credentials for password auth | — |
| `adbc.snowflake.sql.db` / `.schema` / `.warehouse` / `.role` | Session context | — |
| `adbc.connection.catalog` / `adbc.connection.db_schema` | Canonical ADBC current catalog/schema (take precedence over the `sql.db`/`sql.schema` aliases) | — |
| `adbc.snowflake.sql.auth_type` | See Authentication above | `snowflake` |
| `adbc.snowflake.sql.client_option.jwt_private_key_pkcs8_value` / `_password` | Key-pair auth material | — |
| `adbc.snowflake.sql.client_option.auth_token` | OAuth access token | — |
| `adbc.snowflake.sql.uri.host` / `.port` / `.protocol` | Endpoint override (PrivateLink etc.) | account URL |
| `adbc.snowflake.sql.client_option.tls_skip_verify` | Skip TLS certificate validation (**test only**) | `false` |
| `adbc.snowflake.sql.client_option.no_proxy` | Bypass the system proxy | `false` |
| `adbc.snowflake.sql.client_option.request_timeout` | Per-statement timeout, seconds (`STATEMENT_TIMEOUT_IN_SECONDS`) | 300 |
| `adbc.snowflake.sql.client_option.login_timeout` | Login/auth round-trip timeout, seconds | 60 |
| `adbc.snowflake.sql.client_option.enable_compression` | gzip/deflate response compression | `true` |
| `adbc.snowflake.sql.client_option.keep_session_alive` | Heartbeat idle pooled sessions so they never lapse to master-token expiry | `false` |
| `adbc.snowflake.sql.client_option.keep_session_alive_heartbeat_frequency` | Heartbeat interval, seconds (clamped 900–3600) | 3600 |
| `adbc.snowflake.rpc.prefetch_concurrency` | Parallel result-chunk downloads | 10 |
| `adbc.snowflake.pool.max_size` | Max pooled connections per distinct config | 10 |
| `adbc.snowflake.pool.idle_timeout` | Idle eviction (seconds or `30s`/`10m`/`1h`) | 10m |
| `adbc.snowflake.pool.acquire_timeout` | Max wait for a free connection when the pool is full | 120s |
| `adbc.snowflake.pool.max_lifetime` | Max connection lifetime | 1h |

## Sessions and pooling

Connections are pooled per distinct configuration (account, user, credential fingerprint,
database/schema/warehouse/role, endpoint). Session tokens (~1 h) are renewed transparently from
the master token when a query hits expiry; with `keep_session_alive` enabled, idle pooled
connections are heartbeated in the background so the ~4 h master window rolls forward
indefinitely. Server-side sessions are closed when the pool discards a connection.

## Known limitations

Tracked in [TODO.md](TODO.md) with the full backlog:

- **Transactions**: autocommit only — `Commit`/`Rollback` throw `AdbcException` (NotImplemented).
- **Array binding (`executemany`)**: multi-row bind batches throw rather than execute per row.
- **`GetParameterSchema`**: not supported (Snowflake's protocol does not report bind-parameter types).
- **`SetOption` after connect**: not supported yet (catalog/schema are honored at `Connect`).
- **Not implemented**: PUT/GET stage file transfer, multi-statement requests, async (polled) queries.
- Semi-structured types (VARIANT/OBJECT/ARRAY) are returned as JSON strings; GEOGRAPHY/GEOMETRY as GeoJSON strings.
- Very slow consumption of very large results can outlive the chunk URLs' presigned validity.

## More documentation

- [design.md](design.md) — original architecture sketch
- [TODO.md](TODO.md) — tiered backlog and resolved-work log
- [code-review-2026-07-01.md](code-review-2026-07-01.md) / [perf-explainer-2026-07-01.md](perf-explainer-2026-07-01.md) — performance review and results
- [benchmark-results.md](benchmark-results.md) — native-vs-Go benchmark protocol and numbers

## License

Licensed under the Apache License, Version 2.0.
