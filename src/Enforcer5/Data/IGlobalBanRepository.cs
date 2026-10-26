using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>
    /// One global ban. <paramref name="Status"/> is <c>active</c> or <c>revoked</c>;
    /// <paramref name="ActorId"/> is the Telegram id of the global admin who made it (null for bans
    /// imported from Redis, which never recorded one). <paramref name="NotifiedAt"/> is when the
    /// support chat was told the account is there.
    /// </summary>
    internal sealed record GlobalBanRecord(long UserId, string Status, string Reason, long? ActorId,
        DateTimeOffset ImposedAt, DateTimeOffset? ResolvedAt, string ResolutionReason, DateTimeOffset? NotifiedAt);

    /// <summary>
    /// Global bans, in PostgreSQL (<c>global_sanctions</c>). Unlike the per-group histories this is
    /// what the bot runs on: there is no Redis copy. Reads for enforcement go through the in-memory
    /// set in <see cref="Helpers.GlobalBans"/>, not here, so no message waits on the database.
    /// </summary>
    internal interface IGlobalBanRepository
    {
        /// <summary>
        /// Bans <paramref name="userId"/> everywhere. An active ban on the same account is replaced
        /// (closed as <c>revoked</c>). Returns the new ban.
        /// </summary>
        Task<GlobalBanRecord> BanAsync(long userId, string reason, long? actorId, DateTimeOffset at);

        /// <summary>Lifts the active ban, with <paramref name="reason"/>. False when there was none.</summary>
        Task<bool> UnbanAsync(long userId, string reason, DateTimeOffset at);

        /// <summary>Every active ban.</summary>
        Task<IReadOnlyList<GlobalBanRecord>> GetActiveAsync();

        /// <summary>The account's most recent ban, active or not; null if it never had one.</summary>
        Task<GlobalBanRecord> GetLatestAsync(long userId);

        /// <summary>Records that the support chat was told about the active ban.</summary>
        Task MarkNotifiedAsync(long userId, DateTimeOffset at);
    }
}
