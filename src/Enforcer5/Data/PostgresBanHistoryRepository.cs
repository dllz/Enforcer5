using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// Ban history in the <c>sanctions</c> table. A ban is a row; ending it sets <c>status</c> and
    /// <c>resolved_at</c> on that row. Group and member rows are created on first use by
    /// <see cref="PrincipalResolver"/>, which is shared with every later PostgreSQL domain.
    /// </summary>
    internal sealed class PostgresBanHistoryRepository : IBanHistoryRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresBanHistoryRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        /// <summary>Supergroup ids are -100 followed by the channel id; other negative ids are basic groups.</summary>
        internal static string ChatType(long chatId) =>
            chatId <= -1_000_000_000_000 ? "supergroup" : chatId < 0 ? "group" : "unknown";

        internal static string KindValue(SanctionKind kind) => kind switch
        {
            SanctionKind.Ban => "ban",
            SanctionKind.Tempban => "tempban",
            SanctionKind.Kick => "kick",
            SanctionKind.Mute => "mute",
            SanctionKind.Tempmute => "tempmute",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        private static bool IsMute(SanctionKind kind) => kind is SanctionKind.Mute or SanctionKind.Tempmute;

        // Closes the member's open mute. A tempmute Telegram has already lifted ended at its expiry
        // (completed); anything else is lifted or replaced now (revoked). GREATEST keeps
        // resolved_at >= imposed_at (sanctions_period_check) if the two editions' clocks disagree.
        private const string CloseOpenMutes = @"
UPDATE sanctions s SET
    status = CASE WHEN s.expires_at IS NOT NULL AND s.expires_at <= @At THEN 'completed' ELSE 'revoked' END,
    resolved_at = CASE WHEN s.expires_at IS NOT NULL AND s.expires_at <= @At
                       THEN s.expires_at ELSE GREATEST(@At, s.imposed_at) END
WHERE s.group_id = @GroupId AND s.account_id = @AccountId
  AND s.kind IN ('mute', 'tempmute') AND s.status IN ('active', 'due')";

        internal static string StatusValue(BanEnd end) => end == BanEnd.Expired ? "completed" : "revoked";

        public async Task RecordBanAsync(BanEvent ban)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // The same lock as the observation batch, which also creates accounts and links.
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            // A ban made in the Telegram app reaches both editions when both are in the group.
            if (ban.Key != null && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM sanctions WHERE legacy_key = @Key)", new { ban.Key }, transaction)))
                return;
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, ban.ChatId,
                chatType: ChatType(ban.ChatId));
            var target = await PrincipalResolver.EnsureUserAsync(connection, transaction, ban.UserId);
            long? actorUserId = null;
            if (ban.ActorId.HasValue)
                actorUserId = (await PrincipalResolver.EnsureUserAsync(connection, transaction, ban.ActorId.Value)).UserId;

            if (IsMute(ban.Kind))
            {
                await connection.ExecuteAsync(new CommandDefinition(CloseOpenMutes,
                    new { group.GroupId, target.AccountId, ban.At }, transaction));
            }
            else
            {
                // A new ban replaces the open one; a kick lifts it. GREATEST keeps
                // resolved_at >= imposed_at if the two editions' clocks disagree by a little.
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions SET status = 'revoked', resolved_at = GREATEST(@At, imposed_at)
WHERE group_id = @GroupId AND account_id = @AccountId
  AND kind IN ('ban', 'tempban') AND status IN ('active', 'due')",
                    new { group.GroupId, target.AccountId, ban.At }, transaction));
            }

            // A kick is over as soon as it is made.
            var kick = ban.Kind == SanctionKind.Kick;
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO sanctions (group_id, account_id, user_id_at_time, actor_user_id, trigger_warning_event_id,
    report_id, kind, status, reason, imposed_at, expires_at, resolved_at, edition, legacy_key)
VALUES (@GroupId, @AccountId, @UserIdAtTime, @ActorUserId, @TriggerWarningEventId,
    @ReportId, @Kind, @Status, @Reason, @At, @ExpiresAt, @ResolvedAt, @Edition, @Key)",
                new
                {
                    group.GroupId, target.AccountId, UserIdAtTime = target.UserId, ActorUserId = actorUserId,
                    ban.TriggerWarningEventId, ban.ReportId, Kind = KindValue(ban.Kind),
                    Status = kick ? "completed" : "active", ban.Reason, ban.At,
                    ExpiresAt = kick ? null : ban.ExpiresAt, ResolvedAt = kick ? ban.At : (DateTimeOffset?)null,
                    ban.Edition, ban.Key
                }, transaction));
            await transaction.CommitAsync();
        }

        public async Task<bool> RecordUnbanAsync(UnbanEvent unban)
        {
            await using var connection = await Database.OpenConnectionAsync();
            // Plain lookups, not Ensure: an unban for a group or member the history has never seen
            // has no ban to close, and should not create rows.
            var closed = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE sanctions s SET status = @Status, resolved_at = GREATEST(@At, s.imposed_at)
FROM groups g, telegram_chats c, telegram_accounts a
WHERE s.group_id = g.group_id AND g.chat_id = c.chat_id AND c.telegram_chat_id = @ChatId
  AND s.account_id = a.account_id AND a.telegram_user_id = @UserId
  AND s.kind IN ('ban', 'tempban') AND s.status IN ('active', 'due')",
                new { Status = StatusValue(unban.End), unban.At, unban.ChatId, unban.UserId }));
            return closed > 0;
        }

        public async Task<bool> RecordUnmuteAsync(UnmuteEvent unmute)
        {
            await using var connection = await Database.OpenConnectionAsync();
            // Plain lookups, as for unbans: nothing to close for a group or member never seen.
            var ids = await connection.QuerySingleOrDefaultAsync<MemberIds>(new CommandDefinition(@"
SELECT g.group_id AS GroupId, a.account_id AS AccountId
FROM groups g
JOIN telegram_chats c ON c.chat_id = g.chat_id
CROSS JOIN telegram_accounts a
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId",
                new { unmute.ChatId, unmute.UserId }));
            if (ids == null) return false;
            var closed = await connection.ExecuteAsync(new CommandDefinition(CloseOpenMutes,
                new { ids.GroupId, ids.AccountId, unmute.At }));
            return closed > 0;
        }

        public Task<IReadOnlyList<BanRecord>> GetHistoryAsync(long chatId, long userId, int limit) =>
            QueryAsync(chatId, userId, limit, "AND s.kind IN ('ban', 'tempban')");

        public Task<IReadOnlyList<BanRecord>> GetSanctionsAsync(long chatId, long userId, int limit) =>
            QueryAsync(chatId, userId, limit, "");

        private async Task<IReadOnlyList<BanRecord>> QueryAsync(long chatId, long userId, int limit, string kindFilter)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(new CommandDefinition($@"
SELECT s.kind AS Kind, s.status AS Status, s.reason AS Reason, actor.telegram_user_id AS ActorId,
       s.imposed_at AS ImposedAt, s.expires_at AS ExpiresAt, s.resolved_at AS ResolvedAt,
       s.edition AS Edition
FROM sanctions s
JOIN groups g ON g.group_id = s.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = s.account_id
LEFT JOIN user_identity_links l ON l.user_id = s.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId
  {kindFilter}
ORDER BY s.imposed_at DESC, s.sanction_id DESC
LIMIT @Limit", new { ChatId = chatId, UserId = userId, Limit = limit }));
            return rows.Select(r => new BanRecord(r.Kind, r.Status, r.Reason, r.ActorId,
                ToUtc(r.ImposedAt), r.ExpiresAt.HasValue ? ToUtc(r.ExpiresAt.Value) : null,
                r.ResolvedAt.HasValue ? ToUtc(r.ResolvedAt.Value) : null, r.Edition)).ToList();
        }

        private static DateTimeOffset ToUtc(DateTime value) =>
            new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        private sealed class MemberIds
        {
            public long GroupId { get; set; }
            public long AccountId { get; set; }
        }

        private sealed class Row
        {
            public string Kind { get; set; }
            public string Status { get; set; }
            public string Reason { get; set; }
            public long? ActorId { get; set; }
            public DateTime ImposedAt { get; set; }
            public DateTime? ExpiresAt { get; set; }
            public DateTime? ResolvedAt { get; set; }
            public string Edition { get; set; }
        }
    }
}
