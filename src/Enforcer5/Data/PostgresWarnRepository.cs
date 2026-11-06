using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Models;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// Warn counts in <c>warning_balances</c>, each change a <c>warning_events</c> row written in the same
    /// transaction (rollout step 12; the history alone since step 4). A member without a balance yet starts from the
    /// old Redis count (<see cref="LegacyWarnCounts"/>), so no import is needed. Group and member rows are created on
    /// first use by <see cref="PrincipalResolver"/>.
    /// </summary>
    internal sealed class PostgresWarnRepository : IWarnRepository, IWarningHistoryRepository
    {
        private readonly Func<PostgresDatabase> _database;
        private readonly LegacyWarnCounts _legacy;

        internal PostgresWarnRepository(Func<PostgresDatabase> database, LegacyWarnCounts legacy)
        {
            _database = database;
            _legacy = legacy;
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

        /// <summary>The count after a change from <paramref name="before"/>.</summary>
        internal static long After(WarnOperation operation, long before, long quantity) => operation switch
        {
            WarnOperation.Add => before + Math.Max(0, quantity),
            WarnOperation.Remove => Math.Max(0, before - Math.Max(0, quantity)),
            WarnOperation.Reset => 0,
            WarnOperation.Set => Math.Max(0, quantity),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        /// <summary>
        /// The lifetime count after a change: only an add counts. A recovery sets it on its own
        /// (<see cref="SetLifetimeAsync"/>), as Redis's <c>totalWarns</c> was its own hash.
        /// </summary>
        internal static long LifetimeAfter(WarnOperation operation, long lifetime, long before, long after) =>
            operation == WarnOperation.Add ? lifetime + (after - before) : lifetime;

        /// <summary>The history's name for a change; a recovery's set is an add or a remove.</summary>
        internal static string OperationValue(WarnOperation operation, long before, long after) => operation switch
        {
            WarnOperation.Add => "add",
            WarnOperation.Remove => "remove",
            WarnOperation.Reset => "reset",
            WarnOperation.Set => after > before ? "add" : "remove",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        public async Task<WarnChanged> ApplyAsync(WarnOp op)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var type = TypeValue(op.Kind);

            // Carried out before (a change queued while PostgreSQL was down, whose first try went through after all).
            if (op.Id != null)
            {
                var done = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                    "SELECT warning_event_id FROM warning_events WHERE legacy_key = @Key", new { Key = op.Id }, transaction));
                if (done.HasValue)
                {
                    var now = await ReadBalanceAsync(connection, transaction, op.ChatId, op.UserId, type);
                    await transaction.CommitAsync();
                    return new WarnChanged(now?.Active ?? 0, now?.Active ?? 0, now?.Lifetime ?? 0, done);
                }
            }

            var (groupId, target, actor) = await ResolveAsync(connection, transaction, op.ChatId, op.UserId, op.ActorId);
            var balance = await LockBalanceAsync(connection, transaction, groupId, target.UserId, op.ChatId, op.UserId, op.Kind);

            var before = balance.Active;
            var after = After(op.Operation, before, op.Quantity);
            var lifetime = LifetimeAfter(op.Operation, balance.Lifetime, before, after);
            if (after != before || lifetime != balance.Lifetime)
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE warning_balances SET active_quantity = @After, lifetime_quantity = @Lifetime
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @Type",
                    new { After = after, Lifetime = lifetime, GroupId = groupId, target.UserId, Type = type }, transaction));

            long? eventId = null;
            if (after != before)
                eventId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO warning_events (group_id, account_id, user_id_at_time, actor_user_id, report_id, warning_type,
    operation, quantity, reason, occurred_at, legacy_key)
VALUES (@GroupId, @AccountId, @UserIdAtTime, @ActorUserId, @ReportId, @Type, @Operation, @Quantity, @Reason, @At, @Key)
RETURNING warning_event_id",
                    new
                    {
                        GroupId = groupId, target.AccountId, UserIdAtTime = target.UserId, ActorUserId = actor?.UserId,
                        op.ReportId, Type = type, Operation = OperationValue(op.Operation, before, after),
                        Quantity = Math.Abs(after - before), op.Reason, At = op.At == default ? DateTimeOffset.UtcNow : op.At,
                        Key = op.Id
                    }, transaction));
            await transaction.CommitAsync();
            return new WarnChanged(before, after, lifetime, eventId);
        }

        public async Task SetLifetimeAsync(long chatId, long userId, long lifetime)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var (groupId, target, _) = await ResolveAsync(connection, transaction, chatId, userId, null);
            await LockBalanceAsync(connection, transaction, groupId, target.UserId, chatId, userId, WarnKind.Warn);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE warning_balances SET lifetime_quantity = @Lifetime
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = 'standard'",
                new { Lifetime = Math.Max(0, lifetime), GroupId = groupId, target.UserId }, transaction));
            await transaction.CommitAsync();
        }

        public async Task<long> GetCountAsync(long chatId, WarnKind kind, long userId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var balance = await ReadBalanceAsync(connection, null, chatId, userId, TypeValue(kind));
            return balance?.Active ?? (await _legacy.ReadAsync(chatId, kind, userId)).Active;
        }

        public async Task<WarnCounts> GetCountsAsync(long chatId, long userId)
        {
            var balances = await GetBalancesAsync(chatId, userId);
            async Task<(long Active, long Lifetime)> Of(WarnKind kind)
            {
                var row = balances.FirstOrDefault(b => b.WarningType == TypeValue(kind));
                return row != null ? (row.Active, row.Lifetime) : await _legacy.ReadAsync(chatId, kind, userId);
            }
            var warns = await Of(WarnKind.Warn);
            return new WarnCounts(warns.Active, (await Of(WarnKind.Prewarn)).Active, (await Of(WarnKind.Media)).Active, warns.Lifetime);
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

        // ------------------------------------------------------------------ helpers

        private static Task<Balance> ReadBalanceAsync(DbConnection connection, DbTransaction transaction, long chatId,
            long userId, string type) =>
            connection.QuerySingleOrDefaultAsync<Balance>(new CommandDefinition(@"
SELECT b.active_quantity AS Active, b.lifetime_quantity AS Lifetime
FROM warning_balances b
JOIN groups g ON g.group_id = b.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN user_identity_links l ON l.user_id = b.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE c.telegram_chat_id = @ChatId AND a.telegram_user_id = @UserId AND b.warning_type = @Type",
                new { ChatId = chatId, UserId = userId, Type = type }, transaction));

        /// <summary>
        /// The member's balance, locked for this transaction. A member with none yet gets one from the old Redis count
        /// (a second process doing the same at once keeps the first one's row).
        /// </summary>
        private async Task<Balance> LockBalanceAsync(DbConnection connection, DbTransaction transaction, long groupId,
            long userRowId, long chatId, long userId, WarnKind kind)
        {
            var type = TypeValue(kind);
            const string select = @"
SELECT active_quantity AS Active, lifetime_quantity AS Lifetime FROM warning_balances
WHERE group_id = @GroupId AND user_id = @UserId AND warning_type = @Type FOR UPDATE";
            var args = new { GroupId = groupId, UserId = userRowId, Type = type };
            var balance = await connection.QuerySingleOrDefaultAsync<Balance>(new CommandDefinition(select, args, transaction));
            if (balance != null) return balance;

            var (active, lifetime) = await _legacy.ReadAsync(chatId, kind, userId);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO warning_balances (group_id, user_id, warning_type, active_quantity, lifetime_quantity)
VALUES (@GroupId, @UserId, @Type, @Active, @Lifetime)
ON CONFLICT (group_id, user_id, warning_type) DO NOTHING",
                new { GroupId = groupId, UserId = userRowId, Type = type, Active = active, Lifetime = lifetime }, transaction));
            return await connection.QuerySingleAsync<Balance>(new CommandDefinition(select, args, transaction));
        }

        /// <summary>
        /// The group, the member and the admin. Usually they all exist and are only looked up; the shared principal
        /// lock (as the ban history and the observation batch take it) is only taken when one has to be created, so a
        /// warn never waits for the minute's observation batch.
        /// </summary>
        private static async Task<(long GroupId, PrincipalResolver.UserIds Target, PrincipalResolver.UserIds Actor)> ResolveAsync(
            DbConnection connection, DbTransaction transaction, long chatId, long userId, long? actorId)
        {
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId, transaction);
            var target = await FindUserAsync(connection, transaction, userId);
            var actor = actorId.HasValue ? await FindUserAsync(connection, transaction, actorId.Value) : null;
            if (groupId.HasValue && target != null && (actor != null || !actorId.HasValue))
                return (groupId.Value, target, actor);

            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            groupId ??= (await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId,
                chatType: PostgresBanHistoryRepository.ChatType(chatId))).GroupId;
            target ??= await PrincipalResolver.EnsureUserAsync(connection, transaction, userId);
            if (actorId.HasValue) actor ??= await PrincipalResolver.EnsureUserAsync(connection, transaction, actorId.Value);
            return (groupId.Value, target, actor);
        }

        private static async Task<PrincipalResolver.UserIds> FindUserAsync(DbConnection connection, DbTransaction transaction,
            long telegramUserId)
        {
            var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(@"
SELECT a.account_id AS AccountId, l.user_id AS UserId
FROM telegram_accounts a JOIN user_identity_links l ON l.account_id = a.account_id AND l.unlinked_at IS NULL
WHERE a.telegram_user_id = @Id", new { Id = telegramUserId }, transaction));
            return row == null ? null : new PrincipalResolver.UserIds(row.AccountId, row.UserId);
        }

        private sealed class Balance
        {
            public long Active { get; set; }
            public long Lifetime { get; set; }
        }

        private sealed class UserRow
        {
            public long AccountId { get; set; }
            public long UserId { get; set; }
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
