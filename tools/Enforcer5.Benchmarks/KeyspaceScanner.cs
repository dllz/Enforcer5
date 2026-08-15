using System.Diagnostics;
using StackExchange.Redis;

namespace Enforcer5.Benchmarks;

/// <summary>
/// The result of the single full-keyspace pass the migration is allowed to make.
/// Everything downstream works from this rather than re-scanning per group, because
/// <c>SCAN MATCH</c> walks the whole keyspace and gives no per-prefix index.
/// </summary>
internal sealed class KeyspaceInventory
{
    internal List<long> UserIds { get; } = new();
    internal List<long> GlobalMessageCounterIds { get; } = new();
    internal Dictionary<long, List<long>> GroupStatisticsUsers { get; } = new();
    internal long TotalKeys { get; set; }
    internal long UnclassifiedKeys { get; set; }
}

internal static class KeyspaceScanner
{
    /// <summary>
    /// Streams the whole keyspace once with a cursor, classifying every key by owning scope.
    /// <para>
    /// COUNT is deliberately large: the cost here is round trips, not per-key work, and the
    /// measured pass over 19.4M keys is dominated by pipeline throughput rather than Redis CPU.
    /// </para>
    /// </summary>
    internal static async Task<KeyspaceInventory> ScanAsync(IDatabase database, IServer server,
        int pageSize, CancellationToken cancellationToken = default)
    {
        var inventory = new KeyspaceInventory();
        var report = Stopwatch.StartNew();

        await foreach (var key in server.KeysAsync(database.Database, pattern: "*", pageSize: pageSize)
                           .WithCancellation(cancellationToken))
        {
            inventory.TotalKeys++;
            Classify(key.ToString(), inventory);

            if (report.Elapsed.TotalSeconds >= 10)
            {
                Console.WriteLine($"    scanned {inventory.TotalKeys:N0} keys...");
                report.Restart();
            }
        }

        return inventory;
    }

    /// <summary>
    /// Classification is purely by key shape, so it costs no extra Redis round trips.
    /// Only the patterns the user and statistics tiers need are collected here.
    /// </summary>
    private static void Classify(string key, KeyspaceInventory inventory)
    {
        // "{groupId}:users:{userId}" - per-group statistics, the largest population at 7.4M.
        var usersMarker = key.IndexOf(":users:", StringComparison.Ordinal);
        if (usersMarker > 0)
        {
            if (long.TryParse(key.AsSpan(0, usersMarker), out var groupId) &&
                long.TryParse(key.AsSpan(usersMarker + 7), out var memberId))
            {
                if (!inventory.GroupStatisticsUsers.TryGetValue(groupId, out var members))
                    inventory.GroupStatisticsUsers[groupId] = members = new List<long>();
                members.Add(memberId);
                return;
            }
        }

        if (key.StartsWith("user:", StringComparison.Ordinal))
        {
            // "user:{id}" only - "user:{id}:query" is inline query state, a different tier.
            if (long.TryParse(key.AsSpan(5), out var userId))
            {
                inventory.UserIds.Add(userId);
                return;
            }
            inventory.UnclassifiedKeys++;
            return;
        }

        if (key.StartsWith("chat:", StringComparison.Ordinal))
        {
            // "chat:{userId}" is the user's global lifetime message total, not a group key.
            // Group settings are "chat:{groupId}:{suffix}" and are reconstructable, so they
            // are not collected here.
            if (long.TryParse(key.AsSpan(5), out var chatId) && chatId > 0)
            {
                inventory.GlobalMessageCounterIds.Add(chatId);
                return;
            }
        }

        inventory.UnclassifiedKeys++;
    }
}
