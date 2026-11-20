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
    /// Who the bot muted in each group, in PostgreSQL (rollout step 16): <c>group_mutes</c>, one row per member per group,
    /// shared by both editions. A member has one kind at a time, the latest mute's, except that Mute On Join never
    /// replaces a mute for good (so /unmutenewjoiners does not lift it). Times are kept to the second, as Redis kept them.
    /// Reached through <see cref="Helpers.MuteStore"/>, which imports each group's old keys first.
    /// </summary>
    internal sealed class PostgresMuteRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresMuteRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        internal static string ImportName(long chatId) => $"mutes:{chatId}";

        internal Task MuteAsync(long chatId, long userId, DateTime nowUtc) => ExecuteAsync(@"
INSERT INTO group_mutes (telegram_chat_id, telegram_user_id, kind, until_at, muted_at, source)
VALUES (@ChatId, @UserId, 'mute', NULL, @Now, 'bot')
ON CONFLICT (telegram_chat_id, telegram_user_id) DO UPDATE
SET kind = 'mute', until_at = NULL, muted_at = EXCLUDED.muted_at, source = 'bot'",
            new { ChatId = chatId, UserId = userId, Now = PostgresTempbanRepository.Second(nowUtc) });

        internal Task TempMuteAsync(long chatId, long userId, DateTime untilUtc, DateTime nowUtc) => ExecuteAsync(@"
INSERT INTO group_mutes (telegram_chat_id, telegram_user_id, kind, until_at, muted_at, source)
VALUES (@ChatId, @UserId, 'tempmute', @Until, @Now, 'bot')
ON CONFLICT (telegram_chat_id, telegram_user_id) DO UPDATE
SET kind = 'tempmute', until_at = EXCLUDED.until_at, muted_at = EXCLUDED.muted_at, source = 'bot'",
            new
            {
                ChatId = chatId, UserId = userId, Until = PostgresTempbanRepository.Second(untilUtc),
                Now = PostgresTempbanRepository.Second(nowUtc)
            });

        /// <summary>Mute On Join: a member already muted for good stays so.</summary>
        internal Task JoinerAsync(long chatId, long userId, DateTime nowUtc) => ExecuteAsync(@"
INSERT INTO group_mutes (telegram_chat_id, telegram_user_id, kind, until_at, muted_at, source)
VALUES (@ChatId, @UserId, 'joiner', NULL, @Now, 'bot')
ON CONFLICT (telegram_chat_id, telegram_user_id) DO UPDATE
SET kind = 'joiner', until_at = NULL, muted_at = EXCLUDED.muted_at, source = 'bot'
WHERE group_mutes.kind <> 'mute'",
            new { ChatId = chatId, UserId = userId, Now = PostgresTempbanRepository.Second(nowUtc) });

        internal Task ClearAsync(long chatId, long userId) => ExecuteAsync(
            "DELETE FROM group_mutes WHERE telegram_chat_id = @ChatId AND telegram_user_id = @UserId",
            new { ChatId = chatId, UserId = userId });

        /// <summary>The members of <paramref name="kind"/> (<c>mute</c> or <c>joiner</c>), in the order they were muted.</summary>
        internal async Task<IReadOnlyList<long>> MembersAsync(long chatId, string kind)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return (await connection.QueryAsync<long>(new CommandDefinition(@"
SELECT telegram_user_id FROM group_mutes WHERE telegram_chat_id = @ChatId AND kind = @Kind ORDER BY muted_at, telegram_user_id",
                new { ChatId = chatId, Kind = kind }))).ToList();
        }

        /// <summary>The tempmutes still running; ended ones are deleted (Telegram lifted them by itself).</summary>
        internal async Task<IReadOnlyList<TimedRestriction>> TempMutedAsync(long chatId, DateTime nowUtc)
        {
            var now = PostgresTempbanRepository.Second(nowUtc);
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM group_mutes WHERE telegram_chat_id = @ChatId AND kind = 'tempmute' AND until_at <= @Now",
                new { ChatId = chatId, Now = now }));
            var rows = await connection.QueryAsync<(long UserId, DateTime Until)>(new CommandDefinition(@"
SELECT telegram_user_id, until_at FROM group_mutes WHERE telegram_chat_id = @ChatId AND kind = 'tempmute'
ORDER BY until_at, telegram_user_id",
                new { ChatId = chatId }));
            // Npgsql reads timestamptz as a UTC DateTime.
            return rows.Select(r => new TimedRestriction(r.UserId, DateTime.SpecifyKind(r.Until, DateTimeKind.Utc))).ToList();
        }

        /// <summary>True when the group's old keys were imported (or there was nothing to import before).</summary>
        internal async Task<bool> ImportedAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM storage_imports WHERE name = @Name)", new { Name = ImportName(chatId) }));
        }

        /// <summary>
        /// Imports the group's old mutes once (<c>storage_imports</c> says it ran); a member with a row already keeps it.
        /// Returns how many were imported, or null when it had run before.
        /// </summary>
        internal async Task<int?> ImportAsync(long chatId, IReadOnlyList<LegacyMute> mutes, DateTime nowUtc)
        {
            var name = ImportName(chatId);
            var now = PostgresTempbanRepository.Second(nowUtc);
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@Name))",
                new { Name = name }, transaction));
            if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM storage_imports WHERE name = @Name)", new { Name = name }, transaction)))
                return null;
            var imported = 0;
            foreach (var mute in mutes)
            {
                imported += await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_mutes (telegram_chat_id, telegram_user_id, kind, until_at, muted_at, source)
VALUES (@ChatId, @UserId, @Kind, @Until, @Now, 'redis')
ON CONFLICT (telegram_chat_id, telegram_user_id) DO NOTHING",
                    new
                    {
                        ChatId = chatId, mute.UserId, mute.Kind,
                        Until = mute.UntilUtc is { } until ? PostgresTempbanRepository.Second(until) : (DateTimeOffset?)null,
                        Now = now
                    }, transaction));
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO storage_imports (name, done_at, details) VALUES (@Name, @Now, @Details)",
                new { Name = name, Now = now, Details = $"{mutes.Count} mutes, {imported} imported" }, transaction));
            await transaction.CommitAsync();
            return imported;
        }

        private async Task ExecuteAsync(string sql, object args)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(new CommandDefinition(sql, args));
        }
    }
}
