using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Newtonsoft.Json;

namespace Enforcer5.Data
{
    /// <summary>
    /// One stored value a recovery item sets: a hash field, a set member (<see cref="Before"/> and <see cref="After"/>
    /// "true" or "false") or a string key. <see cref="Before"/> is the value the recovery was worked out against, null
    /// for none; <see cref="After"/> null deletes it.
    /// </summary>
    internal sealed record RecoveryChange(string Type, string Key, string Field, string Before, string After);

    /// <summary>A log channel post a recovery item was read from, and when it was posted.</summary>
    internal sealed record RecoveryEvidence(DateTimeOffset At, long Log, long Post, string What);

    internal sealed record RecoveryItem(int Position, string Kind, long? MemberId, IReadOnlyList<RecoveryChange> Changes,
        IReadOnlyList<RecoveryEvidence> Evidence, bool Selected, string Outcome, string Result);

    internal sealed record RecoveryOffer(long OfferId, long ChatId, string Batch, DateTimeOffset PeriodFrom, DateTimeOffset PeriodTo,
        string Status, string Edition, DateTimeOffset ExpiresAt, long? DecidedBy, DateTimeOffset? DecidedAt,
        IReadOnlyList<ReportCopy> Copies, IReadOnlyList<RecoveryItem> Items);

    /// <summary>A group's recovered data as imported, before it is stored.</summary>
    internal sealed record NewRecoveryOffer(long ChatId, IReadOnlyList<RecoveryItem> Items);

    /// <summary>How an item ended once an offer was decided.</summary>
    internal sealed record RecoveryOutcome(int Position, string Outcome, string Result);

    /// <summary>The recovery offers (<c>recovery_offers</c>, <c>recovery_items</c>; Recovery/RecoveryOffers).</summary>
    internal interface IRecoveryRepository
    {
        /// <summary>Stores a batch, held. A group that already has an offer in the batch keeps it. Returns how many were new.</summary>
        Task<int> ImportAsync(string batch, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<NewRecoveryOffer> offers);

        /// <summary>Lets the batch's held offers go out. Returns how many.</summary>
        Task<int> ReleaseAsync(string batch, DateTimeOffset at);

        /// <summary>Released offers nobody sent yet.</summary>
        Task<IReadOnlyList<(long OfferId, long ChatId)>> DueAsync(int limit);

        /// <summary>Takes a released offer for this edition to send; false when the other edition took it.</summary>
        Task<bool> ClaimAsync(long offerId, string edition, DateTimeOffset at, DateTimeOffset expiresAt);

        /// <summary>A claimed offer nobody could be sent: released again, for the next try.</summary>
        Task UnclaimAsync(long offerId);

        Task AddCopiesAsync(long offerId, IReadOnlyList<ReportCopy> copies);

        Task<RecoveryOffer> GetAsync(long offerId);

        /// <summary>The newest offer of the group that is out and undecided, or null.</summary>
        Task<RecoveryOffer> OpenForAsync(long chatId);

        /// <summary>The group's newest offer whatever its status, or null: for a dev's preview.</summary>
        Task<RecoveryOffer> LatestForAsync(long chatId);

        /// <summary>Ticks or unticks an item of an offer still open; false when it is decided.</summary>
        Task<bool> SelectAsync(long offerId, int position, bool selected, DateTimeOffset now);

        /// <summary>
        /// The offer's one decision (applied or declined): the first stands. Returns the standing decision and who made it.
        /// Null when the offer is not out or has expired.
        /// </summary>
        Task<(string Decision, long? By)?> DecideAsync(long offerId, string decision, long actorId, DateTimeOffset at);

        Task SetOutcomesAsync(long offerId, IReadOnlyList<RecoveryOutcome> outcomes);

        /// <summary>Offers this edition sent that were not decided before they expired, marked expired.</summary>
        Task<IReadOnlyList<long>> ExpireAsync(string edition, DateTimeOffset now);

        /// <summary>Offers per batch and status, for the dev who imported them.</summary>
        Task<IReadOnlyList<(string Batch, string Status, int Offers, int Items)>> SummaryAsync();
    }

    internal sealed class PostgresRecoveryRepository : IRecoveryRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresRecoveryRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private static DateTime Utc(DateTimeOffset at) => at.UtcDateTime;
        private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

        /// <summary>How long an offer stays open once it is sent.</summary>
        internal static readonly TimeSpan Open = TimeSpan.FromDays(14);

        public async Task<int> ImportAsync(string batch, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<NewRecoveryOffer> offers)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var created = 0;
            foreach (var offer in offers)
            {
                var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, offer.ChatId,
                    chatType: PostgresBanHistoryRepository.ChatType(offer.ChatId));
                var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(@"
INSERT INTO recovery_offers (group_id, batch, period_from, period_to, expires_at)
VALUES (@GroupId, @Batch, @From, @To, @Expires)
ON CONFLICT (group_id, batch) DO NOTHING
RETURNING offer_id",
                    new { group.GroupId, Batch = batch, From = Utc(from), To = Utc(to), Expires = Utc(to) + Open }, transaction));
                if (id == null) continue;
                created++;
                foreach (var item in offer.Items)
                {
                    long? member = item.MemberId is { } user
                        ? (await PrincipalResolver.EnsureUserAsync(connection, transaction, user)).AccountId
                        : null;
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO recovery_items (offer_id, position, kind, account_id, changes, evidence)
VALUES (@Offer, @Position, @Kind, @Member, @Changes::jsonb, @Evidence::jsonb)",
                        new
                        {
                            Offer = id.Value, item.Position, item.Kind, Member = member,
                            Changes = JsonConvert.SerializeObject(item.Changes), Evidence = JsonConvert.SerializeObject(item.Evidence)
                        }, transaction));
                }
            }
            await transaction.CommitAsync();
            return created;
        }

        public async Task<int> ReleaseAsync(string batch, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync(
                "UPDATE recovery_offers SET status = 'released', released_at = @At WHERE batch = @Batch AND status = 'held'",
                new { Batch = batch, At = Utc(at) });
        }

        public async Task<IReadOnlyList<(long OfferId, long ChatId)>> DueAsync(int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(long, long)>(@"
SELECT o.offer_id, c.telegram_chat_id
FROM recovery_offers o JOIN groups g ON g.group_id = o.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE o.status = 'released' ORDER BY o.offer_id LIMIT @Limit", new { Limit = limit });
            return rows.ToList();
        }

        public async Task<bool> ClaimAsync(long offerId, string edition, DateTimeOffset at, DateTimeOffset expiresAt)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync(@"
UPDATE recovery_offers SET status = 'sent', edition = @Edition, sent_at = @At, expires_at = @Expires
WHERE offer_id = @Id AND status = 'released'",
                new { Id = offerId, Edition = edition, At = Utc(at), Expires = Utc(expiresAt) }) == 1;
        }

        public async Task UnclaimAsync(long offerId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE recovery_offers SET status = 'released', edition = NULL, sent_at = NULL
WHERE offer_id = @Id AND status = 'sent' AND copies = '[]'::jsonb", new { Id = offerId });
        }

        public async Task AddCopiesAsync(long offerId, IReadOnlyList<ReportCopy> copies)
        {
            if (copies.Count == 0) return;
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync("UPDATE recovery_offers SET copies = copies || @Copies::jsonb WHERE offer_id = @Id",
                new { Id = offerId, Copies = JsonConvert.SerializeObject(copies.Select(c => new CopyJson { Chat = c.ChatId, Message = c.MessageId })) });
        }

        private sealed class CopyJson
        {
            [JsonProperty("chat")] public long Chat { get; set; }
            [JsonProperty("message")] public long Message { get; set; }
        }

        private sealed class OfferRow
        {
            public long OfferId { get; set; }
            public long ChatId { get; set; }
            public string Batch { get; set; }
            public DateTime PeriodFrom { get; set; }
            public DateTime PeriodTo { get; set; }
            public string Status { get; set; }
            public string Edition { get; set; }
            public DateTime ExpiresAt { get; set; }
            public long? DecidedBy { get; set; }
            public DateTime? DecidedAt { get; set; }
            public string Copies { get; set; }
        }

        private sealed class ItemRow
        {
            public int Position { get; set; }
            public string Kind { get; set; }
            public long? MemberId { get; set; }
            public string Changes { get; set; }
            public string Evidence { get; set; }
            public bool Selected { get; set; }
            public string Outcome { get; set; }
            public string Result { get; set; }
        }

        private const string OfferSelect = @"
SELECT o.offer_id AS OfferId, c.telegram_chat_id AS ChatId, o.batch AS Batch, o.period_from AS PeriodFrom, o.period_to AS PeriodTo,
       o.status AS Status, o.edition AS Edition, o.expires_at AS ExpiresAt, d.telegram_user_id AS DecidedBy, o.decided_at AS DecidedAt,
       o.copies::text AS Copies
FROM recovery_offers o
JOIN groups g ON g.group_id = o.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
LEFT JOIN telegram_accounts d ON d.account_id = o.decided_by_account_id";

        public async Task<RecoveryOffer> GetAsync(long offerId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<OfferRow>(OfferSelect + " WHERE o.offer_id = @Id", new { Id = offerId });
            return row == null ? null : await WithItemsAsync(connection, row);
        }

        public async Task<RecoveryOffer> OpenForAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QueryFirstOrDefaultAsync<OfferRow>(OfferSelect +
                " WHERE c.telegram_chat_id = @ChatId AND o.status = 'sent' AND o.edition = @Edition ORDER BY o.offer_id DESC",
                new { ChatId = chatId, BanHistory.Edition });
            return row == null ? null : await WithItemsAsync(connection, row);
        }

        public async Task<RecoveryOffer> LatestForAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QueryFirstOrDefaultAsync<OfferRow>(OfferSelect +
                " WHERE c.telegram_chat_id = @ChatId ORDER BY o.offer_id DESC", new { ChatId = chatId });
            return row == null ? null : await WithItemsAsync(connection, row);
        }

        private static async Task<RecoveryOffer> WithItemsAsync(System.Data.Common.DbConnection connection, OfferRow row)
        {
            var items = await connection.QueryAsync<ItemRow>(@"
SELECT i.position AS Position, i.kind AS Kind, a.telegram_user_id AS MemberId, i.changes::text AS Changes, i.evidence::text AS Evidence,
       i.selected AS Selected, i.outcome AS Outcome, i.result::text AS Result
FROM recovery_items i LEFT JOIN telegram_accounts a ON a.account_id = i.account_id
WHERE i.offer_id = @Id ORDER BY i.position", new { Id = row.OfferId });
            var copies = JsonConvert.DeserializeObject<List<CopyJson>>(row.Copies ?? "[]") ?? new List<CopyJson>();
            return new RecoveryOffer(row.OfferId, row.ChatId, row.Batch, Utc(row.PeriodFrom), Utc(row.PeriodTo), row.Status, row.Edition,
                Utc(row.ExpiresAt), row.DecidedBy, row.DecidedAt is { } at ? Utc(at) : null,
                copies.Select(c => new ReportCopy(c.Chat, c.Message)).ToList(),
                items.Select(i => new RecoveryItem(i.Position, i.Kind, i.MemberId,
                    JsonConvert.DeserializeObject<List<RecoveryChange>>(i.Changes) ?? new List<RecoveryChange>(),
                    JsonConvert.DeserializeObject<List<RecoveryEvidence>>(i.Evidence ?? "[]") ?? new List<RecoveryEvidence>(),
                    i.Selected, i.Outcome, i.Result)).ToList());
        }

        public async Task<bool> SelectAsync(long offerId, int position, bool selected, DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync(@"
UPDATE recovery_items i SET selected = @Selected
FROM recovery_offers o
WHERE o.offer_id = i.offer_id AND i.offer_id = @Id AND i.position = @Position AND o.status = 'sent' AND o.expires_at > @Now",
                new { Id = offerId, Position = position, Selected = selected, Now = Utc(now) }) == 1;
        }

        public async Task<(string Decision, long? By)?> DecideAsync(long offerId, string decision, long actorId, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var actor = (await PrincipalResolver.EnsureUserAsync(connection, transaction, actorId)).AccountId;
            var current = await connection.QuerySingleOrDefaultAsync<(string Status, long? By, DateTime Expires)>(new CommandDefinition(@"
SELECT o.status, d.telegram_user_id, o.expires_at
FROM recovery_offers o LEFT JOIN telegram_accounts d ON d.account_id = o.decided_by_account_id
WHERE o.offer_id = @Id FOR UPDATE OF o", new { Id = offerId }, transaction));
            (string, long?)? standing;
            if (current.Status is "applied" or "declined")
                standing = (current.Status, current.By);
            else if (current.Status != "sent" || Utc(current.Expires) <= at)
                standing = null;
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE recovery_offers SET status = @Decision, decided_by_account_id = @Actor, decided_at = @At WHERE offer_id = @Id",
                    new { Id = offerId, Decision = decision, Actor = actor, At = Utc(at) }, transaction));
                standing = (decision, actorId);
            }
            await transaction.CommitAsync();
            return standing;
        }

        public async Task SetOutcomesAsync(long offerId, IReadOnlyList<RecoveryOutcome> outcomes)
        {
            if (outcomes.Count == 0) return;
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE recovery_items i SET outcome = o.outcome, result = o.result::jsonb
FROM unnest(@Positions, @Outcomes, @Results) AS o(position, outcome, result)
WHERE i.offer_id = @Id AND i.position = o.position",
                new
                {
                    Id = offerId, Positions = outcomes.Select(o => o.Position).ToArray(), Outcomes = outcomes.Select(o => o.Outcome).ToArray(),
                    Results = outcomes.Select(o => o.Result).ToArray()
                });
        }

        public async Task<IReadOnlyList<long>> ExpireAsync(string edition, DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var ids = (await connection.QueryAsync<long>(new CommandDefinition(@"
UPDATE recovery_offers SET status = 'expired' WHERE status = 'sent' AND edition = @Edition AND expires_at <= @Now RETURNING offer_id",
                new { Edition = edition, Now = Utc(now) }, transaction))).ToList();
            if (ids.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE recovery_items SET outcome = 'expired' WHERE offer_id = ANY(@Ids) AND outcome IS NULL", new { Ids = ids.ToArray() },
                    transaction));
            await transaction.CommitAsync();
            return ids;
        }

        public async Task<IReadOnlyList<(string Batch, string Status, int Offers, int Items)>> SummaryAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(string, string, int, int)>(@"
SELECT o.batch, o.status, count(DISTINCT o.offer_id)::int, count(i.position)::int
FROM recovery_offers o LEFT JOIN recovery_items i ON i.offer_id = o.offer_id
GROUP BY o.batch, o.status ORDER BY o.batch, o.status");
            return rows.ToList();
        }
    }
}
