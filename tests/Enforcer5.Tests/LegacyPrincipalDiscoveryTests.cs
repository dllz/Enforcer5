using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.PrincipalContent;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// Regression cover for the two silent data-loss defects found by surveying the production
/// keyspace. Both discarded real records, and neither produced any error.
/// </summary>
public sealed class LegacyPrincipalDiscoveryTests
{
    [RedisFact]
    public async Task UsersWithoutANameAreMigratedAndTheMissingNameIsRecorded()
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        using var config = GlobalConfigScope.Acquire(
            "RedisConnection", "StorageMode", "PostgresConnection", "GroupSettingsShadowEnabled");
        config.Set("RedisConnection", connectionString);
        config.Set("GroupSettingsShadowEnabled", "false");
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        Assert.True(Redis.Start());

        var seed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var complete = seed + 1;      // name + username, the modern shape
        var namelessWithUsername = seed + 2;  // username only - migrated today, name still a defect
        var nameless = seed + 3;      // neither - silently discarded before this fix
        var keys = new RedisKey[]
            { $"user:{complete}", $"user:{namelessWithUsername}", $"user:{nameless}" };

        try
        {
            await Redis.db.KeyDeleteAsync(keys);
            await Redis.db.HashSetAsync($"user:{complete}", new[]
            {
                new HashEntry("name", "Complete"), new HashEntry("username", "@complete")
            });
            await Redis.db.HashSetAsync($"user:{namelessWithUsername}", "username", "@usernameonly");
            // The discarded shape: no name, no username, only the legacy counter. 326,787 real
            // users in production look exactly like this.
            await Redis.db.HashSetAsync($"user:{nameless}", "msgs", "1742");

            var snapshot = await new LegacyPrincipalContentReader()
                .ReadAsync(new[] { complete, namelessWithUsername, nameless }, Array.Empty<long>());

            Assert.Equal(3, snapshot.Users.Count);
            Assert.Contains(snapshot.Users, u => u.UserId == nameless);

            var recovered = snapshot.Users.Single(u => u.UserId == nameless);
            Assert.Null(recovered.Name);
            Assert.Null(recovered.Username);

            // A missing name is a defect worth recording; a missing username is not, because 40.6%
            // of users legitimately have none.
            var flagged = snapshot.DataQualityIssues.Select(i => i.SourceKey).ToArray();
            Assert.Contains($"user:{nameless}", flagged);
            Assert.Contains($"user:{namelessWithUsername}", flagged);
            Assert.DoesNotContain($"user:{complete}", flagged);
            Assert.All(snapshot.DataQualityIssues, issue => Assert.Equal("name", issue.SourceField));
        }
        finally
        {
            await Redis.db.KeyDeleteAsync(keys);
        }
    }

    [RedisFact]
    public async Task GroupDiscoveryFindsGroupsMissingFromBotGroupsId()
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        using var config = GlobalConfigScope.Acquire(
            "RedisConnection", "StorageMode", "PostgresConnection", "GroupSettingsShadowEnabled");
        config.Set("RedisConnection", connectionString);
        config.Set("GroupSettingsShadowEnabled", "false");
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        Assert.True(Redis.Start());

        // Negative like a real supergroup id, and far enough out not to collide with the dataset.
        var orphan = -(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 4_000_000_000);
        var statisticsKey = $"{orphan}:users:424242";

        try
        {
            await Redis.db.KeyDeleteAsync(statisticsKey);
            Assert.False(await Redis.db.SetContainsAsync("bot:groupsid", orphan),
                "the orphan must not be declared in bot:groupsid for this test to mean anything");
            await Redis.db.HashSetAsync(statisticsKey, "msgs", "5");

            var discovered = await LegacyPrincipalContentReader.DiscoverGroupIdsAsync();

            // 7,951 production groups have statistics and are absent from bot:groupsid. Seeding
            // from the set alone skips them entirely.
            Assert.Contains(orphan, discovered);
        }
        finally
        {
            await Redis.db.KeyDeleteAsync(statisticsKey);
        }
    }
}
