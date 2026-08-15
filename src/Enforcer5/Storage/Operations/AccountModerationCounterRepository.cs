using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Enforcer5.Storage.Operations
{
    /// <summary>
    /// Lifetime moderation-action counts per user, per kind, across every group.
    /// <para>
    /// The Redis original <c>ban:{userId}</c> is a plain hash of counters incremented on the message
    /// hot path. It is user-tier, so it is loaded once with the rest of the global tier rather than
    /// following any single group.
    /// </para>
    /// </summary>
    internal sealed class AccountModerationCounterRepository : OperationRepository
    {
        internal AccountModerationCounterRepository(PostgresDatabase database) : base(database)
        {
        }

        internal async Task IncrementAsync(long telegramUserId, string kind, long delta = 1,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var account = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                telegramUserId, DateTimeOffset.UtcNow, cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO account_moderation_counters (account_id, kind, count, captured_at)
VALUES (@AccountId, @Kind, @Delta, now())
ON CONFLICT (account_id, kind) DO UPDATE
   SET count = account_moderation_counters.count + @Delta, captured_at = now()",
                new { AccountId = account.AccountId, Kind = kind, Delta = delta }, transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }

        /// <summary>
        /// Sets a counter to an absolute value. Used by the migration, which carries the resulting
        /// value from Redis rather than replaying increments - replaying is not idempotent.
        /// </summary>
        internal async Task SetAsync(long telegramUserId, string kind, long count,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var account = await OperationsPrincipalResolver.ResolveUserAsync(connection, transaction,
                telegramUserId, DateTimeOffset.UtcNow, cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO account_moderation_counters (account_id, kind, count, captured_at)
VALUES (@AccountId, @Kind, @Count, now())
ON CONFLICT (account_id, kind) DO UPDATE
   SET count = EXCLUDED.count, captured_at = now()",
                new { AccountId = account.AccountId, Kind = kind, Count = count }, transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<IReadOnlyDictionary<string, long>> GetAsync(long telegramUserId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<(string Kind, long Count)>(new CommandDefinition(@"
SELECT c.kind AS Kind, c.count AS Count
FROM account_moderation_counters c
JOIN telegram_accounts a ON a.account_id = c.account_id
WHERE a.telegram_user_id = @UserId",
                new { UserId = telegramUserId }, cancellationToken: cancellationToken));

            return rows.ToDictionary(row => row.Kind, row => row.Count, StringComparer.Ordinal);
        }
    }
}
