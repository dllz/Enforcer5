using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Moderation
{
    internal sealed class ModerationBackfillService
    {
        private readonly LegacyModerationReader _reader;
        private readonly PostgresDatabase _database;
        private readonly GroupRoleAssignmentRepository _roles;
        private readonly GroupElevationBlockRepository _blocks;
        private readonly GroupUserPolicyRepository _policies;
        private readonly WarningRepository _warnings;
        internal int ReconciliationMismatches { get; private set; }

        internal ModerationBackfillService(PostgresDatabase database)
        {
            _database = database;
            _reader = new LegacyModerationReader();
            _roles = new GroupRoleAssignmentRepository(database);
            _blocks = new GroupElevationBlockRepository(database);
            _policies = new GroupUserPolicyRepository(database);
            _warnings = new WarningRepository(database);
        }

        internal async Task<ModerationBackfillResult> RunRedisPrimaryAsync(
            IEnumerable<long> knownGroupIds, bool reconcile = false,
            CancellationToken cancellationToken = default)
        {
            if (StorageModeProvider.Current != StorageMode.RedisPrimaryPostgresShadow)
                throw new InvalidOperationException(
                    "Moderation backfill requires RedisPrimaryPostgresShadow mode.");

            var snapshot = await _reader.ReadAsync(knownGroupIds, cancellationToken);
            var result = new ModerationBackfillResult();
            if (reconcile)
                ReconciliationMismatches = await ReconcileAsync(
                    _database, knownGroupIds, snapshot, cancellationToken);
            foreach (var role in snapshot.Roles)
            {
                await _roles.UpsertActiveAsync(role, cancellationToken);
                result.RolesWritten++;
            }
            foreach (var block in snapshot.ElevationBlocks)
            {
                await _blocks.UpsertActiveAsync(block, cancellationToken);
                result.ElevationBlocksWritten++;
            }
            foreach (var policy in snapshot.Policies)
            {
                await _policies.UpsertAsync(policy, cancellationToken);
                result.PoliciesWritten++;
            }
            foreach (var balance in snapshot.WarningBalances)
            {
                await _warnings.UpsertBackfillAsync(balance, cancellationToken);
                result.WarningBalancesWritten++;
            }
            var languageAdmins = await Redis.db.SetMembersAsync("langAdmins");
            await using (var connection = await _database.OpenConnectionAsync(cancellationToken))
            {
                foreach (var value in languageAdmins)
                {
                    if (!long.TryParse(value.ToString(), out var userId))
                        throw new InvalidOperationException($"Malformed language admin '{value}'.");
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO global_roles (user_id, role, active, updated_at)
VALUES (@UserId, 'language_admin', TRUE, now())
ON CONFLICT (user_id, role) DO UPDATE SET active = TRUE, updated_at = now()",
                        new { UserId = userId }, cancellationToken: cancellationToken));
                }
            }
            return result;
        }

        internal static async Task<int> ReconcileAsync(PostgresDatabase database,
            IEnumerable<long> knownGroupIds, ModerationSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            var groupIds = knownGroupIds.Distinct().ToArray();
            if (groupIds.Length == 0) return 0;
            var roleKeys = snapshot.Roles.Select(x => (x.GroupId, x.UserId, x.Role)).ToHashSet();
            var blockKeys = snapshot.ElevationBlocks.Select(x => (x.GroupId, x.UserId)).ToHashSet();
            var policyKeys = snapshot.Policies.Where(x => x.Enabled)
                .Select(x => (x.GroupId, x.UserId, x.Policy)).ToHashSet();
            var warningKeys = snapshot.WarningBalances
                .Select(x => (x.GroupId, x.UserId, x.WarningType)).ToHashSet();

            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var mismatches = 0;
            var roles = await connection.QueryAsync<(long GroupId, long UserId, string Role)>(
                new CommandDefinition(@"SELECT group_id AS GroupId, user_id AS UserId, role AS Role
FROM group_role_assignments WHERE group_id = ANY(@GroupIds) AND revoked_at IS NULL",
                    new { GroupIds = groupIds }, transaction, cancellationToken: cancellationToken));
            mismatches += roleKeys.Count(value => !roles.Contains(value));
            foreach (var value in roles.Where(value => !roleKeys.Contains(value)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_role_assignments
SET revoked_at = now() WHERE group_id = @GroupId AND user_id = @UserId AND role = @Role AND revoked_at IS NULL",
                    value, transaction, cancellationToken: cancellationToken));
            }
            var blocks = await connection.QueryAsync<(long GroupId, long UserId)>(new CommandDefinition(@"
SELECT group_id AS GroupId, user_id AS UserId FROM group_elevation_blocks
WHERE group_id = ANY(@GroupIds) AND revoked_at IS NULL", new { GroupIds = groupIds }, transaction,
                cancellationToken: cancellationToken));
            mismatches += blockKeys.Count(value => !blocks.Contains(value));
            foreach (var value in blocks.Where(value => !blockKeys.Contains(value)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_elevation_blocks
SET revoked_at = now() WHERE group_id = @GroupId AND user_id = @UserId AND revoked_at IS NULL",
                    value, transaction, cancellationToken: cancellationToken));
            }
            var policies = await connection.QueryAsync<(long GroupId, long UserId, string Policy)>(
                new CommandDefinition(@"SELECT group_id AS GroupId, user_id AS UserId, policy AS Policy
FROM group_user_policies WHERE group_id = ANY(@GroupIds) AND enabled", new { GroupIds = groupIds },
                    transaction, cancellationToken: cancellationToken));
            mismatches += policyKeys.Count(value => !policies.Contains(value));
            foreach (var value in policies.Where(value => !policyKeys.Contains(value)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_user_policies
SET enabled = FALSE, updated_at = now() WHERE group_id = @GroupId AND user_id = @UserId AND policy = @Policy",
                    value, transaction, cancellationToken: cancellationToken));
            }
            var warnings = await connection.QueryAsync<(long GroupId, long UserId, string WarningType)>(
                new CommandDefinition(@"SELECT group_id AS GroupId, user_id AS UserId, warning_type AS WarningType
FROM warning_balances WHERE group_id = ANY(@GroupIds) AND active_quantity > 0",
                    new { GroupIds = groupIds }, transaction, cancellationToken: cancellationToken));
            foreach (var value in snapshot.WarningBalances)
            {
                var equal = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM warning_balances WHERE group_id = @GroupId AND user_id = @UserId
AND warning_type = @WarningType AND active_quantity = @ActiveQuantity
AND lifetime_quantity = @LifetimeQuantity)", value, transaction,
                    cancellationToken: cancellationToken));
                if (!equal) mismatches++;
            }
            foreach (var value in warnings.Where(value => !warningKeys.Contains(value)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE warning_balances
SET active_quantity = 0, updated_at = now() WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType",
                    value, transaction, cancellationToken: cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
            Console.Error.WriteLine($"Moderation reconciliation mismatches={mismatches}.");
            return mismatches;
        }
    }
}
