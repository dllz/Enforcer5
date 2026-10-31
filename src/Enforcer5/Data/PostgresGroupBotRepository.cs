using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    internal sealed class PostgresGroupBotRepository : IGroupBotRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresGroupBotRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private sealed class Row
        {
            public long ChatId { get; set; }
            public long BotId { get; set; }
            public string Status { get; set; }
            public string Source { get; set; }
            public long? SetBy { get; set; }
            public DateTime SetAt { get; set; }
        }

        public async Task<IReadOnlyList<GroupBotRecord>> AllAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(@"
SELECT c.telegram_chat_id AS ChatId, a.telegram_user_id AS BotId, x.status AS Status, x.source AS Source,
       b.telegram_user_id AS SetBy, x.set_at AS SetAt
FROM group_bots x
JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN telegram_accounts a ON a.account_id = x.account_id
LEFT JOIN telegram_accounts b ON b.account_id = x.set_by_account_id");
            return rows.Select(r => new GroupBotRecord(r.ChatId, r.BotId, r.Status, r.Source, r.SetBy,
                new DateTimeOffset(DateTime.SpecifyKind(r.SetAt, DateTimeKind.Utc)))).ToList();
        }

        public async Task SetAsync(GroupBotRecord record, bool overwrite)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, record.ChatId,
                chatType: PostgresBanHistoryRepository.ChatType(record.ChatId));
            var bot = await PrincipalResolver.EnsureUserAsync(connection, transaction, record.BotId, isBot: true);
            long? by = record.SetBy is { } setBy && setBy != record.BotId
                ? (await PrincipalResolver.EnsureUserAsync(connection, transaction, setBy)).AccountId
                : null;
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_bots (group_id, account_id, status, source, set_by_account_id, set_at)
VALUES (@GroupId, @AccountId, @Status, @Source, @By, @At)
ON CONFLICT (group_id, account_id) DO " + (overwrite
                    ? "UPDATE SET status = excluded.status, source = excluded.source, set_by_account_id = excluded.set_by_account_id, set_at = excluded.set_at"
                    : "NOTHING"),
                new { group.GroupId, bot.AccountId, record.Status, record.Source, By = by, At = record.SetAt.UtcDateTime },
                transaction));
            await transaction.CommitAsync();
        }

        public async Task RecordEventAsync(BotEvent botEvent)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, botEvent.ChatId,
                chatType: PostgresBanHistoryRepository.ChatType(botEvent.ChatId));
            var bot = await PrincipalResolver.EnsureUserAsync(connection, transaction, botEvent.BotId, isBot: true);
            long? actor = botEvent.ActorId is { } actorId && actorId != botEvent.BotId
                ? (await PrincipalResolver.EnsureUserAsync(connection, transaction, actorId)).AccountId
                : null;
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO bot_events (group_id, bot_account_id, actor_account_id, actor_was_admin, kind, action, edition, occurred_at)
VALUES (@GroupId, @BotAccountId, @Actor, @ActorWasAdmin, @Kind, @Action, @Edition, @At)",
                new
                {
                    group.GroupId, BotAccountId = bot.AccountId, Actor = actor, botEvent.ActorWasAdmin, botEvent.Kind,
                    botEvent.Action, botEvent.Edition, At = botEvent.At.UtcDateTime
                }, transaction));
            await transaction.CommitAsync();
        }
    }
}
