using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>A custom emoji as looked up from Telegram (<c>custom_emoji</c>): its pack, file and format, and its score once scored.</summary>
    internal sealed record CustomEmojiRow(string CustomEmojiId, string SetName, string FileUniqueId, string Format, float? Nsfw, string Model);

    /// <summary>A custom emoji pack found NSFW (<c>nsfw_emoji_sets</c>); <see cref="Cleared"/> when a dev said it is not.</summary>
    internal sealed record NsfwEmojiSet(string SetName, string CustomEmojiId, float? Nsfw, string Source, DateTimeOffset FlaggedAt, bool Cleared);

    /// <summary>
    /// Custom emoji and the emoji packs found NSFW, shared by every group and both editions (Daniel, 2026-11-09;
    /// <see cref="Enforcer5.Detection.CustomEmojis"/>, <see cref="Enforcer5.Detection.NsfwEmojiSets"/>).
    /// </summary>
    internal interface ICustomEmojiRepository
    {
        Task<IReadOnlyList<CustomEmojiRow>> GetAsync(IReadOnlyCollection<string> ids);

        /// <summary>Stores what Telegram said; a score is kept unless the file changed.</summary>
        Task SaveAsync(IReadOnlyCollection<CustomEmojiRow> rows);

        Task SetScoreAsync(string customEmojiId, float nsfw, string model);

        Task<IReadOnlyList<NsfwEmojiSet>> NsfwSetsAsync();

        /// <summary>Marks the pack NSFW. False when it was marked already (a dev's clear stays: <paramref name="source"/> auto never undoes it).</summary>
        Task<bool> MarkNsfwAsync(string setName, string customEmojiId, float? nsfw, string model, string source);

        /// <summary>A dev says the pack is not NSFW. False when it was not marked.</summary>
        Task<bool> ClearAsync(string setName, long clearedBy);
    }

    internal sealed class PostgresCustomEmojiRepository : ICustomEmojiRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresCustomEmojiRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private sealed class EmojiRow
        {
            public string CustomEmojiId { get; set; }
            public string SetName { get; set; }
            public string FileUniqueId { get; set; }
            public string Format { get; set; }
            public float? Nsfw { get; set; }
            public string Model { get; set; }
        }

        private sealed class SetRow
        {
            public string SetName { get; set; }
            public string CustomEmojiId { get; set; }
            public float? Nsfw { get; set; }
            public string Source { get; set; }
            public DateTime FlaggedAt { get; set; }
            public bool Cleared { get; set; }
        }

        public async Task<IReadOnlyList<CustomEmojiRow>> GetAsync(IReadOnlyCollection<string> ids)
        {
            if (ids == null || ids.Count == 0) return Array.Empty<CustomEmojiRow>();
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<EmojiRow>(@"
SELECT custom_emoji_id AS CustomEmojiId, set_name AS SetName, file_unique_id AS FileUniqueId, format AS Format, nsfw AS Nsfw, model AS Model
FROM custom_emoji WHERE custom_emoji_id = ANY(@Ids)", new { Ids = ids.ToArray() });
            return rows.Select(r => new CustomEmojiRow(r.CustomEmojiId, r.SetName, r.FileUniqueId, r.Format, r.Nsfw, r.Model)).ToList();
        }

        public async Task SaveAsync(IReadOnlyCollection<CustomEmojiRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO custom_emoji (custom_emoji_id, set_name, file_unique_id, format, seen_at)
SELECT DISTINCT ON (n.id) n.id, n.set_name, n.file, n.format, now() FROM unnest(@Ids, @Sets, @Files, @Formats) AS n(id, set_name, file, format)
ON CONFLICT (custom_emoji_id) DO UPDATE SET set_name = EXCLUDED.set_name, format = EXCLUDED.format, seen_at = EXCLUDED.seen_at,
    file_unique_id = EXCLUDED.file_unique_id,
    nsfw = CASE WHEN custom_emoji.file_unique_id = EXCLUDED.file_unique_id THEN custom_emoji.nsfw END,
    model = CASE WHEN custom_emoji.file_unique_id = EXCLUDED.file_unique_id THEN custom_emoji.model END",
                new
                {
                    Ids = rows.Select(r => r.CustomEmojiId).ToArray(),
                    Sets = rows.Select(r => r.SetName).ToArray(),
                    Files = rows.Select(r => r.FileUniqueId).ToArray(),
                    Formats = rows.Select(r => r.Format).ToArray()
                });
        }

        public async Task SetScoreAsync(string customEmojiId, float nsfw, string model)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync("UPDATE custom_emoji SET nsfw = @Nsfw, model = @Model WHERE custom_emoji_id = @Id",
                new { Id = customEmojiId, Nsfw = nsfw, Model = model });
        }

        public async Task<IReadOnlyList<NsfwEmojiSet>> NsfwSetsAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<SetRow>(@"
SELECT set_name AS SetName, custom_emoji_id AS CustomEmojiId, nsfw AS Nsfw, source AS Source, flagged_at AS FlaggedAt,
       cleared_at IS NOT NULL AS Cleared
FROM nsfw_emoji_sets");
            return rows.Select(r => new NsfwEmojiSet(r.SetName, r.CustomEmojiId, r.Nsfw, r.Source,
                new DateTimeOffset(DateTime.SpecifyKind(r.FlaggedAt, DateTimeKind.Utc)), r.Cleared)).ToList();
        }

        public async Task<bool> MarkNsfwAsync(string setName, string customEmojiId, float? nsfw, string model, string source)
        {
            await using var connection = await Database.OpenConnectionAsync();
            // A dev's mark undoes a dev's clear; the bot's own never does.
            return await connection.ExecuteAsync(@"
INSERT INTO nsfw_emoji_sets (set_name, custom_emoji_id, nsfw, model, source, flagged_at)
VALUES (@Set, @Id, @Nsfw, @Model, @Source, now())
ON CONFLICT (set_name) DO UPDATE SET source = EXCLUDED.source, flagged_at = EXCLUDED.flagged_at, cleared_at = NULL, cleared_by_account_id = NULL
WHERE EXCLUDED.source = 'dev' AND nsfw_emoji_sets.cleared_at IS NOT NULL",
                new { Set = setName, Id = customEmojiId, Nsfw = nsfw, Model = model, Source = source }) == 1;
        }

        public async Task<bool> ClearAsync(string setName, long clearedBy)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var by = (await PrincipalResolver.EnsureUserAsync(connection, transaction, clearedBy)).AccountId;
            var cleared = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE nsfw_emoji_sets SET cleared_at = now(), cleared_by_account_id = @By WHERE set_name = @Set AND cleared_at IS NULL",
                new { Set = setName, By = by }, transaction));
            await transaction.CommitAsync();
            return cleared == 1;
        }
    }
}
