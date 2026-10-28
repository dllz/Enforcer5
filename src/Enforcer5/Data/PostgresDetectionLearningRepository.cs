using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    internal sealed class PostgresDetectionLearningRepository : IDetectionLearningRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresDetectionLearningRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private static DateTime Utc(DateTimeOffset at) => at.UtcDateTime;
        private static DateTimeOffset Offset(DateTime at) => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
        private static DateTimeOffset? Offset(DateTime? at) => at.HasValue ? Offset(at.Value) : null;

        private const string Member = @"
JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = x.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId";

        private sealed class OverrideRow
        {
            public long ChatId { get; set; }
            public long UserId { get; set; }
            public long? SetBy { get; set; }
            public DateTime SetAt { get; set; }
            public long MessagesAtSet { get; set; }
            public DateTime? RestorePromptedAt { get; set; }
        }

        public async Task<IReadOnlyList<TrustOverride>> AllOverridesAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<OverrideRow>(@"
SELECT c.telegram_chat_id AS ChatId, a.telegram_user_id AS UserId, b.telegram_user_id AS SetBy, x.set_at AS SetAt,
       x.messages_at_set AS MessagesAtSet, x.restore_prompted_at AS RestorePromptedAt
FROM member_trust_overrides x
JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = x.account_id
LEFT JOIN telegram_accounts b ON b.account_id = x.set_by_account_id");
            return rows.Select(r => new TrustOverride(r.ChatId, r.UserId, r.SetBy, Offset(r.SetAt), r.MessagesAtSet, Offset(r.RestorePromptedAt)))
                .ToList();
        }

        public async Task<bool> SetOverrideAsync(long chatId, long userId, long setBy, long messagesAtSet, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId,
                chatType: PostgresBanHistoryRepository.ChatType(chatId));
            var account = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId);
            var by = await PrincipalResolver.EnsureUserAsync(connection, transaction, setBy);
            var added = await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO member_trust_overrides (group_id, account_id, set_by_account_id, set_at, messages_at_set)
VALUES (@GroupId, @AccountId, @By, @At, @Messages) ON CONFLICT DO NOTHING",
                new { group.GroupId, account.AccountId, By = by.AccountId, At = Utc(at), Messages = Math.Max(0, messagesAtSet) },
                transaction));
            await transaction.CommitAsync();
            return added == 1;
        }

        public async Task<bool> RemoveOverrideAsync(long chatId, long userId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync(@"
DELETE FROM member_trust_overrides x
USING groups g, telegram_chats c, telegram_accounts a
WHERE g.group_id = x.group_id AND c.chat_id = g.chat_id AND a.account_id = x.account_id
  AND c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId", new { ChatId = chatId, UserId = userId }) > 0;
        }

        public async Task MarkRestorePromptedAsync(long chatId, long userId, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE member_trust_overrides x SET restore_prompted_at = @At
FROM groups g, telegram_chats c, telegram_accounts a
WHERE g.group_id = x.group_id AND c.chat_id = g.chat_id AND a.account_id = x.account_id
  AND c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId", new { ChatId = chatId, UserId = userId, At = Utc(at) });
        }

        public async Task<bool> AbuseSinceAsync(long chatId, long userId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM detections x" + Member + @" AND x.detected_at >= @Since AND x.category = 'abuse')
    OR EXISTS (SELECT 1 FROM member_labels x" + Member + @" AND x.label = 'bad' AND x.decided_at >= @Since AND x.category = 'abuse')",
                new { ChatId = chatId, UserId = userId, Since = Utc(since) });
        }

        public async Task<IReadOnlyList<string>> AbuseSignsBetweenAsync(long chatId, long userId, DateTimeOffset from, DateTimeOffset to)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>(@"
SELECT x.sign FROM detections x" + Member + @"
  AND x.detected_at > @From AND x.detected_at <= @To AND x.category = 'abuse'
GROUP BY x.sign ORDER BY min(x.detected_at)",
                new { ChatId = chatId, UserId = userId, From = Utc(from), To = Utc(to) })).ToList();
        }

        public async Task<(int Abuse, int GroupRules)> RecordAsync(long chatId, long userId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.QuerySingleAsync<(int, int)>(@"
SELECT count(*) FILTER (WHERE x.category = 'abuse')::int, count(*) FILTER (WHERE x.category = 'group')::int
FROM member_labels x" + Member + @" AND x.label = 'bad' AND x.undone_at IS NULL AND x.decided_at >= @Since",
                new { ChatId = chatId, UserId = userId, Since = Utc(since) });
        }

        public async Task<long> AddAdjustmentAsync(long chatId, string kind, string reason, int? fromValue, int? fromDays, int? toValue,
            int? toDays, long? userId, long? labelId, bool applied, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId,
                chatType: PostgresBanHistoryRepository.ChatType(chatId));
            long? account = userId.HasValue ? (await PrincipalResolver.EnsureUserAsync(connection, transaction, userId.Value)).AccountId : null;
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO detection_adjustments (group_id, kind, reason, from_value, from_days, to_value, to_days, account_id, label_id,
    applied, edition, made_at)
VALUES (@GroupId, @Kind, @Reason, @FromValue, @FromDays, @ToValue, @ToDays, @AccountId, @LabelId, @Applied, @Edition, @At)
RETURNING adjustment_id",
                new
                {
                    group.GroupId, Kind = kind, Reason = reason, FromValue = fromValue, FromDays = fromDays, ToValue = toValue,
                    ToDays = toDays, AccountId = account, LabelId = labelId, Applied = applied, BanHistory.Edition, At = Utc(at)
                }, transaction));
            await transaction.CommitAsync();
            return id;
        }

        private sealed class AdjustmentRow
        {
            public long Id { get; set; }
            public long ChatId { get; set; }
            public string Kind { get; set; }
            public string Reason { get; set; }
            public int? FromValue { get; set; }
            public int? FromDays { get; set; }
            public int? ToValue { get; set; }
            public int? ToDays { get; set; }
            public long? UserId { get; set; }
            public bool Applied { get; set; }
            public DateTime? UndoneAt { get; set; }
            public DateTime MadeAt { get; set; }

            internal Adjustment ToAdjustment() => new Adjustment(Id, ChatId, Kind, Reason, FromValue, FromDays, ToValue, ToDays, UserId,
                Applied, Offset(UndoneAt), Offset(MadeAt));
        }

        private const string AdjustmentSelect = @"
SELECT x.adjustment_id AS Id, c.telegram_chat_id AS ChatId, x.kind AS Kind, x.reason AS Reason, x.from_value AS FromValue,
       x.from_days AS FromDays, x.to_value AS ToValue, x.to_days AS ToDays, a.telegram_user_id AS UserId, x.applied AS Applied,
       x.undone_at AS UndoneAt, x.made_at AS MadeAt
FROM detection_adjustments x
JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
LEFT JOIN telegram_accounts a ON a.account_id = x.account_id";

        public async Task<Adjustment> GetAdjustmentAsync(long id)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QuerySingleOrDefaultAsync<AdjustmentRow>(AdjustmentSelect + " WHERE x.adjustment_id = @Id", new { Id = id }))
                ?.ToAdjustment();
        }

        public async Task MarkUndoneAsync(long id, long by, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var actor = await PrincipalResolver.EnsureUserAsync(connection, transaction, by);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE detection_adjustments SET undone_at = @At, undone_by_account_id = @By
WHERE adjustment_id = @Id AND applied AND undone_at IS NULL", new { Id = id, By = actor.AccountId, At = Utc(at) }, transaction));
            await transaction.CommitAsync();
        }

        public async Task<IReadOnlyList<Adjustment>> RecentAdjustmentsAsync(long chatId, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<AdjustmentRow>(AdjustmentSelect + @"
WHERE c.telegram_chat_id = @ChatId ORDER BY x.made_at DESC, x.adjustment_id DESC LIMIT @Limit",
                new { ChatId = chatId, Limit = limit })).Select(r => r.ToAdjustment()).ToList();
        }

        public async Task<IReadOnlyList<ScoredDecision>> ScoredDecisionsAsync(long chatId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(int Score, bool Bad)>(@"
WITH decided AS (
    SELECT l.label_id, l.group_id, l.account_id, l.decided_at,
           l.label = 'bad' AND l.undone_at IS NULL AS bad
    FROM member_labels l
    JOIN groups g ON g.group_id = l.group_id
    JOIN telegram_chats c ON c.chat_id = g.chat_id
    WHERE c.telegram_chat_id = @ChatId AND l.decided_at >= @Since AND l.account_id IS NOT NULL
      AND ((l.label = 'bad' AND l.action IN ('ban', 'tempban', 'kick', 'mute', 'tempmute')
            AND l.source IN ('command', 'button', 'report', 'app', 'raid') AND l.category = 'abuse')
           OR l.action = 'fine')
)
SELECT max(d.score_after), x.bad
FROM decided x
JOIN detections d ON d.group_id = x.group_id AND d.account_id = x.account_id
 AND d.detected_at > x.decided_at - interval '24 hours' AND d.detected_at <= x.decided_at AND d.mode IN ('live', 'observe')
GROUP BY x.label_id, x.bad
HAVING max(d.score_after) > 0", new { ChatId = chatId, Since = Utc(since) });
            return rows.Select(r => new ScoredDecision(r.Score, r.Bad)).ToList();
        }

        public async Task<IReadOnlyList<long>> GroupsToLearnAsync(DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<long>(@"
SELECT c.telegram_chat_id FROM telegram_chats c JOIN groups g ON g.chat_id = c.chat_id
WHERE EXISTS (SELECT 1 FROM member_labels l WHERE l.group_id = g.group_id AND l.decided_at >= @Since)
   OR EXISTS (SELECT 1 FROM member_trust_overrides o WHERE o.group_id = g.group_id)
   OR EXISTS (SELECT 1 FROM detection_policies p WHERE p.group_id = g.group_id AND p.check_key = 'engine'
              AND (p.settings ->> 'trust_messages' IS NOT NULL OR p.settings ->> 'trust_days' IS NOT NULL))",
                new { Since = Utc(since) })).ToList();
        }
    }
}
