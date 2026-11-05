using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>A message's text vector (<c>message_embeddings</c>). Never the text.</summary>
    /// <param name="Purpose">newcomer (a new member's message) or decision (the message a ban, kick or delete was about).</param>
    internal sealed record MessageEmbeddingRow(long ChatId, long UserId, long MessageId, string Model, string Purpose, float[] Vector,
        DateTimeOffset EmbeddedAt);

    /// <summary>
    /// A vector that got someone banned: what <c>similar_to_banned</c> compares new messages and images with.
    /// </summary>
    /// <param name="Kind">text or image.</param>
    /// <param name="ChatId">The group it was decided in.</param>
    /// <param name="Global">Spam, scams and the like (the decision's category is abuse): counts in every group. Otherwise only
    /// in <paramref name="ChatId"/>, so a group's own rules (a game's, a language) never make another group's talk look bad.</param>
    /// <param name="UserId">The member it was about, while their id is kept; null once anonymous.</param>
    /// <param name="Ref">For a text the embedding's id, for an image its file's unique id.</param>
    internal sealed record EmbeddingReference(string Kind, long ChatId, bool Global, long? UserId, string Ref, float[] Vector);

    /// <summary>What the nightly retention did to the vectors.</summary>
    internal sealed record EmbeddingRetention(int MessagesDeleted, int MessagesAnonymised, int MediaDeleted)
    {
        public override string ToString() =>
            $"{MessagesDeleted} message vectors deleted, {MessagesAnonymised} made anonymous, {MediaDeleted} image vectors deleted";
    }

    /// <summary>
    /// Text and image vectors (design section 12, build step 15): <c>message_embeddings</c> and <c>media_vectors</c>, pgvector
    /// <c>halfvec</c>. The tables exist only where the <c>vector</c> extension is enabled (migration Embeddings); without them
    /// every read is empty and every write is skipped (<see cref="AvailableAsync"/>).
    /// </summary>
    internal interface IEmbeddingRepository
    {
        /// <summary>Whether the tables are there (the database has pgvector).</summary>
        Task<bool> AvailableAsync();

        Task SaveMessageAsync(MessageEmbeddingRow row);

        /// <summary>The image's vector by <paramref name="model"/>; null when it was never embedded.</summary>
        Task<float[]> GetMediaAsync(string mediaUniqueId, string model);

        Task SaveMediaAsync(string mediaUniqueId, string model, string kind, float[] vector, DateTimeOffset at);

        /// <summary>
        /// The vectors behind decisions that got someone removed (ban, tempban, kick, delete, not undone) since
        /// <paramref name="since"/>, the most recent first, at most <paramref name="limit"/> of each kind; and the group's
        /// banned files. A bot's own decision counts only when it was about abuse.
        /// </summary>
        Task<IReadOnlyList<EmbeddingReference>> ReferencesAsync(string textModel, string imageModel, DateTimeOffset since, int limit);

        /// <summary>
        /// Nightly: message vectors past the group's retention are deleted, except those behind a decision, which lose the
        /// account id and are kept to <paramref name="referenceDays"/>; image vectors no decision or banned file points at
        /// are deleted after <paramref name="mediaDays"/>.
        /// </summary>
        Task<EmbeddingRetention> ApplyRetentionAsync(DateTimeOffset now, int defaultDays, int referenceDays, int mediaDays);
    }

    internal sealed class PostgresEmbeddingRepository : IEmbeddingRepository
    {
        /// <summary>The decisions whose message or file is a reference.</summary>
        internal const string RemovingActions = "('ban', 'tempban', 'kick', 'delete')";

        private readonly Func<PostgresDatabase> _database;

        internal PostgresEmbeddingRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        /// <summary>
        /// The schema pgvector is in, quoted, for its type: the tests' schemas are alone on the search path (as
        /// <see cref="PostgresRaidCaptureRepository"/> does for pg_trgm). Null when the extension is not there.
        /// </summary>
        private static async Task<string> VectorSchemaAsync(System.Data.Common.DbConnection connection) =>
            await connection.ExecuteScalarAsync<string>(@"
SELECT quote_ident(n.nspname) FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'vector'");

        public async Task<bool> AvailableAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<bool>(
                "SELECT to_regclass('message_embeddings') IS NOT NULL AND to_regclass('media_vectors') IS NOT NULL");
        }

        public async Task SaveMessageAsync(MessageEmbeddingRow row)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var schema = await VectorSchemaAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var account = await PrincipalResolver.EnsureUserAsync(connection, transaction, row.UserId);
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, row.ChatId,
                chatType: PostgresBanHistoryRepository.ChatType(row.ChatId));
            // A message is embedded once; a decision about one already embedded as a newcomer's makes it a decision's.
            await connection.ExecuteAsync(new CommandDefinition($@"
INSERT INTO message_embeddings (group_id, account_id, message_id, model, purpose, embedding, embedded_at)
VALUES (@GroupId, @AccountId, @MessageId, @Model, @Purpose, CAST(@Vector AS real[])::{schema}.halfvec, @At)
ON CONFLICT (group_id, message_id, model) DO UPDATE SET purpose = 'decision'
WHERE EXCLUDED.purpose = 'decision' AND message_embeddings.purpose <> 'decision'",
                new
                {
                    group.GroupId, account.AccountId, row.MessageId, row.Model, row.Purpose, row.Vector,
                    At = row.EmbeddedAt.UtcDateTime
                }, transaction));
            await transaction.CommitAsync();
        }

        public async Task<float[]> GetMediaAsync(string mediaUniqueId, string model)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<float[]>(
                "SELECT embedding::real[] FROM media_vectors WHERE media_unique_id = @Id AND model = @Model",
                new { Id = mediaUniqueId, Model = model });
        }

        public async Task SaveMediaAsync(string mediaUniqueId, string model, string kind, float[] vector, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var schema = await VectorSchemaAsync(connection);
            await connection.ExecuteAsync($@"
INSERT INTO media_vectors (media_unique_id, model, kind, embedding, embedded_at)
VALUES (@Id, @Model, @Kind, CAST(@Vector AS real[])::{schema}.halfvec, @At) ON CONFLICT DO NOTHING",
                new { Id = mediaUniqueId, Model = model, Kind = kind, Vector = vector, At = at.UtcDateTime });
        }

        private sealed class ReferenceRow
        {
            public long ChatId { get; set; }
            public bool Global { get; set; }
            public long? UserId { get; set; }
            public string Ref { get; set; }
            public float[] Vector { get; set; }
        }

        public async Task<IReadOnlyList<EmbeddingReference>> ReferencesAsync(string textModel, string imageModel, DateTimeOffset since,
            int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            // A bot's own decisions count only about abuse: its flood or length bans say nothing about the words.
            const string counts = "l.label = 'bad' AND l.undone_at IS NULL AND l.action IN " + RemovingActions +
                                  " AND (l.category = 'abuse' OR l.source <> 'auto') AND l.decided_at >= @Since";
            var texts = await connection.QueryAsync<ReferenceRow>($@"
SELECT chat_id AS ChatId, is_global AS Global, user_id AS UserId, ref AS Ref, vector AS Vector FROM (
    SELECT DISTINCT ON (e.embedding_id) c.telegram_chat_id AS chat_id, coalesce(l.category = 'abuse', false) AS is_global,
           a.telegram_user_id AS user_id, e.embedding_id::text AS ref, e.embedding::real[] AS vector, l.decided_at
    FROM message_embeddings e
    JOIN member_labels l ON l.group_id = e.group_id AND l.message_id = e.message_id
    JOIN groups g ON g.group_id = e.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
    LEFT JOIN telegram_accounts a ON a.account_id = e.account_id
    WHERE e.model = @TextModel AND {counts}
    ORDER BY e.embedding_id, coalesce(l.category = 'abuse', false) DESC, l.decided_at DESC) x
ORDER BY decided_at DESC LIMIT @Limit",
                new { TextModel = textModel, Since = since.UtcDateTime, Limit = limit });
            var images = await connection.QueryAsync<ReferenceRow>($@"
SELECT chat_id AS ChatId, is_global AS Global, NULL::bigint AS UserId, ref AS Ref, vector AS Vector FROM ((
    SELECT DISTINCT ON (v.media_unique_id, c.telegram_chat_id) c.telegram_chat_id AS chat_id, coalesce(l.category = 'abuse', false) AS is_global,
           v.media_unique_id AS ref, v.embedding::real[] AS vector, l.decided_at AS at
    FROM media_vectors v
    JOIN member_labels l ON l.media_unique_id = v.media_unique_id
    JOIN groups g ON g.group_id = l.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
    WHERE v.model = @ImageModel AND {counts}
    ORDER BY v.media_unique_id, c.telegram_chat_id, coalesce(l.category = 'abuse', false) DESC)
    UNION ALL (
    SELECT c.telegram_chat_id, false, v.media_unique_id, v.embedding::real[], b.added_at
    FROM media_vectors v
    JOIN banned_media b ON b.media_unique_id = v.media_unique_id
    JOIN groups g ON g.group_id = b.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
    WHERE v.model = @ImageModel)) x
ORDER BY at DESC LIMIT @Limit",
                new { ImageModel = imageModel, Since = since.UtcDateTime, Limit = limit });
            return texts.Select(r => new EmbeddingReference("text", r.ChatId, r.Global, r.UserId, r.Ref, r.Vector))
                .Concat(images.Select(r => new EmbeddingReference("image", r.ChatId, r.Global, null, r.Ref, r.Vector)))
                .ToList();
        }

        public async Task<EmbeddingRetention> ApplyRetentionAsync(DateTimeOffset now, int defaultDays, int referenceDays, int mediaDays)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition(@"
CREATE TEMP TABLE kept_vectors ON COMMIT DROP AS
SELECT g.group_id, @Now - make_interval(days => COALESCE(
    CASE WHEN (p.settings ->> 'retention_days') ~ '^[0-9]+$' THEN (p.settings ->> 'retention_days')::int END, @Default)) AS cut
FROM groups g LEFT JOIN detection_policies p ON p.group_id = g.group_id AND p.check_key = 'engine'",
                new { Now = now.UtcDateTime, Default = defaultDays }, transaction));
            const string referenced = "EXISTS (SELECT 1 FROM member_labels l WHERE l.group_id = e.group_id AND l.message_id = e.message_id " +
                                      "AND l.label = 'bad' AND l.undone_at IS NULL AND l.action IN " + RemovingActions + ")";
            var deleted = await connection.ExecuteAsync(new CommandDefinition($@"
DELETE FROM message_embeddings e USING kept_vectors k
WHERE e.group_id = k.group_id AND e.embedded_at < k.cut AND (NOT {referenced} OR e.embedded_at < @ReferenceCut)",
                new { ReferenceCut = (now - TimeSpan.FromDays(referenceDays)).UtcDateTime }, transaction));
            var anonymised = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE message_embeddings e SET account_id = NULL FROM kept_vectors k
WHERE e.group_id = k.group_id AND e.account_id IS NOT NULL AND e.embedded_at < k.cut", transaction: transaction));
            var media = await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM media_vectors v WHERE v.embedded_at < @Cut
  AND NOT EXISTS (SELECT 1 FROM banned_media b WHERE b.media_unique_id = v.media_unique_id)
  AND NOT EXISTS (SELECT 1 FROM member_labels l WHERE l.media_unique_id = v.media_unique_id AND l.label = 'bad'
                  AND l.undone_at IS NULL AND l.action IN " + RemovingActions + ")",
                new { Cut = (now - TimeSpan.FromDays(mediaDays)).UtcDateTime }, transaction));
            await transaction.CommitAsync();
            return new EmbeddingRetention(deleted, anonymised, media);
        }
    }
}
