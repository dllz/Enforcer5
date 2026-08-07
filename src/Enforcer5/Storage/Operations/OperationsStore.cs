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
            if (!StorageModeProvider.PostgresIsPrimary)
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
            if (!StorageModeProvider.PostgresIsPrimary && !ShadowEnabled) return null;

            await using var database = PostgresDatabase.FromConfiguration();
            return await new AuditEventRepository(database).AppendAsync(auditEvent, cancellationToken);
        }

        internal static async Task<long?> RecordSanctionAsync(long groupId, long userId, string kind,
            string reason = null, DateTimeOffset? expiresAt = null, long? actorUserId = null,
            string legacyKey = null)
        {
            if (!StorageModeProvider.PostgresIsPrimary && !ShadowEnabled) return null;
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).UpsertAsync(new Sanction(0, groupId, userId,
                kind, SanctionStatuses.Active, CurrentEdition, actorUserId, reason,
                DateTimeOffset.UtcNow, expiresAt, null, null, null, legacyKey));
        }

        internal static async Task RecordSanctionResolvedAsync(long groupId, long userId, string kind)
        {
            if (!StorageModeProvider.PostgresIsPrimary && !ShadowEnabled) return;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Dapper.SqlMapper.ExecuteAsync(connection, @"
UPDATE sanctions SET status = 'revoked', resolved_at = now()
WHERE group_id = @GroupId AND user_id = @UserId AND kind = @Kind AND status IN ('active','due')",
                new { GroupId = groupId, UserId = userId, Kind = kind }, transaction);
            if (StorageModeProvider.RedisMirrorEnabled)
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
            if (!StorageModeProvider.PostgresIsPrimary) return;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE sanctions SET legacy_key = @LegacyKey
WHERE id = (SELECT id FROM sanctions WHERE group_id = @GroupId AND user_id = @UserId
  AND kind = 'mute' AND status = 'active' ORDER BY created_at DESC LIMIT 1)",
                new { GroupId = groupId, UserId = userId, LegacyKey = $"new-joiner:{groupId}:{userId}" });
        }

        internal static async Task SetGlobalBanAsync(long userId, bool active, string reason)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
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
            if (!StorageModeProvider.PostgresIsPrimary) return null;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            return await Dapper.SqlMapper.QuerySingleOrDefaultAsync<GlobalSanction>(connection, @"
SELECT user_id AS UserId, active AS Active, reason AS Reason, created_at AS CreatedAt,
       revocation_reason AS RevocationReason, revoked_at AS RevokedAt, legacy_key AS LegacyKey
FROM global_sanctions WHERE user_id = @UserId", new { UserId = userId });
        }

        internal static async Task<Sanction> GetActiveSanctionAsync(long groupId, long userId,
            string kind)
        {
            if (!StorageModeProvider.PostgresIsPrimary) return null;
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).GetActiveAsync(groupId, userId, kind);
        }

        internal static async Task SetActiveSanctionReasonAsync(long groupId, long userId,
            string reason)
        {
            if (!StorageModeProvider.PostgresIsPrimary) return;
            await using var database = PostgresDatabase.FromConfiguration();
            await using var connection = await database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE sanctions SET reason = @Reason
WHERE id = (SELECT id FROM sanctions WHERE group_id = @GroupId AND user_id = @UserId
  AND status IN ('active','due') AND kind IN ('ban','tempban')
  ORDER BY created_at DESC LIMIT 1)", new { GroupId = groupId, UserId = userId, Reason = reason });
        }

        internal static async Task<IReadOnlyList<Sanction>> ListActiveSanctionsAsync(string kind = null)
        {
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).ListActiveAsync(kind);
        }

        internal static async Task<IReadOnlyList<Sanction>> ListActiveSanctionsForGroupAsync(long groupId,
            string kind = null, bool newJoinersOnly = false)
        {
            if (!StorageModeProvider.PostgresIsPrimary) return Array.Empty<Sanction>();
            await using var database = PostgresDatabase.FromConfiguration();
            return await new SanctionRepository(database).ListActiveForGroupAsync(groupId, kind,
                newJoinersOnly);
        }

        internal static async Task<long> GetUserMessagesAsync(long userId)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                long.TryParse((await Redis.db.HashGetAsync($"chat:{userId}", "msgs")).ToString(), out var value);
                return value;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new StatisticsRepository(database).GetUserMessagesAsync(userId);
        }
        internal static async Task<long> GetGroupUserMessagesAsync(long groupId, long userId)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
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
