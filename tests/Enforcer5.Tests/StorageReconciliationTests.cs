using Dapper;
using Enforcer5.Storage;
using Enforcer5.Storage.Moderation;
using Enforcer5.Storage.Operations;
using Enforcer5.Storage.PrincipalContent;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

public sealed class StorageReconciliationTests
{
    [PostgresFact]
    public async Task PrincipalContentRemovesRowsAbsentFromSnapshot()
    {
        await InSchemaAsync(async database =>
        {
            await using (var connection = await database.OpenConnectionAsync())
            {
                await connection.ExecuteAsync("INSERT INTO groups (chat_id, name) VALUES (1, 'group')");
                await connection.ExecuteAsync(@"INSERT INTO group_entitlements (chat_id, entitlement, enabled)
VALUES (1, 'premium', TRUE);
INSERT INTO group_content_items (chat_id, content_type, content) VALUES (1, 'rules', 'old');
INSERT INTO custom_commands (chat_id, trigger, content) VALUES (1, 'old', 'old')");
            }

            var snapshot = new PrincipalContentBackfill(Array.Empty<UserPrincipal>(),
                new[] { new GroupPrincipal(1, "group") }, Array.Empty<GroupEntitlement>(),
                Array.Empty<GroupContentItem>(), Array.Empty<CustomCommand>());
            Assert.Equal(3, await PrincipalContentBackfillService.ReconcileAsync(database, snapshot));

            await using var verify = await database.OpenConnectionAsync();
            Assert.False(await verify.ExecuteScalarAsync<bool>(
                "SELECT enabled FROM group_entitlements WHERE chat_id = 1 AND entitlement = 'premium'"));
            Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM group_content_items"));
            Assert.Equal(0, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM custom_commands"));
        });
    }

    [PostgresFact]
    public async Task ModerationRevokesAndDisablesRowsAbsentFromSnapshot()
    {
        await InSchemaAsync(async database =>
        {
            await using (var connection = await database.OpenConnectionAsync())
                await connection.ExecuteAsync(@"INSERT INTO users (user_id, name) VALUES (2, 'user');
INSERT INTO groups (chat_id, name) VALUES (1, 'group');
INSERT INTO group_role_assignments (group_id, user_id, role, assigned_at) VALUES (1, 2, 'moderator', now());
INSERT INTO group_elevation_blocks (group_id, user_id, blocked_at) VALUES (1, 2, now());
INSERT INTO group_user_policies (group_id, user_id, policy, enabled, updated_at)
VALUES (1, 2, 'watch_disabled', TRUE, now());
INSERT INTO warning_balances (group_id, user_id, warning_type, active_quantity, lifetime_quantity, updated_at)
VALUES (1, 2, 'standard', 2, 4, now())");

            var snapshot = new ModerationSnapshot(Array.Empty<GroupRoleAssignment>(),
                Array.Empty<GroupElevationBlock>(), Array.Empty<GroupUserPolicy>(),
                Array.Empty<WarningBalance>());
            Assert.Equal(4, await ModerationBackfillService.ReconcileAsync(database, new[] { 1L }, snapshot));

            await using var verify = await database.OpenConnectionAsync();
            Assert.NotNull(await verify.ExecuteScalarAsync<DateTimeOffset?>(
                "SELECT revoked_at FROM group_role_assignments"));
            Assert.NotNull(await verify.ExecuteScalarAsync<DateTimeOffset?>(
                "SELECT revoked_at FROM group_elevation_blocks"));
            Assert.False(await verify.ExecuteScalarAsync<bool>("SELECT enabled FROM group_user_policies"));
            Assert.Equal(0, await verify.ExecuteScalarAsync<long>("SELECT active_quantity FROM warning_balances"));
            Assert.Equal(4, await verify.ExecuteScalarAsync<long>("SELECT lifetime_quantity FROM warning_balances"));
        });
    }

    [PostgresFact]
    public async Task OperationsRevokesActiveLegacyRowsButPreservesAuditHistory()
    {
        await InSchemaAsync(async database =>
        {
            await using (var connection = await database.OpenConnectionAsync())
                await connection.ExecuteAsync(@"INSERT INTO sanctions
(group_id, user_id, kind, status, edition, created_at, legacy_key)
VALUES (1, 2, 'ban', 'active', 'normal', now(), 'redis:stale');
INSERT INTO global_sanctions (user_id, active, legacy_key) VALUES (2, TRUE, 'redis:globalBan:2');
INSERT INTO audit_events (edition, event_type, occurred_at, legacy_key)
VALUES ('normal', 'legacy_ban', now(), 'redis:audit:stale')");

            var snapshot = new OperationsBackfillSnapshot();
            Assert.Equal(2, await OperationsBackfillService.ReconcileAsync(database,
                new[] { 1L }, new[] { 2L }, new Dictionary<long, IReadOnlyCollection<long>>(),
                snapshot, Array.Empty<Exception>()));

            await using var verify = await database.OpenConnectionAsync();
            Assert.Equal("revoked", await verify.ExecuteScalarAsync<string>("SELECT status FROM sanctions"));
            Assert.False(await verify.ExecuteScalarAsync<bool>("SELECT active FROM global_sanctions"));
            Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM audit_events"));
        });
    }

    private static async Task InSchemaAsync(Func<PostgresDatabase, Task> test)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_test_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            await test(database);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }
}
