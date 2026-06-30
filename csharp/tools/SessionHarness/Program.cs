/*
* Copyright (c) 2025 ADBC Drivers Contributors
*
* Licensed under the Apache License, Version 2.0 (the "License"); you may not
* use this file except in compliance with the License. You may obtain a copy
* of the License at http://www.apache.org/licenses/LICENSE-2.0
*/

// Long-running session-lifecycle harness for the native Snowflake driver. It opens ONE connection
// and issues a single query every --interval-min minutes, logging each one (with a non-secret token
// fingerprint, so you can watch the session token roll) and full exception detail on failure.
//
// The query interval is the experiment:
//   • short interval (e.g. 60m, ≤ the ~1h session validity + within the ~4h master window): each
//     query that finds the session token expired triggers reactive renewal — which also refreshes
//     the master token — so the connection survives indefinitely. Expect every query to succeed.
//   • long interval (e.g. 360m / 6h, past the ~4h master window): by the time the next query runs,
//     BOTH the session and master tokens have expired, so renewal can't happen and the query FAILS.
//
// --heartbeat-min N runs a heartbeat on its OWN cadence (independent of the query interval), pinging
// /session/heartbeat — each ping past the session expiry triggers a renewal that rolls the master
// forward, so an otherwise-idle connection survives. To matter it must fire inside the ~4h master
// window (≈60m, like gosnowflake's masterValidity/4).
//
// Config: reads the same JSON pointed to by SNOWFLAKE_TEST_CONFIG_FILE (account / user / password /
// warehouse / database / schema, or the authentication.auth_snowflake block).
//
// Usage (from csharp/):
//   $env:SNOWFLAKE_TEST_CONFIG_FILE = "C:\path\to\snowflakeconfig.local.json"
//   dotnet run --project tools/SessionHarness -- --interval-min 60                    # survives (frequent queries)
//   dotnet run --project tools/SessionHarness -- --interval-min 500 --duration-min 800  # fails ~4h (idle, no heartbeat)
//   dotnet run --project tools/SessionHarness -- --interval-min 500 --heartbeat-min 60 --duration-min 800  # survives (heartbeat keeps it alive)
// Flags: --interval-min N (default 60)  --heartbeat-min N (default 0 = off)  --duration-min N (default 0 = run until Ctrl-C)

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdbcDrivers.Snowflake.Native;

int heartbeatMin = ArgInt(args, "--heartbeat-min", 0);   // 0 = no heartbeat
int intervalMin = ArgInt(args, "--interval-min", 60);
int durationMin = ArgInt(args, "--duration-min", 0);

string? configPath = Environment.GetEnvironmentVariable("SNOWFLAKE_TEST_CONFIG_FILE");
if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
{
    Console.Error.WriteLine("Set SNOWFLAKE_TEST_CONFIG_FILE to a Snowflake config JSON file.");
    return 1;
}

Dictionary<string, string> parameters = LoadParameters(configPath);
Log($"starting: query every {intervalMin}m{(heartbeatMin > 0 ? $" + heartbeat every {heartbeatMin}m" : "")}, " +
    $"{(durationMin > 0 ? $"for {durationMin}m" : "until Ctrl-C")}, " +
    $"account={parameters.GetValueOrDefault("adbc.snowflake.sql.account")}");

var driver = new SnowflakeDriver();
using var database = driver.Open(parameters);
using var connection = (SnowflakeConnection)database.Connect(new Dictionary<string, string>());

Log($"connected: session={Fingerprint(connection.AuthToken?.SessionToken)} " +
    $"master={Fingerprint(connection.AuthToken?.MasterToken)} expiresAt={connection.AuthToken?.ExpiresAt:u}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
    Log("stopping (Ctrl-C)...");
};
 
var finished = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromMinutes(durationMin));
    cts.Cancel();
});

var sw = Stopwatch.StartNew();
int iteration = 0;

// Heartbeat on its own cadence, independent of the query interval — this is what keeps an
// otherwise-idle connection alive (each heartbeat past the session expiry triggers a renewal that
// rolls the master forward). Runs concurrently with the query loop on the same connection.
Task? heartbeatTask = heartbeatMin <= 0 ? null : Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(heartbeatMin), cts.Token);
        }
        catch (TaskCanceledException)
        {
            break;
        }

        try
        {
            await connection.HeartbeatAsync(cts.Token);
            Log($"   +{Elapsed(sw)} heartbeat OK   session={Fingerprint(connection.AuthToken?.SessionToken)}");
        }
        catch (Exception ex)
        {
            LogError($"   +{Elapsed(sw)} heartbeat FAILED", ex);
        }
    }
});

while (!cts.IsCancellationRequested)
{
    iteration++;
    await RunQuery($"#{iteration} +{Elapsed(sw)} query");
    try
    {
        await Task.Delay(TimeSpan.FromMinutes(intervalMin), cts.Token);
    }
    catch (TaskCanceledException)
    {
        break;
    }
}

Log($"finished after {Elapsed(sw)} ({iteration} queries).");
await finished;
if (heartbeatTask != null)
    await heartbeatTask;
return 0;

async Task RunQuery(string label)
{
    try
    {
        using var statement = connection.CreateStatement();
        statement.SqlQuery = "SELECT CURRENT_TIMESTAMP() AS NOW";
        var result = await statement.ExecuteQueryAsync();
        if (result.Stream is null)
        {
            Log($"{label}: NO stream returned (session likely dead)");
            return;
        }

        using var stream = result.Stream;
        var batch = await stream.ReadNextRecordBatchAsync();
        Log($"{label}: OK rows={batch?.Length ?? 0} session={Fingerprint(connection.AuthToken?.SessionToken)}");
    }
    catch (Exception ex)
    {
        LogError($"{label}: FAILED", ex);
    }
}

static void Log(string message) =>
    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] {message}");

static void LogError(string context, Exception ex)
{
    Log($"{context}: {ex.GetType().Name}: {ex.Message}");
    for (Exception? inner = ex.InnerException; inner != null; inner = inner.InnerException)
        Log($"    caused by: {inner.GetType().Name}: {inner.Message}");
}

static string Elapsed(Stopwatch sw) => $"{(int)sw.Elapsed.TotalMinutes}m{sw.Elapsed.Seconds:D2}s";

// A short, non-reversible fingerprint of a token so we can see it change on renewal without
// logging the secret itself.
static string Fingerprint(string? token)
{
    if (string.IsNullOrEmpty(token))
        return "(none)";
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
}

static int ArgInt(string[] args, string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int value) ? value : fallback;
}

static Dictionary<string, string> LoadParameters(string configPath)
{
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
    JsonElement root = doc.RootElement;

    string? Get(string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    string? user = Get("user");
    string? password = Get("password");
    if ((user is null || password is null)
        && root.TryGetProperty("authentication", out JsonElement auth)
        && auth.TryGetProperty("auth_snowflake", out JsonElement snowflake))
    {
        user ??= snowflake.TryGetProperty("user", out JsonElement u) ? u.GetString() : null;
        password ??= snowflake.TryGetProperty("password", out JsonElement p) ? p.GetString() : null;
    }

    var parameters = new Dictionary<string, string>();
    void Add(string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            parameters[key] = value;
    }

    Add("adbc.snowflake.sql.account", Get("account"));
    Add("username", user);
    Add("password", password);
    Add("adbc.snowflake.sql.warehouse", Get("warehouse"));
    Add("adbc.snowflake.sql.db", Get("database"));
    Add("adbc.snowflake.sql.schema", Get("schema"));
    Add("adbc.snowflake.sql.role", Get("role"));
    return parameters;
}
