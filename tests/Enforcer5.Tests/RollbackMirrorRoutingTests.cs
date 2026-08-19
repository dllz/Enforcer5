using Enforcer5.Storage;
using Enforcer5.Storage.Cutover;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// Rollback from the per-group phase is a state flip, not a restore - but only if every write a
/// cut-over group makes is mirrored back into Redis while it is on PostgreSQL.
/// <para>
/// There are two mirror flags. <c>RedisMirrorEnabled</c> is process-wide and is <b>false for the
/// whole of Stage C</b>, because the process-wide mode stays <c>RedisPrimaryPostgresShadow</c>
/// until the final flip. <c>GroupRedisMirrorEnabled</c> is the per-group one. Group-scoped writes
/// gated on the former are written to PostgreSQL and never mirrored, so rolling that group back
/// loses them - silently, and only for groups that had already cut over.
/// </para>
/// <para>
/// Four paths were on the wrong flag: sanction revocation, report policies, per-group message
/// totals, and group content and custom commands. These pin the distinction that makes the
/// difference detectable.
/// </para>
/// </summary>
[Collection("GroupDrainBuffer")]
public sealed class RollbackMirrorRoutingTests : IDisposable
{
    private const long Migrated = -1007700001111;
    private const long NotMigrated = -1007700002222;
    private readonly GlobalConfigScope _config;

    public RollbackMirrorRoutingTests()
    {
        GroupMigrationStateStore.ResetForTests();
        _config = GlobalConfigScope.Acquire("StorageMode");
        // Stage C: the process-wide mode has NOT flipped. This is the condition under which the
        // two flags disagree, and therefore the only condition in which the bug is reachable.
        _config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        GroupMigrationStateStore.ApplyLocal(Migrated, GroupMigrationState.Postgres);
    }

    public void Dispose()
    {
        _config.Dispose();
        GroupMigrationStateStore.ResetForTests();
    }

    [Fact]
    public void TheProcessWideMirrorFlagIsFalseThroughoutStageC()
    {
        // The premise of the whole problem. Any group-scoped write gated on this is unmirrored for
        // every group, migrated or not, until Stage D.
        Assert.False(StorageRouting.RedisMirrorEnabled);
    }

    [Fact]
    public void ACutOverGroupMirrorsWhileAnUnmigratedOneDoesNot()
    {
        // A migrated group is PostgreSQL-primary, so its writes must be projected back into Redis
        // or a rollback becomes a restore.
        Assert.True(StorageRouting.GroupRedisMirrorEnabled(Migrated));
        Assert.True(StorageRouting.GroupPostgresIsPrimary(Migrated));

        // An unmigrated group is still Redis-primary, so there is nothing to mirror - Redis already
        // holds the truth.
        Assert.False(StorageRouting.GroupRedisMirrorEnabled(NotMigrated));
        Assert.False(StorageRouting.GroupPostgresIsPrimary(NotMigrated));
    }

    [Fact]
    public void EveryGroupIsAuthoritativeAndMirroredOnceTheProcessWideModeFlips()
    {
        // At Stage D the distinction disappears: the ceiling makes every group PostgreSQL-primary
        // and the process-wide mirror covers everything. Rollback from D really is a config change.
        _config.Set("StorageMode", nameof(StorageMode.PostgresPrimaryRedisMirror));

        Assert.True(StorageRouting.RedisMirrorEnabled);
        Assert.True(StorageRouting.GroupRedisMirrorEnabled(NotMigrated));
        Assert.True(StorageRouting.GroupPostgresIsPrimary(NotMigrated));
    }

    [Fact]
    public void PostgresOnlyStopsMirroringAndSoEndsCheapRollback()
    {
        // The point of no return, and worth being explicit about: under PostgresOnly nothing is
        // written back to Redis, so rollback stops being a state flip. The plan only reaches this
        // after a soak, and this is what that soak is protecting.
        _config.Set("StorageMode", nameof(StorageMode.PostgresOnly));

        Assert.False(StorageRouting.RedisMirrorEnabled);
        Assert.False(StorageRouting.GroupRedisMirrorEnabled(Migrated));
        Assert.True(StorageRouting.GroupPostgresIsPrimary(Migrated));
    }
}
