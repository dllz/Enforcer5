using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Cutover;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The drain buffer records which keys changed while a group's copy was running, so the migrator
/// can re-read them before flipping. Its failure semantics are the subtle part: overflow must cost
/// the cutover, never the user's write.
/// </summary>
[Collection("GroupDrainBuffer")]
public sealed class GroupDrainBufferTests : IDisposable
{
    private const long ChatId = -1009900001234;
    private readonly GlobalConfigScope _config;

    public GroupDrainBufferTests()
    {
        GroupMigrationStateStore.ResetForTests();
        _config = GlobalConfigScope.Acquire("RedisConnection", "StorageMode", "PostgresConnection",
            "GroupDrainBufferCapacity", "GroupSettingsShadowEnabled");
        _config.Set("RedisConnection",
            Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable));
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        // Absent on purpose: abandonment must degrade to a local state change rather than throwing
        // when PostgreSQL cannot be reached.
        _config.Set("PostgresConnection", null);
    }

    public void Dispose()
    {
        _config.Dispose();
        GroupMigrationStateStore.ResetForTests();
    }

    [RedisFact]
    public async Task AGroupThatIsNotBeingCopiedRecordsNothing()
    {
        Assert.True(Redis.Start());
        await Redis.db.KeyDeleteAsync(GroupDrainBuffer.KeyFor(ChatId));

        // The default for every group for almost the whole migration. It must cost nothing.
        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:settings");

        Assert.Equal(0, await GroupDrainBuffer.LengthAsync(ChatId));
    }

    [RedisFact]
    public async Task ADrainingGroupRecordsTheMutatedKey()
    {
        Assert.True(Redis.Start());
        await Redis.db.KeyDeleteAsync(GroupDrainBuffer.KeyFor(ChatId));
        GroupMigrationStateStore.ApplyLocal(ChatId, GroupMigrationState.Draining);

        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:warns");

        Assert.Equal(new[] { $"chat:{ChatId}:warns" }, await GroupDrainBuffer.PeekAsync(ChatId, 10));
        await GroupDrainBuffer.DeleteAsync(ChatId);
    }

    [RedisFact]
    public async Task VerifyingStillBuffersBecauseTheGroupHasNotFlippedYet()
    {
        // Redis is still authoritative during Verifying, so writes keep arriving and still have to
        // be captured - otherwise a mutation during reconciliation would be lost on the flip.
        Assert.True(Redis.Start());
        await Redis.db.KeyDeleteAsync(GroupDrainBuffer.KeyFor(ChatId));
        GroupMigrationStateStore.ApplyLocal(ChatId, GroupMigrationState.Verifying);

        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:auth");

        Assert.Equal(1, await GroupDrainBuffer.LengthAsync(ChatId));
        await GroupDrainBuffer.DeleteAsync(ChatId);
    }

    [RedisFact]
    public async Task AFlippedGroupStopsBuffering()
    {
        Assert.True(Redis.Start());
        await Redis.db.KeyDeleteAsync(GroupDrainBuffer.KeyFor(ChatId));
        GroupMigrationStateStore.ApplyLocal(ChatId, GroupMigrationState.Postgres);

        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:settings");

        Assert.Equal(0, await GroupDrainBuffer.LengthAsync(ChatId));
    }

    [RedisFact]
    public async Task OverflowAbandonsTheCutoverRatherThanFailingTheWrite()
    {
        Assert.True(Redis.Start());
        await Redis.db.KeyDeleteAsync(GroupDrainBuffer.KeyFor(ChatId));
        _config.Set("GroupDrainBufferCapacity", "1000");   // clamped up to the 1,000 minimum
        GroupMigrationStateStore.ApplyLocal(ChatId, GroupMigrationState.Draining);

        for (var i = 0; i < 1000; i++)
            await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:key{i}");
        Assert.Equal(1000, await GroupDrainBuffer.LengthAsync(ChatId));

        // The write that overflows must not throw - it is a live moderation action, and a visible
        // failure would make the migration a user-facing event. The cutover is abandoned instead.
        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:overflow");

        Assert.Equal(GroupMigrationState.Redis, GroupMigrationStateStore.Get(ChatId));
        Assert.False(StorageRouting.GroupRequiresDrainBuffer(ChatId));
        await GroupDrainBuffer.DeleteAsync(ChatId);
    }

    [RedisFact]
    public async Task PeekDeduplicatesAndTrimKeepsWhatArrivedDuringTheDrain()
    {
        Assert.True(Redis.Start());
        await Redis.db.KeyDeleteAsync(GroupDrainBuffer.KeyFor(ChatId));
        GroupMigrationStateStore.ApplyLocal(ChatId, GroupMigrationState.Draining);

        // A hot key is mutated repeatedly; the migrator only needs to re-read it once.
        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:warns");
        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:warns");
        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:auth");

        var batch = await GroupDrainBuffer.PeekAsync(ChatId, 100);
        Assert.Equal(new[] { $"chat:{ChatId}:warns", $"chat:{ChatId}:auth" }, batch);

        // A mutation lands mid-drain. Trimming must consume only what was read, or that write is
        // lost - which is the whole failure the buffer exists to prevent.
        await GroupDrainBuffer.AppendAsync(ChatId, $"chat:{ChatId}:mod");
        await GroupDrainBuffer.TrimAsync(ChatId, 3);

        Assert.Equal(new[] { $"chat:{ChatId}:mod" }, await GroupDrainBuffer.PeekAsync(ChatId, 100));
        await GroupDrainBuffer.DeleteAsync(ChatId);
    }

    [RedisFact]
    public async Task ASettingsMutationRecordsItselfInTheSameScript()
    {
        // Settings mutate through Lua, so the append is atomic with the write rather than following
        // it. This is the only group-scoped path where a crash cannot leave a hole in the buffer.
        Assert.True(Redis.Start());
        await Redis.db.KeyDeleteAsync(new RedisKey[]
        {
            GroupDrainBuffer.KeyFor(ChatId), $"chat:{ChatId}:settings",
            $"compat:group-settings:{ChatId}:settings:revision"
        });
        _config.Set("GroupSettingsShadowEnabled", "false");
        GroupMigrationStateStore.ApplyLocal(ChatId, GroupMigrationState.Draining);

        Assert.True(await GroupSettingsStore.SetAsync(ChatId, GroupSettingGroups.Settings, "welcome", "hi"));

        Assert.Equal(new[] { $"chat:{ChatId}:settings" }, await GroupDrainBuffer.PeekAsync(ChatId, 10));
        await Redis.db.KeyDeleteAsync(new RedisKey[]
        {
            GroupDrainBuffer.KeyFor(ChatId), $"chat:{ChatId}:settings",
            $"compat:group-settings:{ChatId}:settings:revision"
        });
    }
}
