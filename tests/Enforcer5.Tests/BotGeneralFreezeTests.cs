using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// <c>bot:general</c> is frozen for the duration of the migration (rollout plan section 12.2).
/// <para>
/// It is a bot-wide hash with no group dimension, incremented from paths that had no storage
/// routing at all. Once the population is mixed it would silently have become "messages from
/// groups still on Redis" - a number that looks live but means nothing. Its <c>groups</c> and
/// <c>users</c> fields had already drifted 24x before any of this started.
/// </para>
/// <para>
/// These pin the freeze. There was previously no test of any kind on this key, so a reintroduced
/// increment would have gone unnoticed.
/// </para>
/// </summary>
public sealed class BotGeneralFreezeTests
{
    private const string Key = "bot:general";

    [RedisFact]
    public async Task RecordingAMessageLeavesBotGeneralUntouchedButStillCarriesTheAuthoritativePair()
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        using var config = GlobalConfigScope.Acquire(
            "RedisConnection", "StorageMode", "PostgresConnection", "OperationsShadowEnabled");
        config.Set("RedisConnection", connectionString);
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        config.Set("OperationsShadowEnabled", "false");
        // Absent on purpose: if the routing guards were wrong this would throw resolving a
        // connection string rather than quietly taking the Redis path.
        config.Set("PostgresConnection", null);
        Assert.True(Redis.Start());

        var groupId = -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var userId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var userKey = $"chat:{userId}";
        var groupUserKey = $"{groupId}:users:{userId}";

        // Seed a value rather than deleting the key. Freezing means "stops moving", not "removed",
        // so the assertion has to be that an existing value survives untouched.
        await Redis.db.HashSetAsync(Key, "messages", 1_331_976_572L);
        await Redis.db.KeyDeleteAsync(new RedisKey[] { userKey, groupUserKey });
        try
        {
            await OperationsStore.RecordMessageAsync(userId, groupId, DateTimeOffset.UtcNow);

            Assert.Equal(1_331_976_572L,
                (long)await Redis.db.HashGetAsync(Key, "messages"));

            // The counters section 12.1 proved authoritative must still move, and must move
            // together - the invariant "user total = SUM(per-group) + unattributed" depends on it.
            Assert.Equal(1L, (long)await Redis.db.HashGetAsync(userKey, "msgs"));
            Assert.Equal(1L, (long)await Redis.db.HashGetAsync(groupUserKey, "msgs"));
        }
        finally
        {
            await Redis.db.KeyDeleteAsync(new RedisKey[] { userKey, groupUserKey });
            await Redis.db.HashDeleteAsync(Key, "messages");
        }
    }

    [RedisFact]
    public async Task AMessageWithNoGroupWritesNothingAtAll()
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        using var config = GlobalConfigScope.Acquire(
            "RedisConnection", "StorageMode", "PostgresConnection", "OperationsShadowEnabled");
        config.Set("RedisConnection", connectionString);
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        config.Set("OperationsShadowEnabled", "false");
        config.Set("PostgresConnection", null);
        Assert.True(Redis.Start());

        // A private-chat message was the one case that reached bot:general and nothing else, so it
        // is the case most likely to be quietly restored by a future edit.
        await Redis.db.HashSetAsync(Key, "messages", 42L);
        try
        {
            await OperationsStore.RecordMessageAsync(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, DateTimeOffset.UtcNow);

            Assert.Equal(42L, (long)await Redis.db.HashGetAsync(Key, "messages"));
        }
        finally
        {
            await Redis.db.HashDeleteAsync(Key, "messages");
        }
    }
}
