using Enforcer5.Helpers;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// Production runs Redis 6.0.9, where <c>XAUTOCLAIM</c> does not exist - it was added in 6.2.
/// Both reclaim loops called it, caught the resulting "unknown command" error, logged it and
/// retried forever, so nothing was ever reclaimed and entries abandoned by a dead consumer stayed
/// pending permanently. These tests pin the 5.0-compatible replacement.
/// </summary>
public sealed class RedisStreamReclaimTests
{
    [RedisFact]
    public async Task EntryAbandonedByADeadConsumerIsReclaimed()
    {
        var (database, key, group) = await SetUpAsync(nameof(EntryAbandonedByADeadConsumerIsReclaimed));
        try
        {
            await database.StreamAddAsync(key, "payload", "work");

            // Consumer A takes it and dies without acknowledging.
            var delivered = await database.StreamReadGroupAsync(key, group, "consumer-a",
                StreamPosition.NewMessages, 10);
            Assert.Single(delivered);

            // Nothing is stale yet, so a reclaimer must not steal live work.
            var tooEarly = await RedisStreamReclaim.ClaimStaleAsync(database, key, group,
                "reclaimer", minIdleMilliseconds: 60_000, count: 10);
            Assert.Empty(tooEarly);

            await Task.Delay(150);

            var claimed = await RedisStreamReclaim.ClaimStaleAsync(database, key, group,
                "reclaimer", minIdleMilliseconds: 100, count: 10);

            var entry = Assert.Single(claimed);
            Assert.Equal("work", entry["payload"]);
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }

    [RedisFact]
    public async Task AcknowledgedWorkIsNotReclaimed()
    {
        var (database, key, group) = await SetUpAsync(nameof(AcknowledgedWorkIsNotReclaimed));
        try
        {
            await database.StreamAddAsync(key, "payload", "done");
            var delivered = await database.StreamReadGroupAsync(key, group, "consumer-a",
                StreamPosition.NewMessages, 10);
            await database.StreamAcknowledgeAsync(key, group, delivered[0].Id);

            await Task.Delay(150);
            var claimed = await RedisStreamReclaim.ClaimStaleAsync(database, key, group,
                "reclaimer", minIdleMilliseconds: 100, count: 10);

            Assert.Empty(claimed);
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }

    /// <summary>
    /// An entry deleted while still pending comes back from XCLAIM with no fields. XAUTOCLAIM
    /// removed those from the pending list itself; the replacement has to acknowledge them or the
    /// reclaimer retries the same tombstone on every pass forever.
    /// </summary>
    [RedisFact]
    public async Task TombstonedEntriesAreAcknowledgedRatherThanRetriedForever()
    {
        var (database, key, group) = await SetUpAsync(nameof(TombstonedEntriesAreAcknowledgedRatherThanRetriedForever));
        try
        {
            var id = await database.StreamAddAsync(key, "payload", "vanishing");
            await database.StreamReadGroupAsync(key, group, "consumer-a",
                StreamPosition.NewMessages, 10);
            await database.StreamDeleteAsync(key, new[] { id });

            await Task.Delay(150);
            var claimed = await RedisStreamReclaim.ClaimStaleAsync(database, key, group,
                "reclaimer", minIdleMilliseconds: 100, count: 10);
            Assert.Empty(claimed);

            // Acknowledged, so it is gone from the pending list rather than reappearing.
            var pending = await database.StreamPendingAsync(key, group);
            Assert.Equal(0, pending.PendingMessageCount);
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }

    [RedisFact]
    public async Task MissingConsumerGroupIsNotAnError()
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var database = multiplexer.GetDatabase();
        var key = (RedisKey)$"test:reclaim:absent:{Guid.NewGuid():N}";

        var claimed = await RedisStreamReclaim.ClaimStaleAsync(database, key, "no-such-group",
            "reclaimer", minIdleMilliseconds: 100, count: 10);

        Assert.Empty(claimed);
        await multiplexer.CloseAsync();
    }

    private static async Task<(IDatabase Database, RedisKey Key, RedisValue Group)> SetUpAsync(string name)
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var database = multiplexer.GetDatabase();
        var key = (RedisKey)$"test:reclaim:{name}:{Guid.NewGuid():N}";
        await database.KeyDeleteAsync(key);
        await database.StreamCreateConsumerGroupAsync(key, "workers", StreamPosition.NewMessages, true);
        return (database, key, "workers");
    }
}
