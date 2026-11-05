using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection.Images;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>One image's scores (<c>media_scores</c>).</summary>
    internal sealed record MediaScoreRow(string MediaUniqueId, string Kind, string Model, NsfwScore Score, DateTimeOffset ScoredAt);

    /// <summary>One look at a member's profile images (<c>profile_scans</c>).</summary>
    /// <param name="ChatId">The group it was for; null for none.</param>
    /// <param name="Source">join, first_post or eval.</param>
    internal sealed record ProfileScanRow(long? ChatId, long UserId, string Source, string Model, int Photos, int ChannelPosts, int Scored,
        NsfwScore Max, bool Flagged, string FlaggedMedia, DateTimeOffset ScannedAt);

    /// <summary>The NSFW check's data (Daniel, 2026-11-05): images' scores, and the looks at members' profiles.</summary>
    internal interface IImageScoreRepository
    {
        /// <summary>The image's scores by these models; null when it was never scored by them.</summary>
        Task<NsfwScore> GetAsync(string mediaUniqueId, string model);

        Task SaveAsync(MediaScoreRow row);

        Task AddScanAsync(ProfileScanRow scan);

        /// <summary>
        /// For /nsfwscan: the members of the group labelled bad (the latest label not undone), and up to <paramref name="others"/>
        /// members it has seen post who never were, the most recent first. Telegram user ids.
        /// </summary>
        Task<(IReadOnlyList<long> Bad, IReadOnlyList<long> Others)> EvalMembersAsync(long chatId, int bad, int others);
    }

    internal sealed class PostgresImageScoreRepository : IImageScoreRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresImageScoreRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private sealed class ScoreRow
        {
            public float Nsfw { get; set; }
            public string Part { get; set; }
            public float PartScore { get; set; }
            public string Parts { get; set; }
        }

        public async Task<NsfwScore> GetAsync(string mediaUniqueId, string model)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<ScoreRow>(@"
SELECT nsfw AS Nsfw, part AS Part, part_score AS PartScore, parts::text AS Parts FROM media_scores
WHERE media_unique_id = @Id AND model = @Model",
                new { Id = mediaUniqueId, Model = model });
            if (row == null) return null;
            var parts = row.Parts != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(row.Parts)
                : row.Part != null ? new Dictionary<string, double> { [row.Part] = row.PartScore } : null;
            return new NsfwScore(row.Nsfw, parts);
        }

        public async Task SaveAsync(MediaScoreRow row)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO media_scores (media_unique_id, model, kind, nsfw, part, part_score, parts, scored_at)
VALUES (@Id, @Model, @Kind, @Nsfw, @Part, @PartScore, @Parts::jsonb, @At) ON CONFLICT DO NOTHING",
                new
                {
                    Id = row.MediaUniqueId, row.Model, row.Kind, Nsfw = (float)row.Score.Nsfw, row.Score.Part,
                    PartScore = (float)row.Score.PartScore, Parts = System.Text.Json.JsonSerializer.Serialize(row.Score.Parts),
                    At = row.ScoredAt.UtcDateTime
                });
        }

        public async Task AddScanAsync(ProfileScanRow scan)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var account = await PrincipalResolver.EnsureUserAsync(connection, transaction, scan.UserId);
            long? group = scan.ChatId is { } chatId
                ? (await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId, chatType: PostgresBanHistoryRepository.ChatType(chatId))).GroupId
                : null;
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO profile_scans (group_id, account_id, source, model, photos, channel_posts, scored, max_nsfw, max_part, max_part_score,
    flagged, flagged_media, scanned_at)
VALUES (@Group, @AccountId, @Source, @Model, @Photos, @ChannelPosts, @Scored, @Nsfw, @Part, @PartScore, @Flagged, @Media, @At)",
                new
                {
                    Group = group, account.AccountId, scan.Source, scan.Model, scan.Photos, scan.ChannelPosts, scan.Scored,
                    Nsfw = (float)scan.Max.Nsfw, scan.Max.Part, PartScore = (float)scan.Max.PartScore, scan.Flagged,
                    Media = scan.FlaggedMedia, At = scan.ScannedAt.UtcDateTime
                }, transaction));
            await transaction.CommitAsync();
        }

        public async Task<(IReadOnlyList<long> Bad, IReadOnlyList<long> Others)> EvalMembersAsync(long chatId, int bad, int others)
        {
            await using var connection = await Database.OpenConnectionAsync();
            const string group = "(SELECT g.group_id FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId)";
            var badIds = (await connection.QueryAsync<long>($@"
SELECT a.telegram_user_id FROM (
    SELECT DISTINCT ON (l.account_id) l.account_id, l.label, l.undone_at, l.decided_at
    FROM member_labels l WHERE l.group_id = {group} AND l.account_id IS NOT NULL
    ORDER BY l.account_id, l.decided_at DESC) last
JOIN telegram_accounts a ON a.account_id = last.account_id
WHERE last.label = 'bad' AND last.undone_at IS NULL
ORDER BY last.decided_at DESC LIMIT @Limit", new { ChatId = chatId, Limit = bad })).ToList();
            var otherIds = (await connection.QueryAsync<long>($@"
SELECT a.telegram_user_id FROM group_members m JOIN telegram_accounts a ON a.account_id = m.account_id
WHERE m.group_id = {group} AND m.message_count > 0 AND NOT a.is_bot
  AND NOT EXISTS (SELECT 1 FROM member_labels l WHERE l.group_id = m.group_id AND l.account_id = m.account_id AND l.label = 'bad')
ORDER BY m.first_seen_at DESC LIMIT @Limit", new { ChatId = chatId, Limit = others })).ToList();
            return (badIds, otherIds);
        }
    }
}
