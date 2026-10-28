using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    internal sealed class PostgresDetectionLogRepository : IDetectionLogRepository
    {
        /// <summary>A decision sets the outcome of the member's detections from this long before it.</summary>
        internal static readonly TimeSpan DecisionLooksBack = TimeSpan.FromHours(24);

        /// <summary>The decisions that are an admin's own, as opposed to the bot's.</summary>
        internal static readonly string[] AdminSources = { "command", "button", "report", "app" };

        private readonly Func<PostgresDatabase> _database;

        internal PostgresDetectionLogRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private static DateTime Utc(DateTimeOffset at) => at.UtcDateTime;

        public async Task<int> WriteDetectionsAsync(IReadOnlyList<DetectionRecord> detections)
        {
            if (detections.Count == 0) return 0;
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // Accounts and groups the principal flush has not written yet are skipped, as raid capture does.
            var ids = (await connection.QueryAsync<long>(new CommandDefinition(@"
WITH input AS (
    SELECT * FROM unnest(@Chats, @Users, @Signs, @Phases, @Weights, @Before, @After, @Actions, @Deleted, @Reported,
        @Modes, @MessageIds, @MessageTypes, @TextHashes, @MediaIds, @Trust, @Messages, @Since, @Evidence, @Editions, @At)
    AS i(chat, usr, sign, phase, weight, score_before, score_after, action, deleted, reported, mode, message_id,
        message_type, text_hash, media_unique_id, trust_level, member_messages, member_since, evidence, edition, at)
)
INSERT INTO detections (group_id, account_id, sign, phase, weight, score_before, score_after, action, deleted, reported,
    mode, message_id, message_type, text_hash, media_unique_id, trust_level, member_messages, member_since, evidence,
    edition, detected_at, label_id)
SELECT g.group_id, a.account_id, i.sign, i.phase, i.weight, i.score_before, i.score_after, i.action, i.deleted, i.reported,
    i.mode, i.message_id, i.message_type, i.text_hash, i.media_unique_id, i.trust_level, i.member_messages, i.member_since,
    CAST(i.evidence AS jsonb), i.edition, i.at,
    -- A decision made before this was written (detections are written once a minute).
    (SELECT l.label_id FROM member_labels l
     WHERE l.group_id = g.group_id AND l.account_id = a.account_id AND l.label = 'bad'
       AND l.decided_at >= i.at AND l.decided_at < i.at + @LooksBack
     ORDER BY l.decided_at LIMIT 1)
FROM input i
JOIN telegram_chats c ON c.telegram_chat_id = i.chat
JOIN groups g ON g.chat_id = c.chat_id
LEFT JOIN telegram_accounts a ON a.telegram_user_id = i.usr
WHERE i.usr IS NULL OR a.account_id IS NOT NULL
RETURNING detection_id",
                new
                {
                    Chats = detections.Select(d => d.ChatId).ToArray(),
                    Users = detections.Select(d => d.UserId == 0 ? (long?)null : d.UserId).ToArray(),
                    Signs = detections.Select(d => d.Sign).ToArray(),
                    Phases = detections.Select(d => d.Phase).ToArray(),
                    Weights = detections.Select(d => d.Weight).ToArray(),
                    Before = detections.Select(d => d.ScoreBefore).ToArray(),
                    After = detections.Select(d => d.ScoreAfter).ToArray(),
                    Actions = detections.Select(d => d.Action).ToArray(),
                    Deleted = detections.Select(d => d.Deleted).ToArray(),
                    Reported = detections.Select(d => d.Reported).ToArray(),
                    Modes = detections.Select(d => d.Mode).ToArray(),
                    MessageIds = detections.Select(d => d.MessageId).ToArray(),
                    MessageTypes = detections.Select(d => d.MessageType).ToArray(),
                    TextHashes = detections.Select(d => d.TextHash).ToArray(),
                    MediaIds = detections.Select(d => d.MediaUniqueId).ToArray(),
                    Trust = detections.Select(d => d.TrustLevel).ToArray(),
                    Messages = detections.Select(d => d.MemberMessages).ToArray(),
                    Since = detections.Select(d => d.MemberSince?.UtcDateTime).ToArray(),
                    Evidence = detections.Select(d => d.Evidence).ToArray(),
                    Editions = detections.Select(d => d.Edition).ToArray(),
                    At = detections.Select(d => Utc(d.At)).ToArray(),
                    LooksBack = DecisionLooksBack
                }, transaction))).ToArray();

            if (ids.Length > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE detections d SET outcome = CASE WHEN l.undone_at IS NOT NULL THEN 'undone' ELSE l.action END,
    decided_at = l.decided_at, decided_by_account_id = l.decided_by_account_id
FROM member_labels l
WHERE d.label_id = l.label_id AND d.detection_id = ANY(@Ids);
UPDATE member_labels SET missed = FALSE
WHERE missed AND label_id IN (SELECT label_id FROM detections WHERE detection_id = ANY(@Ids) AND label_id IS NOT NULL);",
                    new { Ids = ids }, transaction));
            }
            await transaction.CommitAsync();
            return ids.Length;
        }

        public async Task<IReadOnlyList<MemberScores.Hit>> RecentHitsAsync(long chatId, long userId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(string Sign, int Weight, DateTime At)>(new CommandDefinition(@"
SELECT d.sign, d.weight, d.detected_at
FROM detections d
JOIN groups g ON g.group_id = d.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId AND d.detected_at >= @Since
  AND d.score_after > d.score_before
ORDER BY d.detected_at",
                new { ChatId = chatId, UserId = userId, Since = Utc(since) }));
            return rows.Select(r => new MemberScores.Hit(r.Sign, r.Weight, DateTime.SpecifyKind(r.At, DateTimeKind.Utc))).ToList();
        }

        public async Task<IReadOnlyList<(string Name, string Username)>> RecentMemberNamesAsync(long chatId, DateTimeOffset since, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(string Name, string Username)>(new CommandDefinition(@"
SELECT p.display_name, p.username
FROM group_members m
JOIN groups g ON g.group_id = m.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = m.account_id
JOIN telegram_account_profiles p ON p.account_id = m.account_id AND p.valid_to IS NULL
WHERE c.telegram_chat_id = @ChatId AND NOT a.is_bot
  AND GREATEST(m.last_message_at, m.joined_at, m.first_seen_at) >= @Since
ORDER BY GREATEST(m.last_message_at, m.joined_at, m.first_seen_at) DESC
LIMIT @Limit", new { ChatId = chatId, Since = Utc(since), Limit = limit }));
            return rows.ToList();
        }

        public async Task<(long Members, long Actions)> ObservedAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(@"
SELECT count(DISTINCT d.account_id) FILTER (WHERE d.reported),
       count(*) FILTER (WHERE d.deleted OR d.action IN ('warn', 'mute', 'tempmute', 'kick', 'tempban', 'ban'))
FROM detections d
JOIN groups g ON g.group_id = d.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND d.mode = 'observe'", new { ChatId = chatId }));
        }

        public async Task<IReadOnlyDictionary<string, (long Hits, long Removed)>> SignStatsAsync(long chatId, IReadOnlyList<string> signs)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(string Sign, long Hits, long Removed)>(new CommandDefinition(@"
SELECT d.sign, count(*), count(DISTINCT d.account_id) FILTER (WHERE d.outcome IN ('ban', 'kick', 'tempban'))
FROM detections d
JOIN groups g ON g.group_id = d.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND d.sign = ANY(@Signs)
GROUP BY d.sign", new { ChatId = chatId, Signs = signs.ToArray() }));
            return rows.ToDictionary(r => r.Sign, r => (r.Hits, r.Removed));
        }

        public async Task RecordLabelAsync(LabelEvent label)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            if (label.Key != null && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM member_labels WHERE label_key = @Key)", new { label.Key }, transaction)))
                return; // the other edition saw it first

            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, label.ChatId,
                chatType: PostgresBanHistoryRepository.ChatType(label.ChatId));
            var target = await PrincipalResolver.EnsureUserAsync(connection, transaction, label.UserId);
            long? actor = null;
            if (label.ActorId.HasValue)
                actor = (await PrincipalResolver.EnsureUserAsync(connection, transaction, label.ActorId.Value)).AccountId;
            var at = Utc(label.At);
            var adminDecision = AdminSources.Contains(label.Source);

            var labelId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO member_labels (group_id, account_id, label, action, source, decided_by_account_id, message_id, message_type,
    text_hash, media_unique_id, reason, missed, label_key, edition, decided_at)
VALUES (@GroupId, @AccountId, @Label, @Action, @Source, @Actor, @MessageId, @MessageType, @TextHash, @MediaUniqueId,
    @Reason,
    CASE WHEN @Label = 'bad' AND @AdminDecision THEN NOT EXISTS (
        SELECT 1 FROM detections d WHERE d.group_id = @GroupId AND d.account_id = @AccountId
          AND d.detected_at > @At - @LooksBack AND d.detected_at <= @At) END,
    @Key, @Edition, @At)
RETURNING label_id",
                new
                {
                    group.GroupId, target.AccountId, label.Label, label.Action, label.Source, Actor = actor,
                    label.MessageId, label.MessageType, label.TextHash, label.MediaUniqueId, label.Reason,
                    AdminDecision = adminDecision, label.Key, label.Edition, At = at, LooksBack = DecisionLooksBack
                }, transaction));

            if (label.Label is "bad" or "fine")
            {
                // The member's detections from the day before it that no decision had reached yet.
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE detections SET label_id = @LabelId, outcome = @Action, decided_at = @At, decided_by_account_id = @Actor
WHERE group_id = @GroupId AND account_id = @AccountId AND label_id IS NULL
  AND detected_at > @At - @LooksBack AND detected_at <= @At",
                    new { LabelId = labelId, label.Action, At = at, Actor = actor, group.GroupId, target.AccountId, LooksBack = DecisionLooksBack },
                    transaction));
            }
            else if (label.Label == "undo" && UndoneActions(label.Action) is { } undone)
            {
                // The latest decision this takes back, and the detections it had reached.
                await connection.ExecuteAsync(new CommandDefinition(@"
WITH taken_back AS (
    UPDATE member_labels SET undone_at = @At, undone_by_label_id = @LabelId
    WHERE label_id = (
        SELECT label_id FROM member_labels
        WHERE group_id = @GroupId AND account_id = @AccountId AND label = 'bad' AND undone_at IS NULL
          AND action = ANY(@Undone) AND decided_at <= @At
        ORDER BY decided_at DESC, label_id DESC LIMIT 1)
    RETURNING label_id)
UPDATE detections SET outcome = 'undone' WHERE label_id IN (SELECT label_id FROM taken_back)",
                    new { At = at, LabelId = labelId, group.GroupId, target.AccountId, Undone = undone }, transaction));
            }
            await transaction.CommitAsync();
        }

        public async Task<IReadOnlyList<(string Signs, DateTimeOffset At)>> FinesAsync(long chatId, long userId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(string Signs, DateTime At)>(new CommandDefinition(@"
SELECT l.reason, l.decided_at
FROM member_labels l
JOIN groups g ON g.group_id = l.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId AND l.action = 'fine' AND l.decided_at >= @Since
ORDER BY l.decided_at", new { ChatId = chatId, UserId = userId, Since = Utc(since) }));
            return rows.Select(r => (r.Signs, new DateTimeOffset(DateTime.SpecifyKind(r.At, DateTimeKind.Utc)))).ToList();
        }

        public async Task<IReadOnlyList<(string Sign, int Count)>> SignsSinceAsync(long chatId, long userId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(string Sign, long Count)>(new CommandDefinition(@"
SELECT d.sign, count(*)
FROM detections d
JOIN groups g ON g.group_id = d.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId AND d.detected_at >= @Since AND d.mode = 'live'
  AND (d.score_after > d.score_before OR d.reported)
GROUP BY d.sign ORDER BY min(d.detected_at)", new { ChatId = chatId, UserId = userId, Since = Utc(since) }));
            return rows.Select(r => (r.Sign, (int)r.Count)).ToList();
        }

        /// <summary>The decisions an undo takes back.</summary>
        internal static string[] UndoneActions(string undo) => undo switch
        {
            "unban" => new[] { "ban", "tempban", "kick" },
            "unmute" => new[] { "mute", "tempmute" },
            "unwarn" => new[] { "warn" },
            _ => null
        };
    }
}
