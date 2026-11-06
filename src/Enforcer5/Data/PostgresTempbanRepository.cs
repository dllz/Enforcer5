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
    /// One edition's tempban schedule in PostgreSQL (rollout step 13): <c>tempban_schedule</c>, one row per member per
    /// group, so two tempbans ending in the same second no longer collide and a new tempban replaces the old one.
    /// Times are kept to the second, as the slot of the Redis schedule was.
    /// </summary>
    internal sealed class PostgresTempbanRepository : ITempbanRepository
    {
        /// <summary>A claim older than this is taken again: the process that made it stopped before the unban.</summary>
        internal static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(10);

        /// <summary>At most this many due tempbans are claimed at a time.</summary>
        internal const int ClaimBatch = 200;

        private readonly Func<PostgresDatabase> _database;
        private readonly string _edition;
        private readonly string _claimer = $"{Environment.MachineName}:{Environment.ProcessId}";

        internal PostgresTempbanRepository(Func<PostgresDatabase> database, string edition)
        {
            _database = database;
            _edition = edition;
        }

        internal string Edition => _edition;

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        /// <summary>To the second, UTC: the schedule's resolution.</summary>
        internal static DateTimeOffset Second(DateTime utc)
        {
            var at = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            return DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());
        }

        public async Task<IReadOnlyList<TimedRestriction>> GetActiveAsync(long chatId, DateTime nowUtc)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(new CommandDefinition(@"
SELECT telegram_chat_id AS ChatId, telegram_user_id AS UserId, until_at AS Until FROM tempban_schedule
WHERE edition = @Edition AND telegram_chat_id = @ChatId AND until_at > @Now ORDER BY until_at, telegram_user_id",
                new { Edition = _edition, ChatId = chatId, Now = Second(nowUtc) }));
            return rows.Select(r => new TimedRestriction(r.UserId, r.UntilAt.UtcDateTime)).ToList();
        }

        public async Task<long> ScheduleAsync(long chatId, long userId, DateTime untilUtc, DateTime nowUtc)
        {
            var until = Second(untilUtc);
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO tempban_schedule (edition, telegram_chat_id, telegram_user_id, until_at, created_at, source)
VALUES (@Edition, @ChatId, @UserId, @Until, @Now, 'bot')
ON CONFLICT (edition, telegram_chat_id, telegram_user_id) DO UPDATE
SET until_at = EXCLUDED.until_at, created_at = EXCLUDED.created_at, source = 'bot', claimed_at = NULL, claimed_by = NULL",
                new { Edition = _edition, ChatId = chatId, UserId = userId, Until = until, Now = Second(nowUtc) }));
            return until.ToUnixTimeSeconds();
        }

        public async Task CancelAsync(long chatId, long userId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM tempban_schedule WHERE edition = @Edition AND telegram_chat_id = @ChatId AND telegram_user_id = @UserId",
                new { Edition = _edition, ChatId = chatId, UserId = userId }));
        }

        public async Task<IReadOnlyList<UserTempban>> OfUserAsync(long userId, DateTime nowUtc)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(new CommandDefinition(@"
SELECT telegram_chat_id AS ChatId, telegram_user_id AS UserId, until_at AS Until FROM tempban_schedule
WHERE edition = @Edition AND telegram_user_id = @UserId AND until_at > @Now ORDER BY until_at",
                new { Edition = _edition, UserId = userId, Now = Second(nowUtc) }));
            return rows.Select(r => new UserTempban(r.ChatId, r.UntilAt.UtcDateTime)).ToList();
        }

        /// <summary>
        /// Claims the due tempbans for this process (oldest first; skipping rows another one holds, and taking back
        /// claims older than <see cref="ClaimFor"/>), so two processes of one edition never lift the same one.
        /// </summary>
        public async Task<IReadOnlyList<TempbanEntry>> DueAsync(DateTime nowUtc)
        {
            var now = Second(nowUtc);
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(new CommandDefinition(@"
UPDATE tempban_schedule s SET claimed_at = @Now, claimed_by = @Claimer
FROM (SELECT edition, telegram_chat_id, telegram_user_id FROM tempban_schedule
      WHERE edition = @Edition AND until_at <= @Now AND (claimed_at IS NULL OR claimed_at < @Stale)
      ORDER BY until_at LIMIT @Batch FOR UPDATE SKIP LOCKED) due
WHERE s.edition = due.edition AND s.telegram_chat_id = due.telegram_chat_id AND s.telegram_user_id = due.telegram_user_id
RETURNING s.telegram_chat_id AS ChatId, s.telegram_user_id AS UserId, s.until_at AS Until",
                new { Edition = _edition, Now = now, Stale = now - ClaimFor, Claimer = _claimer, Batch = ClaimBatch }));
            return rows.OrderBy(r => r.UntilAt)
                .Select(r => new TempbanEntry(r.UntilAt.ToUnixTimeSeconds().ToString(), r.ChatId, r.UserId)).ToList();
        }

        /// <summary>Removes the entry, unless the member was tempbanned again since (a new end time).</summary>
        public async Task RemoveAsync(TempbanEntry entry)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM tempban_schedule
WHERE edition = @Edition AND telegram_chat_id = @ChatId AND telegram_user_id = @UserId AND until_at = @Until",
                new
                {
                    Edition = _edition, entry.ChatId, entry.UserId,
                    Until = DateTimeOffset.FromUnixTimeSeconds(long.Parse(entry.Slot))
                }));
        }

        /// <summary>Every member this edition has scheduled, for the in-memory list a join is checked against.</summary>
        internal async Task<IReadOnlyList<(long ChatId, long UserId)>> ScheduledAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(new CommandDefinition(
                "SELECT telegram_chat_id AS ChatId, telegram_user_id AS UserId FROM tempban_schedule WHERE edition = @Edition",
                new { Edition = _edition }));
            return rows.Select(r => (r.ChatId, r.UserId)).ToList();
        }

        /// <summary>
        /// Imports this edition's old Redis schedule once (<c>storage_imports</c> says it ran): the latest end per member
        /// per group, as it is (even far in the future: an end that has not come is a ban still in force). A member
        /// already scheduled here keeps their row. Returns how many were imported, or null when it had run before.
        /// </summary>
        internal async Task<int?> ImportAsync(IReadOnlyList<TempbanEntry> entries, DateTime nowUtc)
        {
            var name = $"tempbans:{_edition}";
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@Name))",
                new { Name = name }, transaction));
            if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM storage_imports WHERE name = @Name)", new { Name = name }, transaction)))
                return null;

            var latest = Latest(entries);
            var maxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
            var imported = 0;
            foreach (var ((chatId, userId), seconds) in latest)
            {
                if (seconds > maxSeconds) continue;
                imported += await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO tempban_schedule (edition, telegram_chat_id, telegram_user_id, until_at, created_at, source)
VALUES (@Edition, @ChatId, @UserId, @Until, @Now, 'redis')
ON CONFLICT (edition, telegram_chat_id, telegram_user_id) DO NOTHING",
                    new
                    {
                        Edition = _edition, ChatId = chatId, UserId = userId,
                        Until = DateTimeOffset.FromUnixTimeSeconds(seconds), Now = Second(nowUtc)
                    }, transaction));
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO storage_imports (name, done_at, details) VALUES (@Name, @Now, @Details)",
                new { Name = name, Now = Second(nowUtc), Details = $"{entries.Count} entries, {imported} imported" }, transaction));
            await transaction.CommitAsync();
            return imported;
        }

        /// <summary>The latest end per member per group; entries whose slot is not a number are left out.</summary>
        internal static Dictionary<(long ChatId, long UserId), long> Latest(IEnumerable<TempbanEntry> entries)
        {
            var latest = new Dictionary<(long, long), long>();
            foreach (var entry in entries)
            {
                if (!long.TryParse(entry.Slot, out var seconds)) continue;
                var key = (entry.ChatId, entry.UserId);
                if (!latest.TryGetValue(key, out var known) || seconds > known) latest[key] = seconds;
            }
            return latest;
        }

        private sealed class Row
        {
            public long ChatId { get; set; }
            public long UserId { get; set; }
            // Npgsql reads timestamptz as a UTC DateTime.
            public DateTime Until { get; set; }
            public DateTimeOffset UntilAt => new(DateTime.SpecifyKind(Until, DateTimeKind.Utc));
        }
    }
}
