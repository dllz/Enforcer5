using StackExchange.Redis;

namespace Enforcer5.Benchmarks;

/// <summary>
/// Sizes the PostgreSQL migration against a copy of the production dataset.
/// <para>
/// Not shipped. See docs/postgres-migration-handoff.md for what each measurement decides.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var redisConnection = Environment.GetEnvironmentVariable("BENCH_REDIS");
        var postgresConnection = Environment.GetEnvironmentVariable("BENCH_POSTGRES");
        var resultPath = Environment.GetEnvironmentVariable("BENCH_RESULTS") ?? "benchmark-results.json";

        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            Console.Error.WriteLine("Set BENCH_REDIS to the Redis holding the production RDB copy.");
            return 2;
        }

        var selected = args.Length == 0
            ? new[] { "1", "2", "3", "4" }
            : args;

        var scanPageSize = GetInt("BENCH_SCAN_PAGE_SIZE", 5000);
        var depths = (Environment.GetEnvironmentVariable("BENCH_DEPTHS") ?? "100,1000,10000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();

        Console.WriteLine($"Redis      : {Redact(redisConnection)}");
        Console.WriteLine($"PostgreSQL : {Redact(postgresConnection) ?? "(not configured)"}");
        Console.WriteLine($"Benchmarks : {string.Join(", ", selected)}");

        var recorder = new ResultRecorder(resultPath);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

        var options = ConfigurationOptions.Parse(redisConnection);
        options.AllowAdmin = true;          // required for SCAN via IServer
        options.SyncTimeout = 30_000;
        options.AsyncTimeout = 30_000;

        await using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        var database = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);

        try
        {
            KeyspaceInventory inventory = null;

            if (selected.Contains("1"))
                inventory = await new RedisHydrationBenchmark(database, server, recorder)
                    .RunAsync(depths, scanPageSize, cancellation.Token);

            if (selected.Intersect(new[] { "2", "3", "4" }).Any())
            {
                if (string.IsNullOrWhiteSpace(postgresConnection))
                {
                    Console.Error.WriteLine("Benchmarks 2-4 need BENCH_POSTGRES.");
                    return 2;
                }

                inventory ??= await ScanOnlyAsync(database, server, scanPageSize, cancellation.Token);

                Console.WriteLine("\nApplying the shipped schema to the benchmark database...");
                await Enforcer5.Storage.SchemaMigrationRunner.ApplyAsync(postgresConnection);

                var loader = new UserTierLoadBenchmark(database, postgresConnection, recorder);
                await loader.RunAsync(inventory, selected, depths.Max(), cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
        }
        finally
        {
            recorder.Flush();
        }

        return 0;
    }

    private static async Task<KeyspaceInventory> ScanOnlyAsync(IDatabase database, IServer server,
        int pageSize, CancellationToken cancellationToken)
    {
        Console.WriteLine("\nScanning keyspace (needed by benchmarks 2-4)...");
        var inventory = await KeyspaceScanner.ScanAsync(database, server, pageSize, cancellationToken);
        Console.WriteLine($"    users={inventory.UserIds.Count:N0}  " +
                          $"globalCounters={inventory.GlobalMessageCounterIds.Count:N0}");
        return inventory;
    }

    private static int GetInt(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) ? value : fallback;

    private static string Redact(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        return System.Text.RegularExpressions.Regex.Replace(connectionString,
            @"(?i)(password\s*=\s*)[^;]*", "$1***");
    }
}
