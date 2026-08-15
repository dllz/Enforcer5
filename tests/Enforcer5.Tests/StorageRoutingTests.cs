using Enforcer5.Storage;
using Enforcer5.Storage.Cutover;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The migration has two tiers moving at different speeds, and conflating them is what made the
/// process-wide storage mode unusable on its own. The global tier - users, accounts, the username
/// index, global sanctions and roles - moves once, to completeness. Group data then moves one group
/// at a time, so a mixed population is normal for the length of the migration.
/// </summary>
public sealed class StorageRoutingTests : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly GlobalConfigScope _config;

    public StorageRoutingTests()
    {
        Gate.Wait();
        GroupMigrationStateStore.ResetForTests();
        _config = GlobalConfigScope.Acquire("StorageMode");
    }

    public void Dispose()
    {
        _config.Dispose();
        GroupMigrationStateStore.ResetForTests();
        Gate.Release();
    }

    [Fact]
    public void WithNoGroupStateNothingChangesFromTheProcessWideMode()
    {
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));

        // The whole point: enabling per-group routing must be inert until groups are deliberately
        // flipped, so deploying it changes no behaviour.
        Assert.False(StorageRouting.GlobalTierPostgresIsPrimary);
        Assert.False(StorageRouting.GroupPostgresIsPrimary(-100));
        Assert.Equal(StorageMode.RedisPrimaryPostgresShadow, StorageRouting.EffectiveGroupMode(-100));
        Assert.False(StorageRouting.GroupRedisMirrorEnabled(-100));
    }

    [Fact]
    public void AMigratedGroupIsOnPostgresWhileItsNeighbourStaysOnRedis()
    {
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        GroupMigrationStateStore.ApplyLocal(-100, GroupMigrationState.Postgres);

        Assert.True(StorageRouting.GroupPostgresIsPrimary(-100));
        Assert.False(StorageRouting.GroupPostgresIsPrimary(-200));

        // A group that has cut over still mirrors back into Redis, which is what keeps rollback a
        // state flip rather than a restore.
        Assert.True(StorageRouting.GroupRedisMirrorEnabled(-100));
        Assert.False(StorageRouting.GroupRedisMirrorEnabled(-200));

        // The global tier is untouched by a group flipping.
        Assert.False(StorageRouting.GlobalTierPostgresIsPrimary);
    }

    [Theory]
    [InlineData(nameof(GroupMigrationState.Draining))]
    [InlineData(nameof(GroupMigrationState.Verifying))]
    public void GroupsMidCutoverStillReadFromRedis(string stateName)
    {
        var state = GroupMigrationStateExtensions.Parse(stateName);
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        GroupMigrationStateStore.ApplyLocal(-100, state);

        // Only Postgres is authoritative. Draining and Verifying still serve from Redis, which is
        // exactly what makes abandoning a cutover cost nothing.
        Assert.False(StorageRouting.GroupPostgresIsPrimary(-100));
        Assert.True(StorageRouting.GroupRequiresDrainBuffer(-100));
    }

    [Fact]
    public void AFailedCutoverFallsBackToRedis()
    {
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        GroupMigrationStateStore.ApplyLocal(-100, GroupMigrationState.Failed);

        Assert.False(StorageRouting.GroupPostgresIsPrimary(-100));
        Assert.False(StorageRouting.GroupRequiresDrainBuffer(-100));
    }

    [Theory]
    [InlineData(nameof(StorageMode.PostgresPrimaryRedisMirror))]
    [InlineData(nameof(StorageMode.PostgresOnly))]
    public void TheProcessWideModeIsACeilingOverEveryGroup(string mode)
    {
        _config.Set("StorageMode", mode);
        // Deliberately left as Redis-owned: once the global mode is flipped, per-group state must
        // not be able to hold a group back.
        GroupMigrationStateStore.ApplyLocal(-100, GroupMigrationState.Redis);

        Assert.True(StorageRouting.GlobalTierPostgresIsPrimary);
        Assert.True(StorageRouting.GroupPostgresIsPrimary(-100));
        Assert.True(StorageRouting.GroupPostgresIsPrimary(-999));
    }

    [Fact]
    public void MirrorFollowsTheModeOnceTheGlobalFlipHasHappened()
    {
        _config.Set("StorageMode", nameof(StorageMode.PostgresOnly));
        // PostgresOnly deliberately stops writing Redis, so no group mirrors.
        Assert.False(StorageRouting.GroupRedisMirrorEnabled(-100));

        _config.Set("StorageMode", nameof(StorageMode.PostgresPrimaryRedisMirror));
        Assert.True(StorageRouting.GroupRedisMirrorEnabled(-100));
    }
}
