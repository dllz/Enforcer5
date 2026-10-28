using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>What was seen about a new member since the last flush (<c>member_signals</c>); null and 0 mean nothing new.</summary>
    internal sealed class SignalDelta
    {
        internal long ChatId;
        internal long UserId;
        internal int? AccountIdRange;
        internal bool? HasUsername;
        internal bool? IsPremium;
        internal string LanguageCode;
        internal string NameScript;
        internal int? NameLetters;
        internal bool? NameEmoji;
        internal bool? NameDigits;
        internal bool? NameStaffWord;
        internal bool? NameLookalikes;
        internal string[] ProfileFlags;
        internal DateTimeOffset? JoinedAt;
        internal string JoinSource;
        internal string InviteLinkName;
        internal bool? AddedByMember;
        internal bool JoinedDuringRaid;
        internal DateTimeOffset? FirstPostAt;
        internal string FirstPostType;
        internal int? FirstPostDelaySeconds;
        internal int Links;
        internal int Mentions;
        internal int Forwards;
        internal int ActiveHours;
        internal int MaxScore;
        internal DateTimeOffset FirstSeenAt;
        internal DateTimeOffset UpdatedAt;
    }

    /// <summary>A channel (by id) or a website (by host) a new member pointed at (<c>member_pointers</c>).</summary>
    internal sealed record PointerRecord(long ChatId, long UserId, string Kind, string Value, DateTimeOffset At);

    /// <summary>How the members who pointed at a channel or website turned out (<c>channel_reputation</c>, <c>domain_reputation</c>).</summary>
    internal sealed record ReputationRow(string Kind, string Value, int BadMembers, int FineMembers, int BadGroups, int Groups,
        DateTimeOffset LastSeenAt, DateTimeOffset ComputedAt);

    /// <summary>What the nightly retention made anonymous, by table.</summary>
    internal sealed record RetentionResult(int Detections, int Labels, int NearMisses, int Signals, int Pointers, int ProfileFlags);

    /// <summary>Detection's data (build step 14): member features, what members pointed at, reputation, implicit labels, retention.</summary>
    internal interface IDetectionDataRepository
    {
        Task WriteSignalsAsync(IReadOnlyList<SignalDelta> signals);

        Task WritePointersAsync(IReadOnlyList<PointerRecord> pointers);

        /// <summary>Counts every channel's and website's reputation again from the pointers and decisions; how many of each.</summary>
        Task<(int Channels, int Domains)> RecomputeReputationAsync(DateTimeOffset now);

        /// <summary>The channels and websites whose members turned out bad (<see cref="Detection.Reputation"/>).</summary>
        Task<IReadOnlyList<(string Kind, string Value)>> BadAsync(int minBad, int minGroups, int finePerBad);

        Task<ReputationRow> GetReputationAsync(string kind, string value);

        /// <summary>A bad label (source global) for each member a global ban came after, in every group Detection saw them in.</summary>
        Task<int> LabelGlobalBansAsync(DateTimeOffset since, string edition);

        /// <summary>New members first seen between the two times with no decision against them since, and no implicit label yet.</summary>
        Task<IReadOnlyList<(long ChatId, long UserId)>> UnlabelledNewcomersAsync(DateTimeOffset from, DateTimeOffset to, int limit);

        /// <summary>Removes the account from rows older than each group's retention (<c>detection_policies</c>, engine).</summary>
        Task<RetentionResult> ApplyRetentionAsync(DateTimeOffset now, int defaultDays, int profileFlagDays);
    }

    internal sealed class PostgresDetectionDataRepository : IDetectionDataRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresDetectionDataRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private static DateTime? Utc(DateTimeOffset? at) => at?.UtcDateTime;

        private const string Principals = @"
FROM telegram_chats c JOIN groups g ON g.chat_id = c.chat_id, telegram_accounts a
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId";

        public async Task WriteSignalsAsync(IReadOnlyList<SignalDelta> signals)
        {
            if (signals.Count == 0) return;
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var s in signals)
            {
                // Members and groups the principal flush has not written yet are skipped, as the detections are.
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO member_signals AS m (group_id, account_id, account_id_range, has_username, is_premium, language_code, name_script,
    name_letters, name_emoji, name_digits, name_staff_word, name_lookalikes, profile_flags, joined_at, join_source,
    invite_link_name, added_by_member, joined_during_raid, first_post_at, first_post_type, first_post_delay_seconds, links,
    mentions, forwards, active_hours, max_score, first_seen_at, updated_at)
SELECT g.group_id, a.account_id, @AccountIdRange, @HasUsername, @IsPremium, @LanguageCode, @NameScript, @NameLetters, @NameEmoji,
    @NameDigits, @NameStaffWord, @NameLookalikes, @ProfileFlags, @JoinedAt, @JoinSource, @InviteLinkName, @AddedByMember,
    @JoinedDuringRaid, @FirstPostAt, @FirstPostType, @FirstPostDelaySeconds, @Links, @Mentions, @Forwards, @ActiveHours,
    @MaxScore, @FirstSeenAt, @UpdatedAt" + Principals + @"
ON CONFLICT (group_id, account_id) WHERE account_id IS NOT NULL DO UPDATE SET
    account_id_range = COALESCE(m.account_id_range, excluded.account_id_range),
    has_username = COALESCE(excluded.has_username, m.has_username),
    is_premium = COALESCE(excluded.is_premium, m.is_premium),
    language_code = COALESCE(excluded.language_code, m.language_code),
    name_script = COALESCE(m.name_script, excluded.name_script),
    name_letters = COALESCE(m.name_letters, excluded.name_letters),
    name_emoji = COALESCE(m.name_emoji, excluded.name_emoji),
    name_digits = COALESCE(m.name_digits, excluded.name_digits),
    name_staff_word = COALESCE(m.name_staff_word, excluded.name_staff_word),
    name_lookalikes = COALESCE(m.name_lookalikes, excluded.name_lookalikes),
    profile_flags = COALESCE(excluded.profile_flags, m.profile_flags),
    joined_at = COALESCE(m.joined_at, excluded.joined_at),
    join_source = CASE WHEN m.join_source IS NULL OR m.join_source = 'unknown' THEN COALESCE(excluded.join_source, m.join_source)
                       ELSE m.join_source END,
    invite_link_name = COALESCE(m.invite_link_name, excluded.invite_link_name),
    added_by_member = COALESCE(m.added_by_member, excluded.added_by_member),
    joined_during_raid = m.joined_during_raid OR excluded.joined_during_raid,
    first_post_at = COALESCE(m.first_post_at, excluded.first_post_at),
    first_post_type = COALESCE(m.first_post_type, excluded.first_post_type),
    first_post_delay_seconds = COALESCE(m.first_post_delay_seconds, excluded.first_post_delay_seconds),
    links = m.links + excluded.links,
    mentions = m.mentions + excluded.mentions,
    forwards = m.forwards + excluded.forwards,
    active_hours = m.active_hours | excluded.active_hours,
    max_score = GREATEST(m.max_score, excluded.max_score),
    updated_at = GREATEST(m.updated_at, excluded.updated_at)",
                    new
                    {
                        s.ChatId, s.UserId, s.AccountIdRange, s.HasUsername, s.IsPremium, s.LanguageCode, s.NameScript, s.NameLetters,
                        s.NameEmoji, s.NameDigits, s.NameStaffWord, s.NameLookalikes, s.ProfileFlags, JoinedAt = Utc(s.JoinedAt),
                        s.JoinSource, s.InviteLinkName, s.AddedByMember, s.JoinedDuringRaid, FirstPostAt = Utc(s.FirstPostAt),
                        s.FirstPostType, s.FirstPostDelaySeconds, s.Links, s.Mentions, s.Forwards, s.ActiveHours, s.MaxScore,
                        FirstSeenAt = s.FirstSeenAt.UtcDateTime, UpdatedAt = s.UpdatedAt.UtcDateTime
                    }, transaction));
            }
            await transaction.CommitAsync();
        }

        public async Task WritePointersAsync(IReadOnlyList<PointerRecord> pointers)
        {
            if (pointers.Count == 0) return;
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO member_pointers AS p (group_id, account_id, kind, value, first_seen_at, last_seen_at)
SELECT g.group_id, a.account_id, i.kind, i.value, min(i.at), max(i.at)
FROM unnest(@Chats, @Users, @Kinds, @Values, @At) AS i(chat, usr, kind, value, at)
JOIN telegram_chats c ON c.telegram_chat_id = i.chat
JOIN groups g ON g.chat_id = c.chat_id
JOIN telegram_accounts a ON a.telegram_user_id = i.usr
GROUP BY g.group_id, a.account_id, i.kind, i.value
ON CONFLICT (group_id, account_id, kind, value) WHERE account_id IS NOT NULL
DO UPDATE SET last_seen_at = GREATEST(p.last_seen_at, excluded.last_seen_at)",
                new
                {
                    Chats = pointers.Select(p => p.ChatId).ToArray(),
                    Users = pointers.Select(p => p.UserId).ToArray(),
                    Kinds = pointers.Select(p => p.Kind).ToArray(),
                    Values = pointers.Select(p => p.Value).ToArray(),
                    At = pointers.Select(p => p.At.UtcDateTime).ToArray()
                });
        }

        public async Task<(int Channels, int Domains)> RecomputeReputationAsync(DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // Bad: removed or muted for something other than a group's own rules (a game's rules say nothing about a
            // channel), from the day before they pointed at it on, or globally banned. Fine: an admin found them fine, or
            // took an action back, or they became established (an implicit label).
            await connection.ExecuteAsync(new CommandDefinition(@"
CREATE TEMP TABLE judged ON COMMIT DROP AS
SELECT p.kind, p.value, p.group_id, p.account_id, p.last_seen_at,
    (EXISTS (SELECT 1 FROM member_labels l
             WHERE l.group_id = p.group_id AND l.account_id = p.account_id AND l.label = 'bad' AND l.undone_at IS NULL
               AND l.action IN ('ban', 'tempban', 'kick', 'mute', 'tempmute') AND l.category IS DISTINCT FROM 'group'
               AND l.decided_at >= p.first_seen_at - interval '1 day')
     OR EXISTS (SELECT 1 FROM global_sanctions s WHERE s.account_id = p.account_id AND s.status = 'active')) AS bad,
    EXISTS (SELECT 1 FROM member_labels l
            WHERE l.group_id = p.group_id AND l.account_id = p.account_id AND (l.action = 'fine' OR l.undone_at IS NOT NULL)) AS fine
FROM member_pointers p WHERE p.account_id IS NOT NULL;
DELETE FROM channel_reputation;
DELETE FROM domain_reputation;", transaction: transaction));
            const string Counts = @"count(DISTINCT account_id) FILTER (WHERE bad), count(DISTINCT account_id) FILTER (WHERE fine AND NOT bad),
    count(DISTINCT group_id) FILTER (WHERE bad), count(DISTINCT group_id), max(last_seen_at), @Now";
            var channels = await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO channel_reputation (channel_id, bad_members, fine_members, bad_groups, groups, last_seen_at, computed_at)
SELECT value::bigint, " + Counts + @" FROM judged WHERE kind = 'channel' AND value ~ '^-?[0-9]+$' GROUP BY value",
                new { Now = now.UtcDateTime }, transaction));
            var domains = await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO domain_reputation (domain, bad_members, fine_members, bad_groups, groups, last_seen_at, computed_at)
SELECT value, " + Counts + @" FROM judged WHERE kind = 'domain' GROUP BY value",
                new { Now = now.UtcDateTime }, transaction));
            await transaction.CommitAsync();
            return (channels, domains);
        }

        public async Task<IReadOnlyList<(string Kind, string Value)>> BadAsync(int minBad, int minGroups, int finePerBad)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(string, string)>(@"
SELECT 'channel', channel_id::text FROM channel_reputation
WHERE bad_members >= @MinBad AND bad_groups >= @MinGroups AND fine_members * @FinePerBad <= bad_members
UNION ALL
SELECT 'domain', domain FROM domain_reputation
WHERE bad_members >= @MinBad AND bad_groups >= @MinGroups AND fine_members * @FinePerBad <= bad_members",
                new { MinBad = minBad, MinGroups = minGroups, FinePerBad = finePerBad })).ToList();
        }

        private sealed class ReputationDbRow
        {
            public string Value { get; set; }
            public int BadMembers { get; set; }
            public int FineMembers { get; set; }
            public int BadGroups { get; set; }
            public int Groups { get; set; }
            public DateTime LastSeenAt { get; set; }
            public DateTime ComputedAt { get; set; }
        }

        public async Task<ReputationRow> GetReputationAsync(string kind, string value)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var sql = kind == "channel"
                ? "SELECT channel_id::text AS Value, bad_members AS BadMembers, fine_members AS FineMembers, bad_groups AS BadGroups, groups AS Groups, last_seen_at AS LastSeenAt, computed_at AS ComputedAt FROM channel_reputation WHERE channel_id = CAST(@Value AS bigint)"
                : "SELECT domain AS Value, bad_members AS BadMembers, fine_members AS FineMembers, bad_groups AS BadGroups, groups AS Groups, last_seen_at AS LastSeenAt, computed_at AS ComputedAt FROM domain_reputation WHERE domain = @Value";
            var row = await connection.QuerySingleOrDefaultAsync<ReputationDbRow>(sql, new { Value = value });
            static DateTimeOffset At(DateTime t) => new(DateTime.SpecifyKind(t, DateTimeKind.Utc));
            return row == null
                ? null
                : new ReputationRow(kind, row.Value, row.BadMembers, row.FineMembers, row.BadGroups, row.Groups, At(row.LastSeenAt), At(row.ComputedAt));
        }

        public async Task<int> LabelGlobalBansAsync(DateTimeOffset since, string edition)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync(@"
INSERT INTO member_labels (group_id, account_id, label, action, source, label_key, edition, decided_at, category)
SELECT DISTINCT ON (m.group_id, m.account_id, s.global_sanction_id)
       m.group_id, m.account_id, 'bad', 'ban', 'global',
       'global:' || m.group_id || ':' || m.account_id || ':' || s.global_sanction_id, @Edition, s.imposed_at, 'abuse'
FROM global_sanctions s
JOIN (SELECT group_id, account_id, detected_at AS seen_at FROM detections WHERE account_id IS NOT NULL
      UNION ALL
      SELECT group_id, account_id, first_seen_at FROM member_signals WHERE account_id IS NOT NULL) m
  ON m.account_id = s.account_id AND m.seen_at <= s.imposed_at AND m.seen_at > s.imposed_at - interval '30 days'
WHERE s.status = 'active' AND s.imposed_at >= @Since
ON CONFLICT (label_key) WHERE label_key IS NOT NULL DO NOTHING",
                new { Since = since.UtcDateTime, Edition = edition });
        }

        public async Task<IReadOnlyList<(long ChatId, long UserId)>> UnlabelledNewcomersAsync(DateTimeOffset from, DateTimeOffset to, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, long)>(@"
SELECT c.telegram_chat_id, a.telegram_user_id
FROM member_signals m
JOIN groups g ON g.group_id = m.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = m.account_id
WHERE m.first_seen_at >= @From AND m.first_seen_at < @To
  AND NOT EXISTS (SELECT 1 FROM member_labels l
                  WHERE l.group_id = m.group_id AND l.account_id = m.account_id
                    AND (l.source = 'established' OR (l.label = 'bad' AND l.undone_at IS NULL AND l.decided_at >= m.first_seen_at)))
  AND NOT EXISTS (SELECT 1 FROM global_sanctions s WHERE s.account_id = m.account_id AND s.status = 'active')
ORDER BY m.first_seen_at
LIMIT @Limit", new { From = from.UtcDateTime, To = to.UtcDateTime, Limit = limit })).ToList();
        }

        public async Task<RetentionResult> ApplyRetentionAsync(DateTimeOffset now, int defaultDays, int profileFlagDays)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition(@"
CREATE TEMP TABLE kept ON COMMIT DROP AS
SELECT g.group_id, @Now - make_interval(days => COALESCE(
    CASE WHEN (p.settings ->> 'retention_days') ~ '^[0-9]+$' THEN (p.settings ->> 'retention_days')::int END, @Default)) AS cut
FROM groups g LEFT JOIN detection_policies p ON p.group_id = g.group_id AND p.check_key = 'engine'",
                new { Now = now.UtcDateTime, Default = defaultDays }, transaction));
            async Task<int> Anonymise(string table, string at) => await connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE {table} x SET account_id = NULL FROM kept k WHERE x.group_id = k.group_id AND x.account_id IS NOT NULL AND x.{at} < k.cut",
                transaction: transaction));
            var detections = await Anonymise("detections", "detected_at");
            var labels = await Anonymise("member_labels", "decided_at");
            var near = await Anonymise("detection_near_misses", "detected_at");
            var signals = await Anonymise("member_signals", "updated_at");
            var pointers = await Anonymise("member_pointers", "last_seen_at");
            // Flags of a profile are looked up again when needed: kept no longer than the shortest choice.
            var flags = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM account_profile_flags WHERE checked_at < @Cut",
                new { Cut = (now - TimeSpan.FromDays(profileFlagDays)).UtcDateTime }, transaction));
            await transaction.CommitAsync();
            return new RetentionResult(detections, labels, near, signals, pointers, flags);
        }
    }
}
