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
    public async Task ACutoverWithNoCompletedInventoryIsRefused()
    {
        // The freshness rule has nothing to compare against, so there is no basis on which to
        // believe the copy saw everything. Refusing is the only safe answer.
        await InSchemaAsync(async database =>
        {
            var result = await new GroupCutoverService(database, "test").CutOverAsync(-100100);

            Assert.False(result.Succeeded);
            Assert.Equal(GroupMigrationState.Redis, result.FinalState);
            Assert.Contains("inventory", result.Failure);
            Assert.Equal(GroupMigrationState.Redis, GroupMigrationStateStore.Get(-100100));
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
    public async Task AFailedCutoverLeavesTheGroupOnRedisAndClearsItsLease()
    {
        await InSchemaAsync(async database =>
        {
            await new GroupCutoverService(database, "test").CutOverAsync(-100300);

            await using var connection = await database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<(string State, string Owner)>(
                "SELECT state AS State, lease_owner AS Owner FROM migration_group_state " +
                "WHERE telegram_chat_id = -100300");

            // Back to Redis with the lease released, so the next attempt is free to retry rather
            // than waiting out an expiry.
            Assert.Equal("Redis", row.State);
            Assert.Null(row.Owner);
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
