using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Storage.Moderation
{
    internal sealed class GroupRoleAssignmentRepository
    {
        private readonly PostgresDatabase _database;

        internal GroupRoleAssignmentRepository(PostgresDatabase database) =>
            _database = database ?? throw new ArgumentNullException(nameof(database));

        internal async Task UpsertActiveAsync(GroupRoleAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                assignment.GroupId, cancellationToken: cancellationToken)).GroupId;
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction,
                assignment.UserId, cancellationToken: cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_role_assignments (group_id, user_id, role, assigned_at, revoked_at)
VALUES (@GroupId, @UserId, @Role, @AssignedAt, NULL)
ON CONFLICT (group_id, user_id, role) WHERE revoked_at IS NULL DO NOTHING", new
            {
                GroupId = groupId,
                UserId = user.UserId,
                assignment.Role,
                assignment.AssignedAt
            }, transaction,
                cancellationToken: cancellationToken));
            if (StorageRouting.GroupRedisMirrorEnabled(assignment.GroupId))
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "roles",
                    $"role:{assignment.GroupId}:{assignment.UserId}:{assignment.Role}", "set",
                    new { groupId = assignment.GroupId, userId = assignment.UserId, role = assignment.Role, active = true }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task RevokeAsync(long groupId, long userId, string role,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var resolvedGroupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                groupId, cancellationToken: cancellationToken)).GroupId;
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId,
                cancellationToken: cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_role_assignments SET revoked_at = now()
WHERE group_id = @GroupId AND user_id = @UserId AND role = @Role AND revoked_at IS NULL",
                new { GroupId = resolvedGroupId, UserId = user.UserId, Role = role }, transaction, cancellationToken: cancellationToken));
            if (StorageRouting.GroupRedisMirrorEnabled(groupId))
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "roles",
                    $"role:{groupId}:{userId}:{role}", "set", new { groupId, userId, role, active = false }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<IReadOnlyList<GroupRoleAssignment>> ListActiveAsync(long groupId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var resolvedGroupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                groupId, cancellationToken: cancellationToken)).GroupId;
            var rows = await connection.QueryAsync<GroupRoleAssignment>(new CommandDefinition(@"
SELECT tc.telegram_chat_id AS GroupId, ta.telegram_user_id AS UserId, r.role AS Role,
       assigned_at AS AssignedAt, revoked_at AS RevokedAt
FROM group_role_assignments r
JOIN groups g ON g.group_id = r.group_id
JOIN telegram_chats tc ON tc.chat_id = g.chat_id
JOIN user_identity_links l ON l.user_id = r.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts ta ON ta.account_id = l.account_id
WHERE r.group_id = @GroupId AND revoked_at IS NULL
ORDER BY role, ta.telegram_user_id", new { GroupId = resolvedGroupId }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return rows.AsList();
        }
    }

    internal sealed class GroupElevationBlockRepository
    {
        private readonly PostgresDatabase _database;

        internal GroupElevationBlockRepository(PostgresDatabase database) =>
            _database = database ?? throw new ArgumentNullException(nameof(database));

        internal async Task UpsertActiveAsync(GroupElevationBlock block,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                block.GroupId, cancellationToken: cancellationToken)).GroupId;
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, block.UserId,
                cancellationToken: cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_elevation_blocks (group_id, user_id, blocked_at, revoked_at)
VALUES (@GroupId, @UserId, @BlockedAt, NULL)
ON CONFLICT (group_id, user_id) WHERE revoked_at IS NULL DO NOTHING", new
            {
                GroupId = groupId,
                UserId = user.UserId,
                block.BlockedAt
            }, transaction, cancellationToken: cancellationToken));
            if (StorageRouting.GroupRedisMirrorEnabled(block.GroupId))
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "elevation-blocks",
                    $"elevation-block:{block.GroupId}:{block.UserId}", "set",
                    new { groupId = block.GroupId, userId = block.UserId, active = true }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task RevokeAsync(long groupId, long userId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var resolvedGroupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                groupId, cancellationToken: cancellationToken)).GroupId;
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId,
                cancellationToken: cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_elevation_blocks SET revoked_at = now()
WHERE group_id = @GroupId AND user_id = @UserId AND revoked_at IS NULL",
                new { GroupId = resolvedGroupId, UserId = user.UserId }, transaction, cancellationToken: cancellationToken));
            if (StorageRouting.GroupRedisMirrorEnabled(groupId))
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "elevation-blocks",
                    $"elevation-block:{groupId}:{userId}", "set", new { groupId, userId, active = false }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class GroupUserPolicyRepository
    {
        private readonly PostgresDatabase _database;

        internal GroupUserPolicyRepository(PostgresDatabase database) =>
            _database = database ?? throw new ArgumentNullException(nameof(database));

        internal async Task UpsertAsync(GroupUserPolicy policy,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                policy.GroupId, cancellationToken: cancellationToken)).GroupId;
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, policy.UserId,
                cancellationToken: cancellationToken);
            var column = PreferenceColumn(policy.Policy);
            var preferences = new
            {
                GroupId = groupId,
                UserId = user.UserId,
                AdminNotificationsDisabled = policy.Policy == GroupUserPolicyNames.AdminNotificationsDisabled && policy.Enabled,
                ReportsBlocked = policy.Policy == GroupUserPolicyNames.ReportsBlocked && policy.Enabled,
                WatchDisabled = policy.Policy == GroupUserPolicyNames.WatchDisabled && policy.Enabled,
                TagAllSubscribed = policy.Policy == GroupUserPolicyNames.TagAllSubscribed && policy.Enabled,
                policy.UpdatedAt
            };
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_user_preferences
    (group_id, user_id, admin_notifications_disabled, reports_blocked, watch_disabled,
     tag_all_subscribed, updated_at)
VALUES (@GroupId, @UserId, @AdminNotificationsDisabled, @ReportsBlocked, @WatchDisabled,
        @TagAllSubscribed, @UpdatedAt)
ON CONFLICT (group_id, user_id) DO UPDATE
SET " + column + @" = EXCLUDED." + column + @", updated_at = EXCLUDED.updated_at", preferences, transaction,
                cancellationToken: cancellationToken));
            if (StorageRouting.GroupRedisMirrorEnabled(policy.GroupId))
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "policies",
                    $"policy:{policy.GroupId}:{policy.UserId}:{policy.Policy}", "set",
                    new { groupId = policy.GroupId, userId = policy.UserId, policy = policy.Policy, enabled = policy.Enabled }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<bool> IsEnabledAsync(long groupId, long userId, string policy,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var resolvedGroupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                groupId, cancellationToken: cancellationToken)).GroupId;
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId,
                cancellationToken: cancellationToken);
            var column = PreferenceColumn(policy);
            var enabled = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT COALESCE((
    SELECT " + column + @" FROM group_user_preferences
    WHERE group_id = @GroupId AND user_id = @UserId
), FALSE)", new { GroupId = resolvedGroupId, UserId = user.UserId }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return enabled;
        }

        internal async Task<IReadOnlyList<long>> ListEnabledUserIdsAsync(long groupId, string policy,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var resolvedGroupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                groupId, cancellationToken: cancellationToken)).GroupId;
            var column = PreferenceColumn(policy);
            var rows = await connection.QueryAsync<long>(new CommandDefinition(@"
SELECT ta.telegram_user_id
FROM group_user_preferences p
JOIN user_identity_links l ON l.user_id = p.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts ta ON ta.account_id = l.account_id
WHERE p.group_id = @GroupId AND " + column + @"
ORDER BY ta.telegram_user_id", new { GroupId = resolvedGroupId }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return rows.AsList();
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

    internal sealed class WarningRepository
    {
        private readonly PostgresDatabase _database;

        internal WarningRepository(PostgresDatabase database) =>
            _database = database ?? throw new ArgumentNullException(nameof(database));

        internal async Task<WarningBalance> GetBalanceAsync(long groupId, long userId,
            string warningType, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var principals = await ResolveAsync(connection, transaction, groupId, userId,
                cancellationToken);
            var balance = await connection.QuerySingleOrDefaultAsync<WarningBalance>(new CommandDefinition(@"
SELECT group_id AS GroupId, user_id AS UserId, warning_type AS WarningType,
       active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity, now() AS UpdatedAt
FROM warning_balances
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType",
                new { principals.GroupId, UserId = principals.User.UserId, WarningType = warningType }, transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return balance is null ? null : balance with { GroupId = groupId, UserId = userId };
        }

        internal async Task UpsertBackfillAsync(WarningBalance balance,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var principals = await ResolveAsync(connection, transaction, balance.GroupId, balance.UserId,
                cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @WarningType, @ActiveQuantity, @LifetimeQuantity)
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = EXCLUDED.active_quantity,
    lifetime_quantity = EXCLUDED.lifetime_quantity", new
            {
                principals.GroupId,
                UserId = principals.User.UserId,
                balance.WarningType,
                balance.ActiveQuantity,
                balance.LifetimeQuantity
            }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<WarningIssueResult> IssuePrewarningAsync(long groupId, long userId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var telegramGroupId = groupId;
            var telegramUserId = userId;
            var principals = await ResolveAsync(connection, transaction, groupId, userId, cancellationToken);
            groupId = principals.GroupId;
            userId = principals.User.UserId;
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @WarningType, 1, 1)
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = GREATEST(0, warning_balances.active_quantity) + 1,
    lifetime_quantity = GREATEST(0, warning_balances.lifetime_quantity) + 1
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, groupId, userId,
                principals.User.AccountId, WarningTypes.Prewarning, WarningOperations.Add, 1,
                cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, telegramGroupId, telegramUserId,
                new[] { new WarningIssueResult(balance.ActiveQuantity, 1, balance.LifetimeQuantity) },
                new[] { WarningTypes.Prewarning }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WarningIssueResult(balance.ActiveQuantity, 1, balance.LifetimeQuantity);
        }

        internal async Task<WarningIssueResult> IssueAsync(long groupId, long userId,
            string warningType, long quantity = 1, CancellationToken cancellationToken = default)
        {
            quantity = Math.Max(1, quantity);
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var telegramGroupId = groupId;
            var telegramUserId = userId;
            var principals = await ResolveAsync(connection, transaction, groupId, userId, cancellationToken);
            groupId = principals.GroupId;
            userId = principals.User.UserId;
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @WarningType, @Quantity, @Quantity)
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = GREATEST(0, warning_balances.active_quantity) + @Quantity,
    lifetime_quantity = GREATEST(0, warning_balances.lifetime_quantity) + @Quantity
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = warningType, Quantity = quantity },
                transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, groupId, userId,
                principals.User.AccountId, warningType, WarningOperations.Add, quantity,
                cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, telegramGroupId, telegramUserId,
                new[] { new WarningIssueResult(balance.ActiveQuantity, quantity, balance.LifetimeQuantity) },
                new[] { warningType }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WarningIssueResult(balance.ActiveQuantity, quantity, balance.LifetimeQuantity);
        }

        internal async Task<WarningIssueResult> IssueWarningAsync(long groupId, long userId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var telegramGroupId = groupId;
            var telegramUserId = userId;
            var principals = await ResolveAsync(connection, transaction, groupId, userId, cancellationToken);
            groupId = principals.GroupId;
            userId = principals.User.UserId;

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @WarningType, 0, 0)
ON CONFLICT (group_id, user_id, warning_type) DO NOTHING",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));
            var prewarningBalance = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT GREATEST(0, active_quantity) FROM warning_balances
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType
FOR UPDATE", new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE warning_balances SET active_quantity = 0
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));
            if (prewarningBalance > 0)
                await InsertEventAsync(connection, transaction, groupId, userId,
                    principals.User.AccountId, WarningTypes.Prewarning, WarningOperations.Reset,
                    prewarningBalance, cancellationToken);

            var quantity = checked(1 + prewarningBalance);
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @WarningType, @Quantity, @Quantity)
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = GREATEST(0, warning_balances.active_quantity) + @Quantity,
    lifetime_quantity = GREATEST(0, warning_balances.lifetime_quantity) + @Quantity
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Standard, Quantity = quantity },
                transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, groupId, userId,
                principals.User.AccountId, WarningTypes.Standard, WarningOperations.Add, quantity,
                cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, telegramGroupId, telegramUserId,
                new[]
                {
                    new WarningIssueResult(0, 0, 0),
                    new WarningIssueResult(balance.ActiveQuantity, quantity, balance.LifetimeQuantity)
                }, new[] { WarningTypes.Prewarning, WarningTypes.Standard }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WarningIssueResult(balance.ActiveQuantity, quantity, balance.LifetimeQuantity);
        }

        internal Task<WarningIssueResult> ResetAsync(long groupId, long userId, string warningType,
            CancellationToken cancellationToken = default) =>
            ReduceAsync(groupId, userId, warningType, null, WarningOperations.Reset, cancellationToken);

        internal Task<WarningIssueResult> DecrementAsync(long groupId, long userId, string warningType,
            long quantity = 1, CancellationToken cancellationToken = default) =>
            ReduceAsync(groupId, userId, warningType, Math.Max(1, quantity),
                WarningOperations.Remove, cancellationToken);

        private async Task<WarningIssueResult> ReduceAsync(long groupId, long userId, string warningType,
            long? quantity, string operation, CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var telegramGroupId = groupId;
            var telegramUserId = userId;
            var principals = await ResolveAsync(connection, transaction, groupId, userId, cancellationToken);
            groupId = principals.GroupId;
            userId = principals.User.UserId;
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @WarningType, 0, 0)
ON CONFLICT (group_id, user_id, warning_type) DO NOTHING",
                new { GroupId = groupId, UserId = userId, WarningType = warningType }, transaction,
                cancellationToken: cancellationToken));
            var previous = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT active_quantity FROM warning_balances
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType FOR UPDATE",
                new { GroupId = groupId, UserId = userId, WarningType = warningType }, transaction,
                cancellationToken: cancellationToken));
            var changed = quantity.HasValue ? Math.Min(previous, quantity.Value) : previous;
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
UPDATE warning_balances
SET active_quantity = active_quantity - @Changed
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = warningType, Changed = changed },
                transaction, cancellationToken: cancellationToken));
            if (changed > 0)
                await InsertEventAsync(connection, transaction, groupId, userId, principals.User.AccountId,
                    warningType, operation, changed, cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, telegramGroupId, telegramUserId,
                new[] { new WarningIssueResult(balance.ActiveQuantity, changed, balance.LifetimeQuantity) },
                new[] { warningType }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WarningIssueResult(balance.ActiveQuantity, changed, balance.LifetimeQuantity);
        }

        private static Task InsertEventAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, long groupId, long userId,
            long accountId, string warningType, string operation, long quantity,
            CancellationToken cancellationToken)
        {
            return connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_events
    (group_id, account_id, user_id_at_time, warning_type, operation, quantity, occurred_at)
VALUES (@GroupId, @AccountId, @UserId, @WarningType, @Operation, @Quantity, now())", new
            {
                GroupId = groupId,
                UserId = userId,
                AccountId = accountId,
                WarningType = warningType,
                Operation = operation,
                Quantity = quantity
            }, transaction, cancellationToken: cancellationToken));
        }

        private static Task EnqueueWarningMirrorAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, long groupId, long userId,
            IReadOnlyList<WarningIssueResult> results, IReadOnlyList<string> warningTypes,
            CancellationToken cancellationToken)
        {
            if (!StorageRouting.GroupRedisMirrorEnabled(groupId)) return Task.CompletedTask;
            var balances = warningTypes.Select((type, index) => new
            {
                warningType = type,
                activeQuantity = results[index].Balance,
                lifetimeQuantity = results[index].LifetimeQuantity
            }).ToArray();
            return CompatibilityOutbox.EnqueueAsync(connection, transaction, "warnings",
                $"warning:{groupId}:{userId}", "set", new { groupId, userId, balances }, cancellationToken);
        }

        private sealed class BalanceRow
        {
            public long ActiveQuantity { get; set; }
            public long LifetimeQuantity { get; set; }
        }

        private static async Task<ResolvedPrincipals> ResolveAsync(
            System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
            long telegramGroupId, long telegramUserId, CancellationToken cancellationToken)
        {
            var groupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction,
                telegramGroupId, cancellationToken: cancellationToken)).GroupId;
            var user = await PrincipalResolver.EnsureUserAsync(connection, transaction,
                telegramUserId, cancellationToken: cancellationToken);
            return new ResolvedPrincipals(groupId, user);
        }

        private readonly record struct ResolvedPrincipals(long GroupId, PrincipalResolver.UserIds User);
    }
}
