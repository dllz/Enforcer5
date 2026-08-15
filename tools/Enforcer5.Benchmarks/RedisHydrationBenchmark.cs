using StackExchange.Redis;

namespace Enforcer5.Benchmarks;

/// <summary>
/// Benchmark 1 - Redis read throughput.
/// <para>
/// The migration's floor is how fast Redis can be drained, not how fast PostgreSQL can ingest.
/// This measures the full inventory scan and then hydration of the user tier at several pipeline
/// depths, without retaining values, so the number is read throughput rather than allocation.
/// </para>
/// </summary>
internal sealed class RedisHydrationBenchmark
{
    private static readonly RedisValue[] UserFields = { "name", "username", "msgs" };

    private readonly IDatabase _database;
    private readonly IServer _server;
    private readonly ResultRecorder _recorder;

    internal RedisHydrationBenchmark(IDatabase database, IServer server, ResultRecorder recorder)
    {
        _database = database;
        _server = server;
        _recorder = recorder;
    }

    internal async Task<KeyspaceInventory> RunAsync(int[] pipelineDepths, int scanPageSize,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("\nBenchmark 1 - Redis hydration");

        KeyspaceInventory inventory;
        var scanned = 0L;
        using (_recorder.Measure("redis-scan", $"full keyspace COUNT {scanPageSize}", () => scanned,
                   new Dictionary<string, string> { ["pageSize"] = scanPageSize.ToString() }))
        {
            inventory = await KeyspaceScanner.ScanAsync(_database, _server, scanPageSize, cancellationToken);
            scanned = inventory.TotalKeys;
        }

        Console.WriteLine($"    users={inventory.UserIds.Count:N0}  " +
                          $"globalCounters={inventory.GlobalMessageCounterIds.Count:N0}  " +
                          $"groupsWithStats={inventory.GroupStatisticsUsers.Count:N0}  " +
                          $"unclassified={inventory.UnclassifiedKeys:N0}");

        foreach (var depth in pipelineDepths)
        {
            await MeasureHashFieldsAsync("redis-hydrate-users", depth, inventory.UserIds,
                id => $"user:{id}", UserFields, cancellationToken);

            await MeasureHashFieldsAsync("redis-hydrate-counters", depth,
                inventory.GlobalMessageCounterIds, id => $"chat:{id}",
                new RedisValue[] { "msgs" }, cancellationToken);
        }

        return inventory;
    }

    /// <summary>
    /// Issues HMGET for every id, keeping <paramref name="depth"/> requests in flight.
    /// StackExchange.Redis multiplexes on one connection, so depth here controls how many
    /// commands are queued before the batch is awaited rather than how many sockets are used.
    /// </summary>
    private async Task MeasureHashFieldsAsync(string benchmark, int depth, List<long> ids,
        Func<long, string> keyFor, RedisValue[] fields, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return;

        var observed = 0L;
        // A checksum forces the values to actually be read without retaining them, so the
        // measurement is Redis throughput rather than the GC cost of holding several hundred MB.
        var checksum = 0L;

        using (_recorder.Measure(benchmark, $"pipeline depth {depth}", () => observed,
                   new Dictionary<string, string> { ["depth"] = depth.ToString() }))
        {
            var batch = new Task<RedisValue[]>[depth];
            var pending = 0;

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch[pending++] = _database.HashGetAsync(keyFor(id), fields);
                if (pending != depth) continue;
                checksum += await DrainAsync(batch, pending);
                observed += pending;
                pending = 0;
            }

            if (pending > 0)
            {
                checksum += await DrainAsync(batch, pending);
                observed += pending;
            }
        }

        if (checksum == long.MinValue) Console.WriteLine("unreachable");
    }

    private static async Task<long> DrainAsync(Task<RedisValue[]>[] batch, int count)
    {
        var total = 0L;
        for (var i = 0; i < count; i++)
        {
            var values = await batch[i];
            for (var f = 0; f < values.Length; f++)
                if (values[f].HasValue)
                    total += values[f].Length();
        }
        return total;
    }
}
