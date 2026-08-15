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
                await using var transaction = await connection.BeginTransactionAsync();
                var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, 1, "group");
                await connection.ExecuteAsync(@"INSERT INTO group_entitlements (group_id, entitlement, enabled)
VALUES (@GroupId, 'premium', TRUE);
INSERT INTO group_content_items (group_id, content_type, content) VALUES (@GroupId, 'rules', 'old');
INSERT INTO custom_commands (group_id, trigger, content) VALUES (@GroupId, 'old', 'old')",
                    new { group.GroupId }, transaction);
                await transaction.CommitAsync();
            }

            var snapshot = new PrincipalContentBackfill(Array.Empty<UserPrincipal>(),
                new[] { new GroupPrincipal(1, "group") }, Array.Empty<GroupEntitlement>(),
                Array.Empty<GroupContentItem>(), Array.Empty<CustomCommand>());
            Assert.Equal(3, await PrincipalContentBackfillService.ReconcileAsync(database, snapshot));

            await using var verify = await database.OpenConnectionAsync();
            Assert.False(await verify.ExecuteScalarAsync<bool>(
                "SELECT enabled FROM group_entitlements WHERE entitlement = 'premium'"));
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
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, 1, "group");
                var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, 2, "user");
                await connection.ExecuteAsync(@"INSERT INTO group_role_assignments (group_id, user_id, role, assigned_at) VALUES (@GroupId, @UserId, 'moderator', now());
INSERT INTO group_elevation_blocks (group_id, user_id, blocked_at) VALUES (@GroupId, @UserId, now());
INSERT INTO group_user_preferences (group_id, user_id, watch_disabled, updated_at)
VALUES (@GroupId, @UserId, TRUE, now());
INSERT INTO warning_balances (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, 'standard', 2, 4)", new { group.GroupId, user.UserId }, transaction);
                await transaction.CommitAsync();
            }

            var snapshot = new ModerationSnapshot(Array.Empty<GroupRoleAssignment>(),
                Array.Empty<GroupElevationBlock>(), Array.Empty<GroupUserPolicy>(),
                Array.Empty<WarningBalance>());
            Assert.Equal(4, await ModerationBackfillService.ReconcileAsync(database, new[] { 1L }, snapshot));

            await using var verify = await database.OpenConnectionAsync();
            Assert.NotNull(await verify.ExecuteScalarAsync<DateTimeOffset?>(
                "SELECT revoked_at FROM group_role_assignments"));
            Assert.NotNull(await verify.ExecuteScalarAsync<DateTimeOffset?>(
                "SELECT revoked_at FROM group_elevation_blocks"));
            Assert.False(await verify.ExecuteScalarAsync<bool>("SELECT watch_disabled FROM group_user_preferences"));
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
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, 1, "group");
                var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, 2, "user");
                await connection.ExecuteAsync(@"INSERT INTO sanctions
(group_id, account_id, user_id_at_time, kind, status, edition, imposed_at, legacy_key)
VALUES (@GroupId, @AccountId, @UserId, 'ban', 'active', 'normal', now(), 'redis:stale');
INSERT INTO global_sanctions (account_id, user_id_at_time, kind, status, imposed_at, legacy_key)
VALUES (@AccountId, @UserId, 'ban', 'active', now(), 'redis:globalBan:2');
INSERT INTO group_audit_events (group_id, event_type, occurred_at, legacy_key)
VALUES (@GroupId, 'legacy_ban', now(), 'redis:audit:stale')",
                    new { group.GroupId, user.AccountId, user.UserId }, transaction);
                await transaction.CommitAsync();
            }

            var snapshot = new OperationsBackfillSnapshot();
            var outcome = await OperationsBackfillService.ReconcileAsync(database,
                new[] { 1L }, new[] { 2L }, new Dictionary<long, IReadOnlyCollection<long>>(),
                snapshot, Array.Empty<Exception>());

            // Two PostgreSQL rows have no Redis counterpart and are revoked. The audit event is
            // durable history and is reported rather than deleted, so it is not an orphan.
            Assert.Equal(2, outcome.PostgresOrphans);
            Assert.False(outcome.RemovalsSkipped);

            await using var verify = await database.OpenConnectionAsync();
            Assert.Equal("revoked", await verify.ExecuteScalarAsync<string>("SELECT status FROM sanctions"));
            Assert.Equal("revoked", await verify.ExecuteScalarAsync<string>("SELECT status FROM global_sanctions"));
            Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM group_audit_events"));
        });
    }

    [PostgresFact]
    public async Task AnIncompleteSnapshotWithholdsRemovalsButStillCompares()
    {
        // This used to return zero the moment the snapshot carried an error, printing a count
        // indistinguishable from a clean run - and the operations snapshot always had errors at
        // full scale, so reconciliation had never actually compared anything.
        await InSchemaAsync(async database =>
        {
            await using (var connection = await database.OpenConnectionAsync())
            {
                await using var transaction = await connection.BeginTransactionAsync();
                var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, 1, "group");
                var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, 2, "user");
                await connection.ExecuteAsync(@"INSERT INTO sanctions
(group_id, account_id, user_id_at_time, kind, status, edition, imposed_at, legacy_key)
VALUES (@GroupId, @AccountId, @UserId, 'ban', 'active', 'normal', now(), 'redis:stale')",
                    new { group.GroupId, user.AccountId, user.UserId }, transaction);
                await transaction.CommitAsync();
            }

            var outcome = await OperationsBackfillService.ReconcileAsync(database,
                new[] { 1L }, new[] { 2L }, new Dictionary<long, IReadOnlyCollection<long>>(),
                new OperationsBackfillSnapshot(), new[] { new FormatException("bad record") });

            // The comparison still ran and found the orphan...
            Assert.Equal(1, outcome.PostgresOrphans);
            Assert.True(outcome.RemovalsSkipped);

            // ...but did not act on it, because a partial snapshot cannot authorise a deletion.
            await using var verify = await database.OpenConnectionAsync();
            Assert.Equal("active", await verify.ExecuteScalarAsync<string>("SELECT status FROM sanctions"));
        });
    }

    [PostgresFact]
    public async Task ARecordPresentInRedisButMissingFromPostgresIsDetected()
    {
        // The direction that did not exist in any domain. Every compare looked only for PostgreSQL
        // rows absent from Redis, so a backfill that silently skipped records reconciled clean by
        // construction - which is exactly the failure mode the operations domain was hiding.
        await InSchemaAsync(async database =>
        {
            var snapshot = new OperationsBackfillSnapshot();
            snapshot.Sanctions.Add(new Sanction(0, 1, 2, SanctionKinds.Ban, SanctionStatuses.Active,
                Editions.Normal, null, null, DateTimeOffset.UtcNow, null, null, null, null,
                "redis:never-loaded"));

            var outcome = await OperationsBackfillService.ReconcileAsync(database,
                new[] { 1L }, new[] { 2L }, new Dictionary<long, IReadOnlyCollection<long>>(),
                snapshot, Array.Empty<Exception>());

            Assert.Equal(1, outcome.MissingFromPostgres);
            Assert.Equal(0, outcome.PostgresOrphans);
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
