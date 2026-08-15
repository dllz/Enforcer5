using Enforcer5.Helpers;
using Enforcer5.Storage;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

public sealed class GroupSettingsRedisIntegrationTests
{
    [RedisFact]
    public async Task SetIncrementDeleteAndObservePreserveExactRedisState()
    {
        var connectionString = Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)!;
        // GroupSettingsStore.SetAsync branches on StorageModeProvider.PostgresIsPrimary at call
        // time, so this test must not observe another test's temporary StorageMode.
        using var config = GlobalConfigScope.Acquire(
            "RedisConnection", "GroupSettingsShadowEnabled", "StorageMode", "PostgresConnection");
        config.Set("RedisConnection", connectionString);
        config.Set("GroupSettingsShadowEnabled", "false");
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        Assert.True(Redis.Start());

        var chatId = -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = $"chat:{chatId}:settings";
        var revisionKey = $"compat:group-settings:{chatId}:settings:revision";
        try
        {
            await Redis.db.KeyDeleteAsync(new RedisKey[] { key, revisionKey });

            Assert.True(await GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Settings,
                "unknown", "MixedCase"));
            Assert.Equal(5, await GroupSettingsStore.IncrementAsync(chatId,
                GroupSettingGroups.Settings, "number", 5));

            var snapshot = await GroupSettingsStore.ObserveAsync(chatId, GroupSettingGroups.Settings);
            Assert.True(snapshot.SourceExists);
            Assert.Equal("MixedCase", snapshot.Fields["unknown"]);
            Assert.Equal("5", snapshot.Fields["number"]);
            Assert.Equal(3, snapshot.SourceRevision);

            Assert.True(await GroupSettingsStore.DeleteAsync(chatId, GroupSettingGroups.Settings, "unknown"));
            Assert.True(await GroupSettingsStore.DeleteAsync(chatId, GroupSettingGroups.Settings, "number"));
            var deleted = await GroupSettingsStore.ObserveAsync(chatId, GroupSettingGroups.Settings);
            Assert.False(deleted.SourceExists);
            Assert.Empty(deleted.Fields);
        }
        finally
        {
            await Redis.db.KeyDeleteAsync(new RedisKey[] { key, revisionKey });
        }
    }
}
