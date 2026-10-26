using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// Global bans in <c>global_sanctions</c>: a row per ban, at most one <c>active</c> per account
    /// (<c>global_sanctions_active_idx</c>). Accounts and users are created on first use by
    /// <see cref="PrincipalResolver"/>, under the same lock as every other writer of them.
    /// </summary>
    internal sealed class PostgresGlobalBanRepository : IGlobalBanRepository
    {
        internal const string Superseded = "Replaced by a new global ban";

        private readonly Func<PostgresDatabase> _database;

        internal PostgresGlobalBanRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private const string SelectRecord = @"
SELECT a.telegram_user_id AS UserId, gs.status AS Status, gs.reason AS Reason, actor.telegram_user_id AS ActorId,
       gs.imposed_at AS ImposedAt, gs.resolved_at AS ResolvedAt, gs.resolution_reason AS ResolutionReason,
       gs.notified_at AS NotifiedAt
FROM global_sanctions gs
JOIN telegram_accounts a ON a.account_id = gs.account_id
LEFT JOIN user_identity_links l ON l.user_id = gs.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id";

        public async Task<GlobalBanRecord> BanAsync(long userId, string reason, long? actorId, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // The same lock as the observation batch and the histories, which also create accounts.
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var target = await PrincipalResolver.EnsureUserAsync(connection, transaction, userId);
            long? actorUserId = null;
            if (actorId.HasValue)
                actorUserId = (await PrincipalResolver.EnsureUserAsync(connection, transaction, actorId.Value)).UserId;

            // GREATEST keeps resolved_at >= imposed_at (global_sanctions_period_check) if the two
            // editions' clocks disagree by a little.
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE global_sanctions SET status = 'revoked', resolved_at = GREATEST(@At, imposed_at),
    resolution_reason = @Superseded
WHERE account_id = @AccountId AND status = 'active'",
                new { target.AccountId, At = at, Superseded }, transaction));
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO global_sanctions (account_id, user_id_at_time, actor_user_id, kind, status, reason, imposed_at)
VALUES (@AccountId, @UserIdAtTime, @ActorUserId, 'ban', 'active', @Reason, @At)",
                new { target.AccountId, UserIdAtTime = target.UserId, ActorUserId = actorUserId, Reason = reason, At = at },
                transaction));
            await transaction.CommitAsync();
            return new GlobalBanRecord(userId, "active", reason, actorId, at, null, null, null);
        }

        public async Task<bool> UnbanAsync(long userId, string reason, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            // A plain lookup: an account never seen has no ban to lift, and gets no rows.
            var closed = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE global_sanctions gs SET status = 'revoked', resolved_at = GREATEST(@At, gs.imposed_at),
    resolution_reason = @Reason
FROM telegram_accounts a
WHERE gs.account_id = a.account_id AND a.telegram_user_id = @UserId AND gs.status = 'active'",
                new { UserId = userId, Reason = reason, At = at }));
            return closed > 0;
        }

        public async Task<IReadOnlyList<GlobalBanRecord>> GetActiveAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(new CommandDefinition(
                SelectRecord + " WHERE gs.status = 'active' ORDER BY gs.imposed_at DESC, gs.global_sanction_id DESC"));
            return rows.Select(r => r.ToRecord()).ToList();
        }

        public async Task<GlobalBanRecord> GetLatestAsync(long userId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(SelectRecord + @"
WHERE a.telegram_user_id = @UserId
ORDER BY gs.imposed_at DESC, gs.global_sanction_id DESC
LIMIT 1", new { UserId = userId }));
            return row?.ToRecord();
        }

        public async Task MarkNotifiedAsync(long userId, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE global_sanctions gs SET notified_at = @At
FROM telegram_accounts a
WHERE gs.account_id = a.account_id AND a.telegram_user_id = @UserId AND gs.status = 'active'",
                new { UserId = userId, At = at }));
        }

        private static DateTimeOffset ToUtc(DateTime value) =>
            new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        private sealed class Row
        {
            public long UserId { get; set; }
            public string Status { get; set; }
            public string Reason { get; set; }
            public long? ActorId { get; set; }
            public DateTime ImposedAt { get; set; }
            public DateTime? ResolvedAt { get; set; }
            public string ResolutionReason { get; set; }
            public DateTime? NotifiedAt { get; set; }

            public GlobalBanRecord ToRecord() => new GlobalBanRecord(UserId, Status, Reason, ActorId,
                ToUtc(ImposedAt), ResolvedAt.HasValue ? ToUtc(ResolvedAt.Value) : null, ResolutionReason,
                NotifiedAt.HasValue ? ToUtc(NotifiedAt.Value) : null);
        }
    }
}
