using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;
using StackExchange.Redis;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// A basic group that became a supergroup gets a new id from Telegram (Daniel, 2026-11-06: until then its settings,
    /// roles, warns and log channel stayed on the old id and the group started from nothing). Learned from the service
    /// messages (migrate_to_chat_id in the old group, migrate_from_chat_id in the new one) and from a refusal that names
    /// the new id; each move is done once per process and is safe to repeat, so both editions may do it.
    /// <list type="bullet">
    /// <item>PostgreSQL: the group's row points at the new chat. Rows the new id gathered meanwhile (a few seconds' worth)
    /// move to the old group's, or give way where it has one already; then the new id's group row goes. The tempban
    /// schedule, and log channels and admin chats that were the old group, follow.</item>
    /// <item>Redis: the group's <c>chat:{id}:…</c> keys are renamed, unless the new id has one already.</item>
    /// <item>What this process keeps in memory about either id is forgotten.</item>
    /// </list>
    /// </summary>
    internal static class GroupMigration
    {
        private static readonly ConcurrentDictionary<long, long> Done = new();

        /// <summary>The group <paramref name="from"/> is now <paramref name="to"/>.</summary>
        internal static void Learned(long from, long to)
        {
            if (from == 0 || to == 0 || from == to || !Done.TryAdd(from, to)) return;
            Bot.Dispatch(() => MoveAsync(from, to), "GroupMigration");
        }

        /// <summary>The service message of an upgrade, in either group.</summary>
        internal static void OnMessage(Telegram.Bot.Types.Message message)
        {
            if (message?.MigrateToChatId is { } to && to != 0) Learned(message.Chat.Id, to);
            else if (message?.MigrateFromChatId is { } from && from != 0) Learned(from, message.Chat.Id);
        }

        internal static async Task MoveAsync(long from, long to)
        {
            LogHelper.Info($"GroupMigration: {from} became {to}");
            if (PostgresStorage.Ready)
            {
                try
                {
                    await MovePostgresAsync(from, to);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Moving group {from} to its new id {to} in PostgreSQL");
                }
            }
            try
            {
                await MoveRedisAsync(from, to);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Moving group {from} to its new id {to} in Redis");
            }
            Forget(from);
            Forget(to);
        }

        private static async Task MovePostgresAsync(long from, long to)
        {
            await using var connection = await PostgresStorage.Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('group-migration'), 0)", transaction: transaction);

            var oldChat = await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT chat_id FROM telegram_chats WHERE telegram_chat_id = @From", new { From = from }, transaction);
            var oldGroup = oldChat == null ? null : await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT group_id FROM groups WHERE chat_id = @Chat", new { Chat = oldChat }, transaction);
            var newChat = await PrincipalResolver.EnsureChatAsync(connection, transaction, to, "supergroup");

            var newGroup = await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT group_id FROM groups WHERE chat_id = @Chat", new { Chat = newChat }, transaction);
            if (oldGroup is { } first && newGroup is { } second && first != second)
            {
                // Both ids have a group: the older one is the group's history, the other a few seconds' or (when the
                // other edition moved it already and the old id was seen again since) a few minutes' worth.
                var olderFirst = await connection.QuerySingleAsync<bool>(
                    "SELECT (SELECT created_at FROM groups WHERE group_id = @A) <= (SELECT created_at FROM groups WHERE group_id = @B)",
                    new { A = first, B = second }, transaction);
                if (!olderFirst) (oldGroup, newGroup) = (second, first);
            }
            if (oldGroup is { } group)
            {
                if (newGroup is { } gathered && gathered != group)
                {
                    foreach (var table in await GroupTablesAsync(connection, transaction))
                    {
                        // Where both have a row (one per group, or per member), the old group's stays.
                        await connection.ExecuteAsync("SAVEPOINT move_rows", transaction: transaction);
                        try
                        {
                            await connection.ExecuteAsync($"UPDATE \"{table}\" SET group_id = @Old WHERE group_id = @New",
                                new { Old = group, New = gathered }, transaction);
                            await connection.ExecuteAsync("RELEASE SAVEPOINT move_rows", transaction: transaction);
                        }
                        catch (Npgsql.PostgresException e) when (e.SqlState == "23505")
                        {
                            await connection.ExecuteAsync("ROLLBACK TO SAVEPOINT move_rows", transaction: transaction);
                            await connection.ExecuteAsync($"DELETE FROM \"{table}\" WHERE group_id = @New",
                                new { New = gathered }, transaction);
                        }
                    }
                    await connection.ExecuteAsync("DELETE FROM groups WHERE group_id = @New", new { New = gathered }, transaction);
                }
                await connection.ExecuteAsync("UPDATE groups SET chat_id = @Chat, updated_at = now() WHERE group_id = @Group",
                    new { Chat = newChat, Group = group }, transaction);
            }

            // A log channel that was the old group.
            if (oldChat != null && oldChat != newChat)
                await connection.ExecuteAsync("UPDATE group_settings SET log_chat_id = @New WHERE log_chat_id = @Old",
                    new { New = newChat, Old = oldChat }, transaction);
            // An admin chat that was the old group (Detection reports).
            await connection.ExecuteAsync(@"
UPDATE detection_policies SET settings = jsonb_set(settings, '{admin_chat_id}', to_jsonb(@To))
WHERE settings->>'admin_chat_id' = @FromText",
                new { To = to, FromText = from.ToString() }, transaction);
            // Tempbans still to lift there; one per member, the new id's wins.
            await connection.ExecuteAsync(@"
DELETE FROM tempban_schedule o WHERE o.telegram_chat_id = @From AND EXISTS (
    SELECT 1 FROM tempban_schedule n WHERE n.edition = o.edition AND n.telegram_chat_id = @To AND n.telegram_user_id = o.telegram_user_id);
UPDATE tempban_schedule SET telegram_chat_id = @To WHERE telegram_chat_id = @From",
                new { From = from, To = to }, transaction);
            await transaction.CommitAsync();
        }

        // Every table that hangs off a group by its group_id, but the groups table itself.
        private static async Task<IReadOnlyList<string>> GroupTablesAsync(DbConnection connection, DbTransaction transaction) =>
            (await connection.QueryAsync<string>(@"
SELECT c.table_name FROM information_schema.columns c
JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
WHERE c.table_schema = current_schema() AND c.column_name = 'group_id' AND c.table_name <> 'groups'
  AND t.table_type = 'BASE TABLE'
ORDER BY c.table_name", transaction: transaction)).ToList();

        // The group's own keys. Most data lives in PostgreSQL by now; these are what is left in Redis.
        internal static readonly string[] RedisSuffixes =
        {
            ":about", ":adminOff", ":allowedchannels", ":auth", ":banned", ":blockList", ":blockedinline", ":blockedstickersets",
            ":channelposts", ":chatlast", ":deauth", ":details", ":extra", ":language", ":lastwelcome", ":linkedchannel",
            ":mediawarn", ":mod", ":modlog", ":muted", ":mutedJoiners", ":prewarns", ":raid", ":recentwarnreasons",
            ":reportblocked", ":rules", ":settings", ":spamList", ":tagall2", ":tempmuted", ":totalWarns", ":userlast",
            ":warnreasonmode", ":warnreasons", ":warns", ":watch", ":welcome", "links", "",
        };

        private static async Task MoveRedisAsync(long from, long to)
        {
            var db = Redis.db;
            var keys = new HashSet<string>(RedisSuffixes.Select(s => $"chat:{from}{s}"));
            // Per-member and per-name keys too, where the server can be scanned (not in the test harness). Telegram
            // upgrades a group rarely; a scan of the keyspace is affordable then.
            if (db.Multiplexer?.GetEndPoints().FirstOrDefault() is { } endpoint && db.Multiplexer.GetServer(endpoint) is { } server)
                await foreach (var key in server.KeysAsync(db.Database, $"chat:{from}:*", pageSize: 10000))
                    keys.Add(key);
            var moved = 0;
            foreach (var key in keys)
            {
                var target = $"chat:{to}" + key.Substring($"chat:{from}".Length);
                if (await db.KeyExistsAsync(key) && await db.KeyRenameAsync(key, target, When.NotExists)) moved++;
            }
            await db.StringIncrementAsync(GroupConfigs.VersionKey(to));
            await db.KeyDeleteAsync(Detection.DetectionSettings.CacheKey(to));
            await db.StringIncrementAsync(Detection.DetectionSettings.VersionKey(to));
            LogHelper.Info($"GroupMigration: {moved} Redis keys of {from} moved to {to}");
        }

        private static void Forget(long chatId)
        {
            GroupConfigs.Forget(chatId);
            Detection.DetectionSettings.Forget(chatId);
            AdminDirectory.Forget(chatId);
            _ = Detection.AdminChats.ReloadAsync();
        }

        internal static void ResetForTests() => Done.Clear();
    }
}
