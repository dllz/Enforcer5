using System;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>A group the bot may not post in: since when, and when Daniel was last asked about leaving it.</summary>
    internal sealed record BotMute(long ChatId, DateTimeOffset MutedSince, DateTimeOffset? LeaveAskedAt);

    /// <summary>The groups an edition may not post in (<c>bot_mutes</c>; Helpers/MutedGroups).</summary>
    internal interface IBotMuteRepository
    {
        /// <summary>Notes the bot may not post in the group from <paramref name="since"/>, unless it is noted already.</summary>
        Task MarkAsync(long chatId, DateTimeOffset since);

        /// <summary>The bot may post there again, left, or was removed: forgotten.</summary>
        Task ClearAsync(long chatId);

        Task<BotMute> GetAsync(long chatId);

        /// <summary>
        /// Claims asking Daniel about the group: true (and asked at <paramref name="now"/>) when it has been muted since
        /// <paramref name="mutedBefore"/> or earlier and was not asked since <paramref name="askedBefore"/>. One edition, one
        /// process asks.
        /// </summary>
        Task<bool> ClaimAskAsync(long chatId, DateTimeOffset now, DateTimeOffset mutedBefore, DateTimeOffset askedBefore);

        /// <summary>Daniel said Keep: not asked again before <see cref="MutedGroups.AskAgainAfter"/>.</summary>
        Task KeepAsync(long chatId, DateTimeOffset now);

    }

    internal sealed class PostgresBotMuteRepository : IBotMuteRepository
    {
        private readonly Func<PostgresDatabase> _database;
        private readonly string _edition;

        internal PostgresBotMuteRepository(Func<PostgresDatabase> database, string edition)
        {
            _database = database;
            _edition = edition;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        public async Task MarkAsync(long chatId, DateTimeOffset since)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO bot_mutes (edition, telegram_chat_id, muted_since) VALUES (@Edition, @ChatId, @Since)
ON CONFLICT (edition, telegram_chat_id) DO NOTHING", new { Edition = _edition, ChatId = chatId, Since = since });
        }

        public async Task ClearAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync("DELETE FROM bot_mutes WHERE edition = @Edition AND telegram_chat_id = @ChatId",
                new { Edition = _edition, ChatId = chatId });
        }

        public async Task<BotMute> GetAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<(long, DateTimeOffset, DateTimeOffset?)?>(@"
SELECT telegram_chat_id, muted_since, leave_asked_at FROM bot_mutes WHERE edition = @Edition AND telegram_chat_id = @ChatId",
                new { Edition = _edition, ChatId = chatId });
            return row is { } r ? new BotMute(r.Item1, r.Item2, r.Item3) : null;
        }

        public async Task<bool> ClaimAskAsync(long chatId, DateTimeOffset now, DateTimeOffset mutedBefore, DateTimeOffset askedBefore)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync(@"
UPDATE bot_mutes SET leave_asked_at = @Now
WHERE edition = @Edition AND telegram_chat_id = @ChatId AND muted_since <= @MutedBefore
  AND (leave_asked_at IS NULL OR leave_asked_at <= @AskedBefore)",
                new { Edition = _edition, ChatId = chatId, Now = now, MutedBefore = mutedBefore, AskedBefore = askedBefore }) == 1;
        }

        public async Task KeepAsync(long chatId, DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE bot_mutes SET leave_asked_at = @Now WHERE edition = @Edition AND telegram_chat_id = @ChatId",
                new { Edition = _edition, ChatId = chatId, Now = now });
        }

    }
}
