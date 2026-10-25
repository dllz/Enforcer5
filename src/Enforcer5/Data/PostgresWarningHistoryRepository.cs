using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Models;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// Warn history in <c>warning_events</c> and the balances it leaves in <c>warning_balances</c>.
    /// Group and member rows are created on first use by <see cref="PrincipalResolver"/>.
    /// </summary>
    internal sealed class PostgresWarningHistoryRepository : IWarningHistoryRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresWarningHistoryRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        internal static string TypeValue(WarnKind kind) => kind switch
        {
            WarnKind.Warn => "standard",
            WarnKind.Prewarn => "prewarning",
            WarnKind.Media => "media",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        internal static string OperationValue(WarnOperation operation) => operation switch
        {
            WarnOperation.Add => "add",
            WarnOperation.Remove => "remove",
            WarnOperation.Reset => "reset",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        public async Task<long> RecordAsync(WarnEvent warn)
        {
            if (warn.Quantity <= 0)
                throw new ArgumentOutOfRangeException(nameof(warn), warn.Quantity, "A warn event changes at least one warn.");

            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // The same lock as the ban history and the observation batch, which also create accounts.
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, warn.ChatId,
                chatType: PostgresBanHistoryRepository.ChatType(warn.ChatId));
            var target = await PrincipalResolver.EnsureUserAsync(connection, transaction, warn.UserId);
            long? actorUserId = null;
            if (warn.ActorId.HasValue)
                actorUserId = (await PrincipalResolver.EnsureUserAsync(connection, transaction, warn.ActorId.Value)).UserId;

            var type = TypeValue(warn.Kind);
            var eventId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO warning_events (group_id, account_id, user_id_at_time, actor_user_id, warning_type, operation,
    quantity, reason, occurred_at)
VALUES (@GroupId, @AccountId, @UserIdAtTime, @ActorUserId, @Type, @Operation, @Quantity, @Reason, @At)
RETURNING warning_event_id",
                new
                {
                    group.GroupId, target.AccountId, UserIdAtTime = target.UserId, ActorUserId = actorUserId, Type = type,
                    Operation = OperationValue(warn.Operation), warn.Quantity, warn.Reason, warn.At
                }, transaction));

            // Active copies Redis. Lifetime copies Redis's totalWarns where there is one, and
            // otherwise counts the adds seen here.
            var added = warn.Operation == WarnOperation.Add ? warn.Quantity : 0;
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_balances (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @Type, @Active, COALESCE(@Lifetime, @Added))
ON CONFLICT (group_id, user_id, warning_type) DO UPDATE
SET active_quantity = EXCLUDED.active_quantity,
    lifetime_quantity = COALESCE(@Lifetime, warning_balances.lifetime_quantity + @Added)",
                new
                {
                    group.GroupId, target.UserId, Type = type, Active = Math.Max(0, warn.ActiveAfter),
                    Lifetime = warn.LifetimeAfter.HasValue ? Math.Max(0, warn.LifetimeAfter.Value) : (long?)null,
                    Added = added
                }, transaction));
            await transaction.CommitAsync();
            return eventId;
        }

        public async Task<IReadOnlyList<WarnRecord>> GetHistoryAsync(long chatId, long userId, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<HistoryRow>(new CommandDefinition(@"
SELECT e.warning_event_id AS EventId, e.warning_type AS WarningType, e.operation AS Operation,
       e.quantity AS Quantity, e.reason AS Reason, actor.telegram_user_id AS ActorId, e.occurred_at AS OccurredAt
FROM warning_events e
JOIN groups g ON g.group_id = e.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = e.account_id
LEFT JOIN user_identity_links l ON l.user_id = e.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId
ORDER BY e.occurred_at DESC, e.warning_event_id DESC
LIMIT @Limit", new { ChatId = chatId, UserId = userId, Limit = limit }));
            return rows.Select(r => new WarnRecord(r.EventId, r.WarningType, r.Operation, r.Quantity, r.Reason, r.ActorId,
                new DateTimeOffset(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc)))).ToList();
        }

        public async Task<IReadOnlyList<WarnBalance>> GetBalancesAsync(long chatId, long userId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<WarnBalance>(new CommandDefinition(@"
SELECT b.warning_type AS WarningType, b.active_quantity AS Active, b.lifetime_quantity AS Lifetime
FROM warning_balances b
JOIN groups g ON g.group_id = b.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN user_identity_links l ON l.user_id = b.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId
ORDER BY b.warning_type", new { ChatId = chatId, UserId = userId }));
            return rows.ToList();
        }

        private sealed class HistoryRow
        {
            public long EventId { get; set; }
            public string WarningType { get; set; }
            public string Operation { get; set; }
            public long Quantity { get; set; }
            public string Reason { get; set; }
            public long? ActorId { get; set; }
            public DateTime OccurredAt { get; set; }
        }
    }
}
