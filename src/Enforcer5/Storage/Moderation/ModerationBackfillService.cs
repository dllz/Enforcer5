using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;

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
            CancellationToken cancellationToken = default) =>
            await RunRedisPrimaryAsync(knownGroupIds, reconcile, false, cancellationToken);

        internal async Task<ModerationBackfillResult> RunRedisPrimaryAsync(
            IEnumerable<long> knownGroupIds, bool reconcile, bool includeGlobalRoles,
            CancellationToken cancellationToken = default)
        {
            if (StorageModeProvider.Current != StorageMode.RedisPrimaryPostgresShadow)
                throw new InvalidOperationException(
                    "Moderation backfill requires RedisPrimaryPostgresShadow mode.");

            var scopedGroupIds = knownGroupIds.Distinct().ToArray();
            var snapshot = await _reader.ReadAsync(scopedGroupIds, cancellationToken);
            var result = new ModerationBackfillResult();
            if (reconcile)
                ReconciliationMismatches = await ReconcileAsync(
                    _database, scopedGroupIds, snapshot, cancellationToken);
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
            if (includeGlobalRoles)
            {
                var languageAdmins = await Redis.db.SetMembersAsync("langAdmins");
                await using var connection = await _database.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                foreach (var value in languageAdmins)
                {
                    if (!long.TryParse(value.ToString(), out var userId))
                        throw new InvalidOperationException($"Malformed language admin '{value}'.");
                    var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId,
                        cancellationToken: cancellationToken);
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO global_role_assignments (user_id, role, assigned_at)
VALUES (@UserId, 'global_admin', now())
ON CONFLICT (user_id, role) WHERE revoked_at IS NULL DO NOTHING",
                        new { UserId = user.UserId }, transaction, cancellationToken: cancellationToken));
                }
                await transaction.CommitAsync(cancellationToken);
            }
            return result;
        }

        internal static async Task<int> ReconcileAsync(PostgresDatabase database,
            IEnumerable<long> knownGroupIds, ModerationSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            var groupIds = knownGroupIds.Distinct().ToArray();
            if (groupIds.Length == 0) return 0;
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var resolvedGroups = (await connection.QueryAsync<(long TelegramId, long InternalId)>(
                new CommandDefinition(@"SELECT tc.telegram_chat_id AS TelegramId, g.group_id AS InternalId
FROM groups g JOIN telegram_chats tc ON tc.chat_id = g.chat_id
WHERE tc.telegram_chat_id = ANY(@Ids)", new { Ids = groupIds }, transaction,
                    cancellationToken: cancellationToken))).ToDictionary(x => x.TelegramId, x => x.InternalId);
            if (resolvedGroups.Count != groupIds.Length)
                throw new InvalidOperationException("A moderation reconciliation group principal is missing.");
            var telegramUserIds = snapshot.Roles.Select(x => x.UserId)
                .Concat(snapshot.ElevationBlocks.Select(x => x.UserId))
                .Concat(snapshot.Policies.Select(x => x.UserId))
                .Concat(snapshot.WarningBalances.Select(x => x.UserId)).Distinct();
            var telegramUserIdArray = telegramUserIds.ToArray();
            var resolvedUsers = (await connection.QueryAsync<(long TelegramId, long InternalId)>(
                new CommandDefinition(@"SELECT ta.telegram_user_id AS TelegramId, l.user_id AS InternalId
FROM telegram_accounts ta JOIN user_identity_links l ON l.account_id = ta.account_id
WHERE l.unlinked_at IS NULL AND ta.telegram_user_id = ANY(@Ids)",
                    new { Ids = telegramUserIdArray }, transaction,
                    cancellationToken: cancellationToken))).ToDictionary(x => x.TelegramId, x => x.InternalId);
            if (resolvedUsers.Count != telegramUserIdArray.Length)
                throw new InvalidOperationException("A moderation reconciliation user principal is missing.");
            var generatedGroupIds = resolvedGroups.Values.ToArray();
            var roleKeys = snapshot.Roles.Select(x =>
                (GroupId: resolvedGroups[x.GroupId], UserId: resolvedUsers[x.UserId], x.Role)).ToHashSet();
            var blockKeys = snapshot.ElevationBlocks.Select(x =>
                (GroupId: resolvedGroups[x.GroupId], UserId: resolvedUsers[x.UserId])).ToHashSet();
            var policyKeys = snapshot.Policies.Where(x => x.Enabled).Select(x =>
                (GroupId: resolvedGroups[x.GroupId], UserId: resolvedUsers[x.UserId], x.Policy)).ToHashSet();
            var warningKeys = snapshot.WarningBalances.Select(x =>
                (GroupId: resolvedGroups[x.GroupId], UserId: resolvedUsers[x.UserId], x.WarningType)).ToHashSet();

            var mismatches = 0;
            var roles = await connection.QueryAsync<(long GroupId, long UserId, string Role)>(
                new CommandDefinition(@"SELECT group_id AS GroupId, user_id AS UserId, role AS Role
FROM group_role_assignments WHERE group_id = ANY(@GroupIds) AND revoked_at IS NULL",
                    new { GroupIds = generatedGroupIds }, transaction, cancellationToken: cancellationToken));
            mismatches += roleKeys.Count(value => !roles.Contains(value));
            foreach (var value in roles.Where(value => !roleKeys.Contains(value)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_role_assignments
SET revoked_at = now() WHERE group_id = @GroupId AND user_id = @UserId AND role = @Role AND revoked_at IS NULL",
                    new { value.GroupId, value.UserId, value.Role }, transaction,
                    cancellationToken: cancellationToken));
            }
            var blocks = await connection.QueryAsync<(long GroupId, long UserId)>(new CommandDefinition(@"
SELECT group_id AS GroupId, user_id AS UserId FROM group_elevation_blocks
            WHERE group_id = ANY(@GroupIds) AND revoked_at IS NULL", new { GroupIds = generatedGroupIds }, transaction,
                cancellationToken: cancellationToken));
            mismatches += blockKeys.Count(value => !blocks.Contains(value));
            foreach (var value in blocks.Where(value => !blockKeys.Contains(value)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_elevation_blocks
SET revoked_at = now() WHERE group_id = @GroupId AND user_id = @UserId AND revoked_at IS NULL",
                    new { value.GroupId, value.UserId }, transaction,
                    cancellationToken: cancellationToken));
            }
            var policies = await connection.QueryAsync<(long GroupId, long UserId, string Policy)>(
                new CommandDefinition(@"SELECT group_id AS GroupId, user_id AS UserId, policy AS Policy
FROM (
    SELECT group_id, user_id, 'admin_notifications_disabled' AS policy FROM group_user_preferences WHERE admin_notifications_disabled
    UNION ALL SELECT group_id, user_id, 'reports_blocked' FROM group_user_preferences WHERE reports_blocked
    UNION ALL SELECT group_id, user_id, 'watch_disabled' FROM group_user_preferences WHERE watch_disabled
    UNION ALL SELECT group_id, user_id, 'tag_all_subscribed' FROM group_user_preferences WHERE tag_all_subscribed
) enabled_preferences WHERE group_id = ANY(@GroupIds)", new { GroupIds = generatedGroupIds },
                    transaction, cancellationToken: cancellationToken));
            mismatches += policyKeys.Count(value => !policies.Contains(value));
            foreach (var value in policies.Where(value => !policyKeys.Contains(value)))
            {
                mismatches++;
                var column = PreferenceColumn(value.Policy);
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_user_preferences
SET " + column + @" = FALSE, updated_at = now() WHERE group_id = @GroupId AND user_id = @UserId",
                    new { value.GroupId, value.UserId }, transaction,
                    cancellationToken: cancellationToken));
            }
            var warnings = await connection.QueryAsync<(long GroupId, long UserId, string WarningType)>(
                new CommandDefinition(@"SELECT group_id AS GroupId, user_id AS UserId, warning_type AS WarningType
FROM warning_balances WHERE group_id = ANY(@GroupIds) AND active_quantity > 0",
                    new { GroupIds = generatedGroupIds }, transaction, cancellationToken: cancellationToken));
            foreach (var value in snapshot.WarningBalances)
            {
                var resolved = new
                {
                    GroupId = resolvedGroups[value.GroupId],
                    UserId = resolvedUsers[value.UserId],
                    value.WarningType,
                    value.ActiveQuantity,
                    value.LifetimeQuantity
                };
                var equal = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM warning_balances WHERE group_id = @GroupId AND user_id = @UserId
AND warning_type = @WarningType AND active_quantity = @ActiveQuantity
AND lifetime_quantity = @LifetimeQuantity)", resolved, transaction,
                    cancellationToken: cancellationToken));
                if (!equal) mismatches++;
            }
            foreach (var value in warnings.Where(value => !warningKeys.Contains(value)))
            {
                mismatches++;
                await connection.ExecuteAsync(new CommandDefinition(@"UPDATE warning_balances
SET active_quantity = 0 WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType",
                    new { value.GroupId, value.UserId, value.WarningType }, transaction,
                    cancellationToken: cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
            Console.Error.WriteLine($"Moderation reconciliation mismatches={mismatches}.");
            return mismatches;
        }

        private static string PreferenceColumn(string policy) => policy switch
        {
            GroupUserPolicyNames.WatchDisabled => "watch_disabled",
            GroupUserPolicyNames.ReportsBlocked => "reports_blocked",
            GroupUserPolicyNames.AdminNotificationsDisabled => "admin_notifications_disabled",
            GroupUserPolicyNames.TagAllSubscribed => "tag_all_subscribed",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
    }
}
