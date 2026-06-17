# Native Snowflake ADBC Driver — Tests

Tests for the native C# Snowflake ADBC driver (`AdbcDrivers.Snowflake.Native`). The
suite splits into two tiers:

- **Offline unit tests** — no network, no credentials; run everywhere (CI included).
- **Integration tests** — exercise a *live* Snowflake account; written as
  `[SkippableFact]`/`[SkippableTheory]` so they **skip automatically** when no
  configuration is present instead of failing.

```
dotnet test                                   # everything (integration skips if unconfigured)
dotnet test --filter "Category=offline"       # not used — see filters below
```

---

## What is tested

### Offline unit tests (no Snowflake connection)

| File | Tests | What it covers |
|------|-------|----------------|
| `TypeConverterTests.cs` | ~41 | Snowflake ⇄ Arrow type mapping: NUMBER scale 0→Int64 / scale>0→Decimal128, BOOLEAN/VARCHAR/BINARY/DATE/TIME, TIMESTAMP NTZ/LTZ/TZ timezone handling, and unsupported-type → `NotSupportedException`. |
| `RequestBuilderTests.cs` | ~10 | REST request-body construction — `BuildQueryRequest` (sqlText, ARROW result format, session parameters, bindings, multi-statement, `describeOnly`), `BuildCancelRequest`, `BuildMetadataRequest`, and argument validation. |
| `SnowflakeAccountUrlTests.cs` | ~8 | Account → base-URL building: plain account vs. full hostname (no double-append, case-insensitive), and `NetworkConfig` host/port/protocol overrides. |
| `QueryExecutorTests.cs` | ~5 | DML affected-row detection (`TryGetDmlAffectedRows`): INSERT count, MERGE summing across count columns, reads the row-count summary (not the payload `Returned`), non-DML / empty → false. |
| `Configuration/ConnectionStringParserTests.cs` | ~12 | Connection-string / parameter parsing and required-parameter / invalid-authenticator validation. |
| `SnowflakeDriverTests.cs` | ~7 | Driver lifecycle: `Open` parameter validation (missing/invalid/null → `ArgumentException`/`ArgumentNullException`), and idempotent `Dispose`. |

These assert pure logic against in-memory inputs, so they're the fast feedback loop and
the regression net for the protocol/encoding code.

### Integration tests (require a live account)

| File | What it covers |
|------|----------------|
| `SampleDataTests.cs` | **Content-asserting** tests against the shared read-only `SNOWFLAKE_SAMPLE_DATA` (TPC-H SF1). Because that data has fixed cardinalities/contents in every account, these verify *real values*: REGION's 5 region names, NATION = 25 rows/4 cols, CUSTOMER = full 150,000-row chunk streaming, invalid-SQL → `AdbcException`, deterministic `GetTableSchema` types, `GetTableTypes` = TABLE/VIEW, `GetInfo` vendor = "Snowflake", and `GetObjects` navigated through the nested Arrow down to NATION's columns. |
| `DriverTests.cs` | Baseline driver surface: connect, execute query, execute update (DML affected-rows), and the metadata entry points (`GetInfo`/`GetTableTypes`/`GetTableSchema`/`GetObjects`). Uses the configured `metadata.*` table. |
| `StatementTests.cs` | Statement surface: execute query, `ExecuteUpdate` on a SELECT → -1, `GetParameterSchema` before `Prepare` throws, and prepare-then-execute. |
| `ClientTests.cs` | Client/connection patterns: sync and async connect + query/update/get-schema. |
| `ClientIntegrationTests.cs` | Larger-result throughput across row-count sizes against the sample data. |

> **Note on result-set types.** Query result-set Arrow types come from Snowflake's
> native Arrow IPC (encoding-dependent), so the integration tests assert on **content,
> row counts, and column names** there. Deterministic *type* assertions are done via
> `GetTableSchema` (the describe path, which maps through `TypeConverter`).

### Not yet covered (known gaps)

- **Parameter binding (`?` placeholders)** — the bound-value wire format is unverified
  and likely needs work, so there is intentionally no passing binding test yet.
- **Transactions** — `Commit`/`Rollback` currently throw `NotImplementedException`.
- **Constraint column detail** — `GetObjects` reports constraint names/types only (FK
  referenced-column usage needs `SHOW … KEYS` + `RESULT_SCAN`).

---

## Requirements

### Offline tests
- .NET 8 SDK. Nothing else — `dotnet test` with the filters below runs them with no
  account.

### Integration tests
- A reachable **Snowflake account** and login (username/password, key-pair/JWT, or
  OAuth — see config below).
- A **running warehouse** (the sample-data queries need compute).
- The **`SNOWFLAKE_SAMPLE_DATA`** share mounted (it is, by default) — required by
  `SampleDataTests` and `ClientIntegrationTests`.
- For `DriverTests`/`StatementTests` metadata tests: a **writable database/schema** and
  the `metadata.*` table populated in the config (the sample share is read-only).

---

## Configuring integration tests

Configuration is loaded from a **JSON file** pointed to by the
`SNOWFLAKE_TEST_CONFIG_FILE` environment variable. (The per-variable
`SNOWFLAKE_*` env-var loader exists in `SnowflakeTestingUtils` but is currently
disabled because it does not populate the `metadata.*` block the metadata tests need.)

**1. Create a config file** (keep it outside the repo — it holds secrets):

```json
{
  "account": "your-account",
  "user": "your-username",
  "password": "your-password",
  "warehouse": "your-warehouse",
  "database": "your-database",
  "schema": "your-schema",
  "query": "SELECT * FROM your-database.your-schema.your-table",
  "expectedResults": 2,
  "authentication": {
    "auth_snowflake": {
      "user": "your-username",
      "password": "your-password"
    }
  },
  "metadata": {
    "catalog": "your-database",
    "schema": "your-schema",
    "table": "your-table",
    "expectedColumnCount": 30
  }
}
```

`SampleDataTests` ignores `metadata.*`/`query` (it targets `SNOWFLAKE_SAMPLE_DATA`
directly), so it works with only valid credentials + a warehouse. The other suites use
`query`/`metadata.*`.

**2. Point the environment variable at it:**

```powershell
# PowerShell
$env:SNOWFLAKE_TEST_CONFIG_FILE = "C:\path\to\snowflakeconfig.local.json"
```
```bash
# Linux/macOS
export SNOWFLAKE_TEST_CONFIG_FILE=/path/to/snowflakeconfig.local.json
```

The JSON format matches the Interop Snowflake tests, so the same config file works for
both drivers (the native driver just ignores `driverPath`/`driverEntryPoint`).

---

## Running

```bash
# Offline unit tests only (no account needed)
dotnet test --filter "FullyQualifiedName~TypeConverterTests|FullyQualifiedName~RequestBuilderTests|FullyQualifiedName~SnowflakeAccountUrlTests|FullyQualifiedName~QueryExecutorTests|FullyQualifiedName~ConnectionStringParserTests|FullyQualifiedName~SnowflakeDriverTests"

# A single integration suite (set SNOWFLAKE_TEST_CONFIG_FILE first)
dotnet test --filter "FullyQualifiedName~SampleDataTests"

# Everything — integration tests skip automatically if SNOWFLAKE_TEST_CONFIG_FILE is unset
dotnet test
```

---

## Security

- **Never commit credentials.** Keep the config file outside the repository.
- Prefer key-pair (JWT) or OAuth over passwords where possible.
- Rotate credentials regularly.
