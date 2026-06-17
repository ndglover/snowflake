# Native C# Snowflake ADBC Driver — TODO

Issues identified during code review. Resolve as we go.

## Bugs

- [ ] **SnowflakeStatement double-wraps exceptions** — The catch-all `catch (Exception ex)` in `ExecuteQueryAsync` wraps `AdbcException` inside another `AdbcException`. Should rethrow `AdbcException` directly.
- [ ] **SnowflakeTestingUtils OAuth user null reference** — The OAuth branch in `GetSnowflakeAdbcDriver` calls `Parameter(OAuth.User, "user")` which throws if user is empty/null. Guard with a null check since user is optional for OAuth.

## Resource Management

- [ ] **SnowflakeDatabase doesn't dispose HttpClient** — When the database creates its own `HttpClient` (not injected), it should dispose it in `Dispose()`.
- [ ] **PooledConnection doesn't close Snowflake session** — No `/session` DELETE request on dispose. Orphaned sessions accumulate on the server.

## Consistency

- [ ] **Inconsistent exception types across authenticators** — `BasicAuthenticator` throws `AdbcException`, others throw `InvalidOperationException`. Standardize on `AdbcException`.
- [ ] **Two `ParameterSet` classes with conflicting nullability** — `TypeConversion/ParameterSet.cs` uses `Dictionary<string, object?>`, `Query/IQueryExecutor.cs` uses `Dictionary<string, object>`. Consolidate.

## Dead Code / Incomplete

- [ ] **AuthenticationService._tokenCache is never populated** — The `ConcurrentDictionary` is created but tokens are never added. Either implement caching or remove.
- [ ] **QueryExecutor.CancelQueryAsync throws NotImplementedException** — Either implement or remove from interface until ready.

## Resilience

- [ ] **RestApiClient doesn't handle 401 (token expired)** — Transient error detection only covers 408/429/503/504. An expired token mid-session won't trigger refresh/retry.
- [ ] **SsoAuthenticator hardcodes port 8080** — No fallback if port is in use. Should use OS-assigned port or try multiple.

## Style / Minor

- [ ] **SnowflakeConnection typo in comment** — `/// <summary>AdbcDatabaseAdbcDatabase`
- [ ] **RequestBuilder returns `object`** — Should return a typed request model for testability.
- [ ] **ConnectionPoolEntry mixes primary constructor with mutable field** — Consider using a regular constructor for clarity.
- [ ] **ClientTests and ClientIntegrationTests overlap** — Consider consolidating into one test class.
