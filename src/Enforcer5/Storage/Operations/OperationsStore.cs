using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Dapper;

namespace Enforcer5.Storage.Operations
{
    internal static class OperationsStore
    {
#if PREMIUM
        internal const string CurrentEdition = Editions.Premium;
#else
        internal const string CurrentEdition = Editions.Normal;
#endif

        internal static async Task RecordMessageAsync(long userId, long? groupId,
            DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        {
            // This spans both tiers: it increments the user's lifetime total and the per-group
            // total in one operation. The group decides, because the two must stay in the same
            // store - the invariant
            // "user total = SUM(per-group totals) + unattributed" only holds if they move together.
            // A message with no group is user-tier only, so it follows the global tier.
            var postgresIsPrimary = groupId.HasValue
                ? StorageRouting.GroupPostgresIsPrimary(groupId.Value)
                : StorageRouting.GlobalTierPostgresIsPrimary;

            if (!postgresIsPrimary)
            {
                await WriteRedisStatisticsAsync(userId, groupId, occurredAt);
                if (!ShadowEnabled) return;
            }

            await using var database = PostgresDatabase.FromConfiguration();
            await new StatisticsRepository(database).IncrementMessageAsync(
                CurrentEdition, userId, groupId, occurredAt, cancellationToken);

        }

        internal static async Task<long?> AppendAuditAsync(AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            if (auditEvent == null) throw new ArgumentNullException(nameof(auditEvent));
            // Audit rows are append-only history. A group-scoped event follows its group; an event
            // with no group is bot-level and follows the global tier.
            var postgresIsPrimary = auditEvent.GroupId.HasValue
                ? StorageRouting.GroupPostgresIsPrimary(auditEvent.GroupId.Value)
                : StorageRouting.GlobalTierPostgresIsPrimary;
            if (!postgresIsPrimary && !ShadowEnabled) return null;

            await using var database = PostgresDatabase.FromConfiguration();
            return await new AuditEventRepository(database).AppendAsync(auditEvent, cancellationToken);
        }

        internal static async Task<long?> RecordSanctionAsync(long groupId, long userId, string kind,
            string reason = null, DateTimeOffset? expiresAt = null, long? actorUserId = null,
            string legacyKey = null)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId) && !ShadowEnabled) return null;
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).UpsertAsync(new Sanction(0, groupId, userId,
                kind, SanctionStatuses.Active, CurrentEdition, actorUserId, reason,
                DateTimeOffset.UtcNow, expiresAt, null, null, null, legacyKey));
        }

        internal static async Task RecordSanctionResolvedAsync(long groupId, long userId, string kind)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId) && !ShadowEnabled) return;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var resolvedGroupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                groupId, DateTimeOffset.UtcNow);
            var account = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                userId, DateTimeOffset.UtcNow);
            await Dapper.SqlMapper.ExecuteAsync(connection, @"
UPDATE sanctions SET status = 'revoked', resolved_at = now()
WHERE group_id = @GroupId AND account_id = @AccountId AND kind = @Kind AND status IN ('active','due')",
                new { GroupId = resolvedGroupId, account.AccountId, Kind = kind }, transaction);
            if (StorageRouting.RedisMirrorEnabled)
                await CompatibilityOutbox.EnqueueAsync(connection, transaction, "sanctions",
                    $"sanction:{groupId}:{userId}", "resolve",
                    new { groupId, userId, kind });
            await transaction.CommitAsync();
        }

        internal static async Task RecordAllSanctionsResolvedAsync(long groupId, long userId,
            params string[] kinds)
        {
            foreach (var kind in kinds) await RecordSanctionResolvedAsync(groupId, userId, kind);
        }

        internal static async Task MarkMuteAsNewJoinerAsync(long groupId, long userId)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId)) return;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var resolvedGroupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                groupId, DateTimeOffset.UtcNow);
            var account = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                userId, DateTimeOffset.UtcNow);
            await connection.ExecuteAsync(@"
UPDATE sanctions SET legacy_key = @LegacyKey
WHERE sanction_id = (SELECT sanction_id FROM sanctions WHERE group_id = @GroupId AND account_id = @AccountId
  AND kind = 'mute' AND status = 'active' ORDER BY imposed_at DESC LIMIT 1)",
                new { GroupId = resolvedGroupId, account.AccountId, LegacyKey = $"new-joiner:{groupId}:{userId}" }, transaction);
            await transaction.CommitAsync();
        }

        internal static async Task SetGlobalBanAsync(long userId, bool active, string reason)
        {
            // Global tier: not group-scoped, migrated to completeness in Stage B.
            if (!StorageRouting.GlobalTierPostgresIsPrimary)
            {
                await Redis.db.HashSetAsync($"globalBan:{userId}", new[]
                {
                    new StackExchange.Redis.HashEntry("banned", active ? "1" : "0"),
                    new StackExchange.Redis.HashEntry(active ? "motivation" : "unbanMotivation", reason ?? string.Empty),
                    new StackExchange.Redis.HashEntry(active ? "time" : "unbanTime", DateTime.UtcNow.ToString("O"))
                });
                if (!ShadowEnabled) return;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            await new GlobalSanctionRepository(database).UpsertAsync(new GlobalSanction(userId, active,
                active ? reason : null, active ? DateTimeOffset.UtcNow : null,
                active ? null : reason, active ? null : DateTimeOffset.UtcNow, null));
        }

        internal static async Task<GlobalSanction> GetGlobalBanAsync(long userId)
        {
            // Global tier. Absence here means "not banned", so this must never read from a store
            // that has not been loaded to completeness.
            if (!StorageRouting.GlobalTierPostgresIsPrimary) return null;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            return await Dapper.SqlMapper.QuerySingleOrDefaultAsync<GlobalSanction>(connection, @"
SELECT ta.telegram_user_id AS UserId, gs.status = 'active' AS Active, gs.reason AS Reason,
       gs.imposed_at AS CreatedAt, CASE WHEN gs.status = 'revoked' THEN gs.reason END AS RevocationReason,
       gs.resolved_at AS RevokedAt, gs.legacy_key AS LegacyKey
FROM global_sanctions gs JOIN telegram_accounts ta ON ta.account_id = gs.account_id
WHERE ta.telegram_user_id = @UserId ORDER BY gs.imposed_at DESC LIMIT 1", new { UserId = userId });
        }

        internal static async Task<Sanction> GetActiveSanctionAsync(long groupId, long userId,
            string kind)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId)) return null;
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).GetActiveAsync(groupId, userId, kind);
        }

        internal static async Task SetActiveSanctionReasonAsync(long groupId, long userId,
            string reason)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId)) return;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var resolvedGroupId = await OperationsPrincipalResolver.ResolveGroupAsync(connection, transaction,
                groupId, DateTimeOffset.UtcNow);
            var account = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                userId, DateTimeOffset.UtcNow);
            await connection.ExecuteAsync(@"
UPDATE sanctions SET reason = @Reason
WHERE sanction_id = (SELECT sanction_id FROM sanctions WHERE group_id = @GroupId AND account_id = @AccountId
  AND status IN ('active','due') AND kind IN ('ban','tempban')
  ORDER BY imposed_at DESC LIMIT 1)", new { GroupId = resolvedGroupId, account.AccountId, Reason = reason }, transaction);
            await transaction.CommitAsync();
        }

        internal static async Task<IReadOnlyList<Sanction>> ListActiveSanctionsAsync(string kind = null)
        {
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).ListActiveAsync(kind);
        }

        internal static async Task<IReadOnlyList<Sanction>> ListActiveSanctionsForGroupAsync(long groupId,
            string kind = null, bool newJoinersOnly = false)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId)) return Array.Empty<Sanction>();
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).ListActiveForGroupAsync(groupId, kind,
                newJoinersOnly);
        }

        /// <summary>
        /// Records a moderation action against a user. User-tier: the count spans every group.
        /// </summary>
        internal static async Task RecordModerationCounterAsync(long userId, string kind,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(kind)) return;
            if (!StorageRouting.GlobalTierPostgresIsPrimary && !ShadowEnabled) return;

            await using var database = PostgresDatabase.FromConfiguration();
            await new AccountModerationCounterRepository(database)
                .IncrementAsync(userId, kind, 1, cancellationToken);
        }

        /// <summary>
        /// The user's moderation counters by kind, for /getuser.
        /// </summary>
        internal static async Task<IReadOnlyDictionary<string, long>> GetModerationCountersAsync(
            long userId, CancellationToken cancellationToken = default)
        {
            if (!StorageRouting.GlobalTierPostgresIsPrimary)
                return null;
            await using var database = PostgresDatabase.FromConfiguration();
            return await new AccountModerationCounterRepository(database)
                .GetAsync(userId, cancellationToken);
        }

        internal static async Task<long> GetUserMessagesAsync(long userId)
        {
            // The user's lifetime total spans every group, so it is user-tier, not group-tier.
            if (!StorageRouting.GlobalTierPostgresIsPrimary)
            {
                long.TryParse((await Redis.db.HashGetAsync($"chat:{userId}", "msgs")).ToString(), out var value);
                return value;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new StatisticsRepository(database).GetUserMessagesAsync(userId);
        }
        internal static async Task<long> GetGroupUserMessagesAsync(long groupId, long userId)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId))
            {
                long.TryParse((await Redis.db.HashGetAsync($"{groupId}:users:{userId}", "msgs")).ToString(), out var value);
                return value;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new StatisticsRepository(database).GetGroupUserMessagesAsync(groupId, userId);
        }

        private static async Task WriteRedisStatisticsAsync(long userId, long? groupId,
            DateTimeOffset occurredAt)
        {
            await Redis.db.HashIncrementAsync("bot:general", "messages");
            if (groupId == null) return;

            await Task.WhenAll(
                Redis.db.HashIncrementAsync($"chat:{userId}", "msgs"),
                Redis.db.HashIncrementAsync($"{groupId}:users:{userId}", "msgs"),
                Redis.db.HashSetAsync($"chat:{groupId}:userlast", userId,
                    occurredAt.LocalDateTime.Ticks),
                Redis.db.StringSetAsync($"chat:{groupId}:chatlast",
                    occurredAt.LocalDateTime.Ticks));
        }

        private static bool ShadowEnabled =>
            string.Equals(RegHelper.GetRegValue("OperationsShadowEnabled"), "true",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection"));
    }
}
