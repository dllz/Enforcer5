using Enforcer5.Storage;
using Enforcer5.Storage.Cutover;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// Stage A of the rollout deploys this build with the shipped defaults and every shadow flag off.
/// The claim is that it changes no behaviour, so these pin that claim rather than trusting it.
/// </summary>
public sealed class StageADefaultsTests : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly GlobalConfigScope _config;

    public StageADefaultsTests()
    {
        Gate.Wait();
        GroupMigrationStateStore.ResetForTests();
        _config = GlobalConfigScope.Acquire("StorageMode", "PostgresConnection",
            "GroupSettingsShadowEnabled", "PrincipalContentShadowEnabled",
            "ModerationShadowEnabled", "OperationsShadowEnabled");

        // The shipped defaults, from appsettings.template.json and the systemd units.
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        _config.Set("GroupSettingsShadowEnabled", "false");
        _config.Set("PrincipalContentShadowEnabled", "false");
        _config.Set("ModerationShadowEnabled", "false");
        _config.Set("OperationsShadowEnabled", "false");
    }

    public void Dispose()
    {
        _config.Dispose();
        GroupMigrationStateStore.ResetForTests();
        Gate.Release();
    }

    [Fact]
    public void EverythingStaysOnRedis()
    {
        Assert.False(StorageRouting.GlobalTierPostgresIsPrimary);
        Assert.False(StorageRouting.GroupPostgresIsPrimary(-1001234567890));
        Assert.False(StorageRouting.GroupRedisMirrorEnabled(-1001234567890));
        Assert.False(StorageRouting.GroupRequiresDrainBuffer(-1001234567890));
        Assert.Equal(StorageMode.RedisPrimaryPostgresShadow,
            StorageRouting.EffectiveGroupMode(-1001234567890));
    }

    [Fact]
    public void NoGroupIsTreatedAsMigratedBeforeTheCacheIsPrimed()
    {
        // A cold start, or a PostgreSQL outage during priming, must read as "nothing has migrated".
        Assert.False(GroupMigrationStateStore.IsPrimed);
        Assert.Equal(GroupMigrationState.Redis, GroupMigrationStateStore.Get(-1001234567890));
        Assert.False(GroupMigrationStateStore.PostgresIsAuthoritative(-1001234567890));
    }

    [Fact]
    public async Task GlobalTierWritesDoNotTouchPostgresWithTheDefaults()
    {
        // PostgresConnection deliberately absent: if the guards were wrong this would throw trying
        // to resolve a connection string rather than returning quietly.
        _config.Set("PostgresConnection", null);

        await Enforcer5.Storage.Operations.OperationsStore
            .RecordModerationCounterAsync(4242, "flood");

        var counters = await Enforcer5.Storage.Operations.OperationsStore
            .GetModerationCountersAsync(4242);
        Assert.Null(counters);
    }
}
