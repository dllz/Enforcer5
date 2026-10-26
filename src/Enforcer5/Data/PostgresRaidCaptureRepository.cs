using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;
using Npgsql;

namespace Enforcer5.Data
{
    /// <summary>A group's normal, as recomputed nightly (see <see cref="GroupStatsEntity"/>).</summary>
    internal sealed record GroupNorms(long TelegramChatId, int EstablishedMessages, int EstablishedDays, double JoinsPerHour, int ActiveMembers);

    /// <summary>Someone who looks like the seeds, and why.</summary>
    internal sealed record SimilarMember(long TelegramUserId, IReadOnlyList<string> Reasons, int Score);

    /// <summary>
    /// The raid building blocks in PostgreSQL: who is in which group and how they came in, what their
    /// messages were like, each group's normal, and the lookup that finds everyone resembling a few
    /// known raiders.
    /// </summary>
    internal interface IRaidCaptureRepository
    {
        Task ApplyAsync(CaptureBatch batch, DateTimeOffset now);

        /// <summary>Recomputes every group's normal from the last 30 days.</summary>
        Task RecomputeNormsAsync(DateTimeOffset now);

        Task<IReadOnlyList<GroupNorms>> GetNormsAsync();

        /// <summary>Drops fingerprints older than 30 days and joins older than 180.</summary>
        Task CleanUpAsync(DateTimeOffset now);

        /// <summary>
        /// Members of <paramref name="telegramChatId"/> active or joined since <paramref name="since"/>
        /// that resemble <paramref name="seedTelegramUserIds"/>, most alike first: same invite link or
        /// adder, joined close to them, silent before joining, an account id close to theirs, same
        /// language, same script, same text or image, a similar name.
        /// </summary>
        Task<IReadOnlyList<SimilarMember>> FindSimilarAsync(long telegramChatId, IReadOnlyList<long> seedTelegramUserIds,
            DateTimeOffset since, int limit = 200);
    }

    internal sealed class PostgresRaidCaptureRepository : IRaidCaptureRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresRaidCaptureRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        public async Task ApplyAsync(CaptureBatch batch, DateTimeOffset now)
        {
            if (batch.IsEmpty) return;
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var at = now.UtcDateTime;
            if (batch.Members.Count > 0) await ApplyMembersAsync(connection, transaction, batch.Members, at);
            if (batch.Joins.Count > 0) await ApplyJoinsAsync(connection, transaction, batch.Joins);
            if (batch.Leaves.Count > 0) await ApplyLeavesAsync(connection, transaction, batch.Leaves);
            if (batch.Fingerprints.Count > 0) await ApplyFingerprintsAsync(connection, transaction, batch.Fingerprints);
            await transaction.CommitAsync();
        }

        // Rows whose account or group is not in PostgreSQL yet are skipped: the principal batch,
        // written just before, creates both for everything the bot saw.
        private const string Resolve = @"
JOIN telegram_chats c ON c.telegram_chat_id = o.chat_id
JOIN groups g ON g.chat_id = c.chat_id
JOIN telegram_accounts a ON a.telegram_user_id = o.user_id";

        private static async Task ApplyMembersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            IReadOnlyList<MemberActivity> members, DateTime now)
        {
            await connection.ExecuteAsync(@"
CREATE TEMP TABLE obs_members (chat_id bigint, user_id bigint, messages bigint, first_at timestamptz, last_at timestamptz,
    redis_count bigint) ON COMMIT DROP;
INSERT INTO obs_members SELECT * FROM unnest(@Chats, @Users, @Messages, @First, @Last, @Redis);

UPDATE group_members m SET
    message_count = m.message_count + o.messages,
    first_message_at = COALESCE(m.first_message_at, o.first_at),
    last_message_at = GREATEST(m.last_message_at, o.last_at)
FROM obs_members o " + Resolve + @"
WHERE m.group_id = g.group_id AND m.account_id = a.account_id;

-- A member written for the first time starts from their Redis count, so existing members are not new.
INSERT INTO group_members (group_id, account_id, first_seen_at, message_count, first_message_at, last_message_at)
SELECT g.group_id, a.account_id, o.first_at, GREATEST(o.messages, o.redis_count), o.first_at, o.last_at
FROM obs_members o " + Resolve + @"
ON CONFLICT (group_id, account_id) DO NOTHING;",
                new
                {
                    Chats = members.Select(m => m.ChatId).ToArray(),
                    Users = members.Select(m => m.UserId).ToArray(),
                    Messages = members.Select(m => m.Messages).ToArray(),
                    First = members.Select(m => m.FirstMessageAt.UtcDateTime).ToArray(),
                    Last = members.Select(m => m.LastMessageAt.UtcDateTime).ToArray(),
                    Redis = members.Select(m => m.RedisCount).ToArray()
                }, transaction);
        }

        private static async Task ApplyJoinsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<JoinEvent> joins)
        {
            await connection.ExecuteAsync(@"
CREATE TEMP TABLE obs_joins (chat_id bigint, user_id bigint, joined_at timestamptz, source text, invite_link text,
    invite_link_name text, added_by bigint) ON COMMIT DROP;
INSERT INTO obs_joins SELECT * FROM unnest(@Chats, @Users, @At, @Sources, @Links, @Names, @AddedBy);

INSERT INTO group_joins (group_id, account_id, joined_at, source, invite_link, invite_link_name, added_by_account_id)
SELECT g.group_id, a.account_id, o.joined_at, o.source, o.invite_link, o.invite_link_name, adder.account_id
FROM obs_joins o " + Resolve + @"
LEFT JOIN telegram_accounts adder ON adder.telegram_user_id = o.added_by;

INSERT INTO group_members AS m (group_id, account_id, first_seen_at, joined_at, join_source, invite_link_name, added_by_account_id)
SELECT DISTINCT ON (g.group_id, a.account_id) g.group_id, a.account_id, o.joined_at, o.joined_at, o.source,
       o.invite_link_name, adder.account_id
FROM obs_joins o " + Resolve + @"
LEFT JOIN telegram_accounts adder ON adder.telegram_user_id = o.added_by
ORDER BY g.group_id, a.account_id, o.joined_at DESC
ON CONFLICT (group_id, account_id) DO UPDATE SET
    joined_at = EXCLUDED.joined_at, join_source = EXCLUDED.join_source,
    invite_link_name = EXCLUDED.invite_link_name, added_by_account_id = EXCLUDED.added_by_account_id, left_at = NULL
WHERE m.joined_at IS NULL OR EXCLUDED.joined_at >= m.joined_at;",
                new
                {
                    Chats = joins.Select(j => j.ChatId).ToArray(),
                    Users = joins.Select(j => j.UserId).ToArray(),
                    At = joins.Select(j => j.At.UtcDateTime).ToArray(),
                    Sources = joins.Select(j => j.Source).ToArray(),
                    Links = joins.Select(j => j.InviteLink).ToArray(),
                    Names = joins.Select(j => j.InviteLinkName).ToArray(),
                    AddedBy = joins.Select(j => j.AddedBy).ToArray()
                }, transaction);
        }

        private static async Task ApplyLeavesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<LeaveEvent> leaves)
        {
            await connection.ExecuteAsync(@"
CREATE TEMP TABLE obs_leaves (chat_id bigint, user_id bigint, left_at timestamptz) ON COMMIT DROP;
INSERT INTO obs_leaves SELECT * FROM unnest(@Chats, @Users, @At);
UPDATE group_members m SET left_at = o.left_at
FROM obs_leaves o " + Resolve + @"
WHERE m.group_id = g.group_id AND m.account_id = a.account_id AND (m.joined_at IS NULL OR o.left_at >= m.joined_at);",
                new
                {
                    Chats = leaves.Select(l => l.ChatId).ToArray(),
                    Users = leaves.Select(l => l.UserId).ToArray(),
                    At = leaves.Select(l => l.At.UtcDateTime).ToArray()
                }, transaction);
        }

        private static async Task ApplyFingerprintsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            IReadOnlyList<FingerprintEvent> prints)
        {
            await connection.ExecuteAsync(@"
CREATE TEMP TABLE obs_prints (chat_id bigint, user_id bigint, message_id bigint, sent_at timestamptz, content_type text,
    text_hash text, media_unique_id text, script text, link_count int, bare_link boolean, sample_reason text) ON COMMIT DROP;
INSERT INTO obs_prints SELECT * FROM unnest(@Chats, @Users, @Ids, @At, @Types, @Hashes, @Media, @Scripts, @Links, @Bare, @Reasons);
INSERT INTO message_fingerprints (group_id, account_id, telegram_message_id, sent_at, content_type, text_hash,
    media_unique_id, script, link_count, bare_link, sample_reason)
SELECT g.group_id, a.account_id, o.message_id, o.sent_at, o.content_type, o.text_hash, o.media_unique_id, o.script,
       o.link_count, o.bare_link, o.sample_reason
FROM obs_prints o " + Resolve + ";",
                new
                {
                    Chats = prints.Select(p => p.ChatId).ToArray(),
                    Users = prints.Select(p => p.UserId).ToArray(),
                    Ids = prints.Select(p => p.MessageId).ToArray(),
                    At = prints.Select(p => p.At.UtcDateTime).ToArray(),
                    Types = prints.Select(p => p.Print.ContentType).ToArray(),
                    Hashes = prints.Select(p => p.Print.TextHash).ToArray(),
                    Media = prints.Select(p => p.Print.MediaUniqueId).ToArray(),
                    Scripts = prints.Select(p => p.Print.Script).ToArray(),
                    Links = prints.Select(p => p.Print.LinkCount).ToArray(),
                    Bare = prints.Select(p => p.Print.BareLink).ToArray(),
                    Reasons = prints.Select(p => p.Reason).ToArray()
                }, transaction);
        }

        public async Task RecomputeNormsAsync(DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            // An established member is one who has been there and talked as much as the group's
            // usual active member: up to 1000 messages in a busy group, at least 20 in a quiet one;
            // 3 to 30 days. Too few active members to say: 20 messages and 7 days.
            await connection.ExecuteAsync(@"
WITH active AS (
    SELECT group_id, message_count, EXTRACT(epoch FROM (@Now - first_seen_at)) / 86400.0 AS days
    FROM group_members
    WHERE last_message_at > @Now - interval '30 days' AND left_at IS NULL
), agg AS (
    SELECT group_id, count(*) AS n,
           percentile_cont(0.5) WITHIN GROUP (ORDER BY message_count) AS median_messages,
           percentile_cont(0.5) WITHIN GROUP (ORDER BY days) AS median_days
    FROM active GROUP BY group_id
), joins AS (
    SELECT group_id, count(*) / (30 * 24.0) AS per_hour
    FROM group_joins WHERE joined_at > @Now - interval '30 days' GROUP BY group_id
)
INSERT INTO group_stats (group_id, computed_at, active_members, median_messages, median_tenure_days,
    established_messages, established_days, joins_per_hour)
SELECT g.group_id, @Now, COALESCE(agg.n, 0), COALESCE(round(agg.median_messages), 0)::int, COALESCE(agg.median_days, 0),
       CASE WHEN COALESCE(agg.n, 0) < 10 THEN 20 ELSE LEAST(1000, GREATEST(20, round(agg.median_messages)))::int END,
       CASE WHEN COALESCE(agg.n, 0) < 10 THEN 7 ELSE LEAST(30, GREATEST(3, round(agg.median_days)))::int END,
       COALESCE(joins.per_hour, 0)
FROM groups g
LEFT JOIN agg ON agg.group_id = g.group_id
LEFT JOIN joins ON joins.group_id = g.group_id
WHERE agg.group_id IS NOT NULL OR joins.group_id IS NOT NULL
ON CONFLICT (group_id) DO UPDATE SET
    computed_at = EXCLUDED.computed_at, active_members = EXCLUDED.active_members,
    median_messages = EXCLUDED.median_messages, median_tenure_days = EXCLUDED.median_tenure_days,
    established_messages = EXCLUDED.established_messages, established_days = EXCLUDED.established_days,
    joins_per_hour = EXCLUDED.joins_per_hour;", new { Now = now.UtcDateTime });
        }

        public async Task<IReadOnlyList<GroupNorms>> GetNormsAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<GroupNorms>(@"
SELECT c.telegram_chat_id AS TelegramChatId, s.established_messages AS EstablishedMessages,
       s.established_days AS EstablishedDays, s.joins_per_hour AS JoinsPerHour, s.active_members AS ActiveMembers
FROM group_stats s JOIN groups g USING (group_id) JOIN telegram_chats c ON c.chat_id = g.chat_id")).ToList();
        }

        public async Task CleanUpAsync(DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
DELETE FROM message_fingerprints WHERE sent_at < @Now - interval '30 days';
DELETE FROM group_joins WHERE joined_at < @Now - interval '180 days';", new { Now = now.UtcDateTime }, commandTimeout: 300);
        }

        public async Task<IReadOnlyList<SimilarMember>> FindSimilarAsync(long telegramChatId, IReadOnlyList<long> seedTelegramUserIds,
            DateTimeOffset since, int limit = 200)
        {
            await using var connection = await Database.OpenConnectionAsync();
            // Names: pg_trgm similarity when the extension is installed, else the same first five
            // letters. Qualified with the extension's schema, which need not be on the search path.
            var trigram = await connection.ExecuteScalarAsync<string>(@"
SELECT quote_ident(n.nspname) FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_trgm'");
            var nameMatch = trigram != null
                ? $"{trigram}.similarity(lower(cp.display_name), lower(sp.display_name)) > 0.5"
                : "left(lower(cp.display_name), 5) = left(lower(sp.display_name), 5)";
            var rows = await connection.QueryAsync<(long TelegramUserId, string Reason)>($@"
WITH grp AS (
    SELECT g.group_id FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @Chat
), seeds AS (
    SELECT a.account_id, a.telegram_user_id FROM telegram_accounts a WHERE a.telegram_user_id = ANY(@Seeds)
), seed_members AS (
    SELECT m.* FROM group_members m JOIN grp USING (group_id) JOIN seeds s USING (account_id)
), seed_profiles AS (
    SELECT p.* FROM telegram_account_profiles p JOIN seeds s USING (account_id) WHERE p.valid_to IS NULL
), seed_prints AS (
    SELECT f.* FROM message_fingerprints f JOIN grp USING (group_id) JOIN seeds s USING (account_id)
    WHERE f.sent_at >= @Since - interval '1 day'
), candidates AS (
    SELECT m.*, a.telegram_user_id FROM group_members m JOIN grp USING (group_id)
    JOIN telegram_accounts a ON a.account_id = m.account_id
    WHERE m.account_id NOT IN (SELECT account_id FROM seeds)
      AND (m.joined_at >= @Since OR m.last_message_at >= @Since OR m.first_seen_at >= @Since)
)
SELECT c.telegram_user_id, 'same_link' FROM candidates c
WHERE c.invite_link_name IS NOT NULL AND c.invite_link_name IN (SELECT invite_link_name FROM seed_members WHERE invite_link_name IS NOT NULL)
   OR EXISTS (SELECT 1 FROM group_joins cj JOIN group_joins sj ON sj.invite_link = cj.invite_link AND sj.group_id = cj.group_id
              WHERE cj.account_id = c.account_id AND cj.group_id = c.group_id AND cj.invite_link IS NOT NULL
                AND sj.account_id IN (SELECT account_id FROM seeds) AND cj.joined_at >= @Since)
UNION ALL
SELECT c.telegram_user_id, 'same_adder' FROM candidates c
WHERE c.added_by_account_id IS NOT NULL AND c.added_by_account_id IN (SELECT added_by_account_id FROM seed_members)
UNION ALL
SELECT c.telegram_user_id, 'joined_close' FROM candidates c
WHERE c.joined_at IS NOT NULL AND EXISTS (SELECT 1 FROM seed_members s WHERE s.joined_at IS NOT NULL
      AND abs(EXTRACT(epoch FROM c.joined_at - s.joined_at)) <= 600)
UNION ALL
SELECT c.telegram_user_id, 'silent_before_join' FROM candidates c
WHERE c.joined_at IS NOT NULL AND (c.first_message_at IS NULL OR c.first_message_at >= c.joined_at)
  AND c.first_seen_at >= c.joined_at - interval '1 minute'
UNION ALL
SELECT c.telegram_user_id, 'account_id_close' FROM candidates c
WHERE c.telegram_user_id >= (SELECT min(telegram_user_id) FROM seeds) * 0.995
UNION ALL
SELECT c.telegram_user_id, 'same_language' FROM candidates c
JOIN telegram_account_profiles cp ON cp.account_id = c.account_id AND cp.valid_to IS NULL
WHERE cp.language_code IS NOT NULL AND cp.language_code IN (SELECT language_code FROM seed_profiles WHERE language_code IS NOT NULL)
UNION ALL
SELECT DISTINCT c.telegram_user_id, 'same_script' FROM candidates c
JOIN message_fingerprints f ON f.group_id = c.group_id AND f.account_id = c.account_id AND f.sent_at >= @Since
WHERE f.script IS NOT NULL AND f.script IN (SELECT script FROM seed_prints WHERE script IS NOT NULL)
UNION ALL
SELECT DISTINCT c.telegram_user_id, 'same_text' FROM candidates c
JOIN message_fingerprints f ON f.group_id = c.group_id AND f.account_id = c.account_id AND f.sent_at >= @Since
WHERE f.text_hash IN (SELECT text_hash FROM seed_prints WHERE text_hash IS NOT NULL)
UNION ALL
SELECT DISTINCT c.telegram_user_id, 'same_media' FROM candidates c
JOIN message_fingerprints f ON f.group_id = c.group_id AND f.account_id = c.account_id AND f.sent_at >= @Since
WHERE f.media_unique_id IN (SELECT media_unique_id FROM seed_prints WHERE media_unique_id IS NOT NULL)
UNION ALL
SELECT DISTINCT c.telegram_user_id, 'bare_link' FROM candidates c
JOIN message_fingerprints f ON f.group_id = c.group_id AND f.account_id = c.account_id AND f.sent_at >= @Since
WHERE f.bare_link AND EXISTS (SELECT 1 FROM seed_prints WHERE bare_link)
UNION ALL
SELECT DISTINCT c.telegram_user_id, 'similar_name' FROM candidates c
JOIN telegram_account_profiles cp ON cp.account_id = c.account_id AND cp.valid_to IS NULL
JOIN seed_profiles sp ON {nameMatch}
WHERE cp.display_name IS NOT NULL AND sp.display_name IS NOT NULL",
                new { Chat = telegramChatId, Seeds = seedTelegramUserIds.ToArray(), Since = since.UtcDateTime });

            // The strongest signs count three: the same text, image or link a raider used.
            static int Weight(string reason) => reason is "same_text" or "same_media" or "same_link" or "same_adder" ? 3 : 1;
            return rows.GroupBy(r => r.TelegramUserId)
                .Select(g =>
                {
                    var reasons = g.Select(r => r.Reason).Distinct().OrderBy(r => r).ToList();
                    return new SimilarMember(g.Key, reasons, reasons.Sum(Weight));
                })
                .OrderByDescending(m => m.Score).ThenBy(m => m.TelegramUserId)
                .Take(limit)
                .ToList();
        }
    }
}
