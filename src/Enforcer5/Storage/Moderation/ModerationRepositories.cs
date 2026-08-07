using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

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
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_role_assignments (group_id, user_id, role, assigned_at, revoked_at)
VALUES (@GroupId, @UserId, @Role, @AssignedAt, NULL)
ON CONFLICT (group_id, user_id, role) DO UPDATE
SET assigned_at = EXCLUDED.assigned_at, revoked_at = NULL", assignment, transaction,
                cancellationToken: cancellationToken));
            if (StorageModeProvider.RedisMirrorEnabled)
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
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_role_assignments SET revoked_at = now()
WHERE group_id = @GroupId AND user_id = @UserId AND role = @Role AND revoked_at IS NULL",
                new { GroupId = groupId, UserId = userId, Role = role }, transaction, cancellationToken: cancellationToken));
            if (StorageModeProvider.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "roles",
                    $"role:{groupId}:{userId}:{role}", "set", new { groupId, userId, role, active = false }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<IReadOnlyList<GroupRoleAssignment>> ListActiveAsync(long groupId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<GroupRoleAssignment>(new CommandDefinition(@"
SELECT group_id AS GroupId, user_id AS UserId, role AS Role,
       assigned_at AS AssignedAt, revoked_at AS RevokedAt
FROM group_role_assignments WHERE group_id = @GroupId AND revoked_at IS NULL
ORDER BY role, user_id", new { GroupId = groupId }, cancellationToken: cancellationToken));
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
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_elevation_blocks (group_id, user_id, blocked_at, revoked_at)
VALUES (@GroupId, @UserId, @BlockedAt, NULL)
ON CONFLICT (group_id, user_id) DO UPDATE
SET blocked_at = EXCLUDED.blocked_at, revoked_at = NULL", block, transaction, cancellationToken: cancellationToken));
            if (StorageModeProvider.RedisMirrorEnabled)
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
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_elevation_blocks SET revoked_at = now()
WHERE group_id = @GroupId AND user_id = @UserId AND revoked_at IS NULL",
                new { GroupId = groupId, UserId = userId }, transaction, cancellationToken: cancellationToken));
            if (StorageModeProvider.RedisMirrorEnabled)
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
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_user_policies (group_id, user_id, policy, enabled, updated_at)
VALUES (@GroupId, @UserId, @Policy, @Enabled, @UpdatedAt)
ON CONFLICT (group_id, user_id, policy) DO UPDATE
SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at", policy, transaction,
                cancellationToken: cancellationToken));
            if (StorageModeProvider.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "policies",
                    $"policy:{policy.GroupId}:{policy.UserId}:{policy.Policy}", "set",
                    new { groupId = policy.GroupId, userId = policy.UserId, policy = policy.Policy, enabled = policy.Enabled }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<bool> IsEnabledAsync(long groupId, long userId, string policy,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT COALESCE((
    SELECT enabled FROM group_user_policies
    WHERE group_id = @GroupId AND user_id = @UserId AND policy = @Policy
), FALSE)", new { GroupId = groupId, UserId = userId, Policy = policy },
                cancellationToken: cancellationToken));
        }

        internal async Task<IReadOnlyList<long>> ListEnabledUserIdsAsync(long groupId, string policy,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<long>(new CommandDefinition(@"
SELECT user_id FROM group_user_policies
WHERE group_id = @GroupId AND policy = @Policy AND enabled
ORDER BY user_id", new { GroupId = groupId, Policy = policy },
                cancellationToken: cancellationToken));
            return rows.AsList();
        }
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
            return await connection.QuerySingleOrDefaultAsync<WarningBalance>(new CommandDefinition(@"
SELECT group_id AS GroupId, user_id AS UserId, warning_type AS WarningType,
       active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity,
       updated_at AS UpdatedAt
FROM warning_balances
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType",
                new { GroupId = groupId, UserId = userId, WarningType = warningType },
                cancellationToken: cancellationToken));
        }

        internal async Task UpsertBackfillAsync(WarningBalance balance,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity, updated_at)
VALUES (@GroupId, @UserId, @WarningType, @ActiveQuantity, @LifetimeQuantity, @UpdatedAt)
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = EXCLUDED.active_quantity,
    lifetime_quantity = EXCLUDED.lifetime_quantity,
    updated_at = EXCLUDED.updated_at", balance, transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, balance.GroupId, balance.UserId,
                balance.WarningType, WarningOperations.Backfill, balance.ActiveQuantity,
                balance.ActiveQuantity, balance.LifetimeQuantity, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<WarningIssueResult> IssuePrewarningAsync(long groupId, long userId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity, updated_at)
VALUES (@GroupId, @UserId, @WarningType, 1, 1, now())
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = GREATEST(0, warning_balances.active_quantity) + 1,
    lifetime_quantity = GREATEST(0, warning_balances.lifetime_quantity) + 1,
    updated_at = now()
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, groupId, userId, WarningTypes.Prewarning,
                WarningOperations.Issue, 1, balance.ActiveQuantity, balance.LifetimeQuantity,
                cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, groupId, userId,
                new[] { new WarningIssueResult(balance.ActiveQuantity, 1, balance.LifetimeQuantity) },
                new[] { WarningTypes.Prewarning }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WarningIssueResult(balance.ActiveQuantity, 1, balance.LifetimeQuantity);
        }

        internal async Task<WarningIssueResult> IssueAsync(long groupId, long userId,
            string warningType, long quantity = 1, CancellationToken cancellationToken = default)
        {
            quantity = Math.Max(0, quantity);
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity, updated_at)
VALUES (@GroupId, @UserId, @WarningType, @Quantity, @Quantity, now())
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = GREATEST(0, warning_balances.active_quantity) + @Quantity,
    lifetime_quantity = GREATEST(0, warning_balances.lifetime_quantity) + @Quantity,
    updated_at = now()
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = warningType, Quantity = quantity },
                transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, groupId, userId, warningType,
                WarningOperations.Issue, quantity, balance.ActiveQuantity, balance.LifetimeQuantity,
                cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, groupId, userId,
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

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity, updated_at)
VALUES (@GroupId, @UserId, @WarningType, 0, 0, now())
ON CONFLICT (group_id, user_id, warning_type) DO NOTHING",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));
            var prewarningBalance = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
SELECT GREATEST(0, active_quantity) FROM warning_balances
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType
FOR UPDATE", new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE warning_balances SET active_quantity = 0, updated_at = now()
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @WarningType",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Prewarning },
                transaction, cancellationToken: cancellationToken));

            var quantity = checked(1 + prewarningBalance);
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity, updated_at)
VALUES (@GroupId, @UserId, @WarningType, @Quantity, @Quantity, now())
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = GREATEST(0, warning_balances.active_quantity) + @Quantity,
    lifetime_quantity = GREATEST(0, warning_balances.lifetime_quantity) + @Quantity,
    updated_at = now()
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = WarningTypes.Standard, Quantity = quantity },
                transaction, cancellationToken: cancellationToken));
            await InsertEventAsync(connection, transaction, groupId, userId, WarningTypes.Standard,
                WarningOperations.Issue, quantity, balance.ActiveQuantity, balance.LifetimeQuantity,
                cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, groupId, userId,
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
            ReduceAsync(groupId, userId, warningType, Math.Max(0, quantity),
                WarningOperations.Decrement, cancellationToken);

        private async Task<WarningIssueResult> ReduceAsync(long groupId, long userId, string warningType,
            long? quantity, string operation, CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var balance = await connection.QuerySingleAsync<BalanceRow>(new CommandDefinition(@"
INSERT INTO warning_balances
    (group_id, user_id, warning_type, active_quantity, lifetime_quantity, updated_at)
VALUES (@GroupId, @UserId, @WarningType, 0, 0, now())
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = CASE WHEN @Quantity IS NULL THEN 0
                           ELSE GREATEST(0, warning_balances.active_quantity - @Quantity) END,
    lifetime_quantity = GREATEST(0, warning_balances.lifetime_quantity),
    updated_at = now()
RETURNING active_quantity AS ActiveQuantity, lifetime_quantity AS LifetimeQuantity",
                new { GroupId = groupId, UserId = userId, WarningType = warningType, Quantity = quantity },
                transaction, cancellationToken: cancellationToken));
            var changed = quantity.HasValue ? quantity.Value : 0;
            await InsertEventAsync(connection, transaction, groupId, userId, warningType, operation,
                changed, balance.ActiveQuantity, balance.LifetimeQuantity, cancellationToken);
            await EnqueueWarningMirrorAsync(connection, transaction, groupId, userId,
                new[] { new WarningIssueResult(balance.ActiveQuantity, changed, balance.LifetimeQuantity) },
                new[] { warningType }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WarningIssueResult(balance.ActiveQuantity, changed, balance.LifetimeQuantity);
        }

        private static Task InsertEventAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, long groupId, long userId,
            string warningType, string operation, long quantity, long activeQuantity,
            long lifetimeQuantity, CancellationToken cancellationToken)
        {
            return connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_events
    (group_id, user_id, warning_type, operation, quantity,
     active_quantity, lifetime_quantity, created_at)
VALUES (@GroupId, @UserId, @WarningType, @Operation, @Quantity,
        @ActiveQuantity, @LifetimeQuantity, now())", new
            {
                GroupId = groupId,
                UserId = userId,
                WarningType = warningType,
                Operation = operation,
                Quantity = quantity,
                ActiveQuantity = activeQuantity,
                LifetimeQuantity = lifetimeQuantity
            }, transaction, cancellationToken: cancellationToken));
        }

        private static Task EnqueueWarningMirrorAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, long groupId, long userId,
            IReadOnlyList<WarningIssueResult> results, IReadOnlyList<string> warningTypes,
            CancellationToken cancellationToken)
        {
            if (!StorageModeProvider.RedisMirrorEnabled) return Task.CompletedTask;
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
    }
}
