using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// <c>ban:{userId}</c> is a user-tier hash of lifetime moderation counts across every group -
/// 121,061 keys in production. It had no storage routing on either side and nothing under Storage/
/// touched it, so it would have been left in Redis entirely.
/// </summary>
public sealed class ModerationCounterMigrationTests
{
    [PostgresFact]
    [Trait("RequiresRedis", "true")]
    public async Task LegacyBanCountersAreCarriedAsAbsoluteValuesAndAreIdempotent()
    {
        var redisConnection = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(redisConnection)) return;

        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_modcounter_{Guid.NewGuid():N}";

        using var config = GlobalConfigScope.Acquire(
            "RedisConnection", "StorageMode", "PostgresConnection", "OperationsShadowEnabled");
        config.Set("RedisConnection", redisConnection);
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        Assert.True(Redis.Start());

        var userId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var banKey = $"ban:{userId}";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);

            await Redis.db.KeyDeleteAsync(banKey);
            await Redis.db.HashSetAsync(banKey, new[]
            {
                new StackExchange.Redis.HashEntry("flood", 12),
                new StackExchange.Redis.HashEntry("kick", 3),
                new StackExchange.Redis.HashEntry("longmessages", 47)
            });

            var snapshot = await new LegacyOperationsReader()
                // Signature is (groupIds, userIds, reportMessageIds).
                .ReadAsync(Array.Empty<long>(), new[] { userId },
                    new Dictionary<long, IReadOnlyCollection<long>>());

            var carried = snapshot.ModerationCounters.Where(c => c.UserId == userId)
                .ToDictionary(c => c.Kind, c => c.Count);
            Assert.Equal(12, carried["flood"]);
            Assert.Equal(3, carried["kick"]);
            Assert.Equal(47, carried["longmessages"]);

            var repository = new AccountModerationCounterRepository(database);
            foreach (var counter in snapshot.ModerationCounters.Where(c => c.UserId == userId))
                await repository.SetAsync(counter.UserId, counter.Kind, counter.Count);

            // Absolute values, not increments: a second pass must converge, not double-count.
            foreach (var counter in snapshot.ModerationCounters.Where(c => c.UserId == userId))
                await repository.SetAsync(counter.UserId, counter.Kind, counter.Count);

            var stored = await repository.GetAsync(userId);
            Assert.Equal(12, stored["flood"]);
            Assert.Equal(3, stored["kick"]);
            Assert.Equal(47, stored["longmessages"]);
        }
        finally
        {
            await Redis.db.KeyDeleteAsync(banKey);
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
