using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Cutover;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// Stage C's safety properties, which are all about what happens when a cutover does <em>not</em>
/// succeed. Redis stays authoritative until the final transition, so a failed attempt has to cost
/// nothing - the group carries on being served correctly and the next attempt can retry.
/// </summary>
[Collection("GroupDrainBuffer")]
public sealed class GroupCutoverTests : IDisposable
{
    public GroupCutoverTests() => GroupMigrationStateStore.ResetForTests();
    public void Dispose() => GroupMigrationStateStore.ResetForTests();

    [PostgresFact]
    public async Task ACutoverDoesNotDependOnTheKeyspaceInventory()
    {
        // Section 6.1's freshness rule assumed the per-group copy would replay the recorded
        // inventory, leaving a gap for keys created between the scan and the drain. This copy reads
        // Redis live through the same per-domain backfills the bulk path uses, so it sees whatever
        // exists at copy time and the drain buffer covers the rest. Enforcing the rule anyway would
        // have required a fresh 15-minute keyspace scan before every single group.
        //
        // There is no inventory in this schema. The cutover must not refuse on that basis -
        // whether it then succeeds or fails for some other reason is not what is under test.
        await InSchemaAsync(async database =>
        {
            var result = await new GroupCutoverService(database, "test").CutOverAsync(-100100);

            Assert.DoesNotContain("inventory", result.Failure ?? string.Empty);
        });
    }

    [PostgresFact]
    public async Task ARollbackCanUnflipAMigratedGroupButAbandonmentCannot()
    {
        // The two paths differ deliberately. Abandonment is raised by the writer path, holds no
        // lease, and must never un-flip a live group - reverting one underneath its readers loses
        // writes. A rollback is an operator action that specifically intends to un-flip, so it takes
        // the lease and is allowed to. Conflating them made the rollback command silently do
        // nothing while reporting success.
        await InSchemaAsync(async database =>
        {
            var lease = await GroupMigrationStateStore.TryAcquireLeaseAsync(database, -100700,
                "owner", TimeSpan.FromMinutes(10));
            await GroupMigrationStateStore.TransitionAsync(database, lease!, GroupMigrationState.Postgres);
            await GroupMigrationStateStore.ReleaseLeaseAsync(database, lease!);

            await GroupMigrationStateStore.AbandonAsync(database, -100700, "writer path");
            await using (var check = await database.OpenConnectionAsync())
                Assert.Equal("Postgres", await check.ExecuteScalarAsync<string>(
                    "SELECT state FROM migration_group_state WHERE telegram_chat_id = -100700"));

            Assert.True(await GroupMigrationStateStore.RollBackAsync(database, -100700, "operator",
                "manual rollback"));

            await using var connection = await database.OpenConnectionAsync();
            var row = await connection.QuerySingleAsync<(string State, DateTimeOffset? MigratedAt)>(
                "SELECT state AS State, migrated_at AS MigratedAt FROM migration_group_state " +
                "WHERE telegram_chat_id = -100700");
            Assert.Equal("Redis", row.State);
            // Cleared, so a later re-cutover records its own timestamp rather than inheriting one.
            Assert.Null(row.MigratedAt);
        });
    }

    [PostgresFact]
    public async Task ARollbackIsRefusedWhileAnotherMigratorHoldsTheLease()
    {
        await InSchemaAsync(async database =>
        {
            await GroupMigrationStateStore.TryAcquireLeaseAsync(database, -100800, "in-flight",
                TimeSpan.FromMinutes(10));

            // Reporting success here while changing nothing is exactly what the command used to do.
            Assert.False(await GroupMigrationStateStore.RollBackAsync(database, -100800, "operator",
                "manual rollback"));
        });
    }

    [PostgresFact]
    public async Task ASecondMigratorCannotTakeAGroupThatIsAlreadyLeased()
    {
        await InSchemaAsync(async database =>
        {
            var held = await GroupMigrationStateStore.TryAcquireLeaseAsync(database, -100200,
                "first-migrator", TimeSpan.FromMinutes(10));
            Assert.NotNull(held);

            var result = await new GroupCutoverService(database, "second-migrator")
                .CutOverAsync(-100200);

            Assert.False(result.Succeeded);
            Assert.Contains("lease", result.Failure);
        });
    }

    [PostgresFact]
    public async Task ACutoverAlwaysReleasesItsLeaseAndLeavesAConsistentState()
    {
        await InSchemaAsync(async database =>
        {
            var result = await new GroupCutoverService(database, "test").CutOverAsync(-100300);

            await using var connection = await database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<(string State, string Owner)>(
                "SELECT state AS State, lease_owner AS Owner FROM migration_group_state " +
                "WHERE telegram_chat_id = -100300");

            // Released whatever the outcome, so the next attempt retries immediately instead of
            // waiting out an expiry. A lease left behind is how a crashed migrator strands a group.
            Assert.Null(row.Owner);
            // And the recorded state agrees with what the caller was told - a group reported as not
            // cut over but left saying Postgres would be served from the wrong store.
            Assert.Equal(result.Succeeded ? "Postgres" : "Redis", row.State);
        });
    }

    [PostgresFact]
    public async Task OnlyTheLeaseHolderCanAdvanceAGroup()
    {
        await InSchemaAsync(async database =>
        {
            var lease = await GroupMigrationStateStore.TryAcquireLeaseAsync(database, -100400,
                "owner", TimeSpan.FromMinutes(10));
            var impostor = new GroupLease(-100400, "someone-else", DateTimeOffset.UtcNow.AddMinutes(10));

            Assert.False(await GroupMigrationStateStore.TransitionAsync(database, impostor,
                GroupMigrationState.Postgres));
            Assert.True(await GroupMigrationStateStore.TransitionAsync(database, lease!,
                GroupMigrationState.Draining));
        });
    }

    [PostgresFact]
    public async Task AbandonmentNeverPullsAGroupBackOutOfPostgres()
    {
        // Abandonment is raised by the writer path, which holds no lease. It is only safe because
        // it can exclusively move a group *back* - a group that has already flipped is serving
        // reads from PostgreSQL, and reverting it underneath them would lose writes.
        await InSchemaAsync(async database =>
        {
            var lease = await GroupMigrationStateStore.TryAcquireLeaseAsync(database, -100500,
                "owner", TimeSpan.FromMinutes(10));
            await GroupMigrationStateStore.TransitionAsync(database, lease!, GroupMigrationState.Postgres);

            await GroupMigrationStateStore.AbandonAsync(database, -100500, "buffer overflow");

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal("Postgres", await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM migration_group_state WHERE telegram_chat_id = -100500"));
        });
    }

    [Fact]
    public void TrafficCutoverIsOffByDefaultSoDeployingChangesNothing()
    {
        using var config = GlobalConfigScope.Acquire("GroupTrafficCutoverEnabled");
        config.Set("GroupTrafficCutoverEnabled", null);
        GroupCutoverCrawler.ResetForTests();

        GroupCutoverCrawler.Observe(-100600);

        // Nothing queued, and the group is untouched. Traffic-triggered cutover only comes on after
        // the crawler has proven itself over several thousand clean groups.
        Assert.Equal(GroupMigrationState.Redis, GroupMigrationStateStore.Get(-100600));
    }

    private static async Task InSchemaAsync(Func<PostgresDatabase, Task> body)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_cutover_{Guid.NewGuid():N}";

        using var config = GlobalConfigScope.Acquire("StorageMode");
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA \"{schema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            await body(database);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }
}
