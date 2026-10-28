using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>A file banned in a group (<c>banned_media</c>).</summary>
    internal sealed record BannedFile(string MediaUniqueId, string MediaType, long? AddedBy, string Source, DateTimeOffset AddedAt);

    /// <summary>The files admins banned in each group (Detection step 12).</summary>
    internal interface IBannedMediaRepository
    {
        Task<IReadOnlyList<BannedFile>> ListAsync(long chatId);

        /// <summary>Bans the file in the group; keeps the newest <paramref name="limit"/>. False when it was banned already.</summary>
        Task<bool> AddAsync(long chatId, BannedFile file, int limit);

        Task<bool> RemoveAsync(long chatId, string mediaUniqueId);

        Task<int> ClearAsync(long chatId);
    }

    internal sealed class PostgresBannedMediaRepository : IBannedMediaRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresBannedMediaRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private const string Group = @"
group_id = (SELECT g.group_id FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId)";

        private sealed class Row
        {
            public string MediaUniqueId { get; set; }
            public string MediaType { get; set; }
            public long? AddedBy { get; set; }
            public string Source { get; set; }
            public DateTime AddedAt { get; set; }
        }

        public async Task<IReadOnlyList<BannedFile>> ListAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<Row>(@"
SELECT b.media_unique_id AS MediaUniqueId, b.media_type AS MediaType, a.telegram_user_id AS AddedBy, b.source AS Source,
       b.added_at AS AddedAt
FROM banned_media b LEFT JOIN telegram_accounts a ON a.account_id = b.added_by_account_id
WHERE b." + Group.Trim() + " ORDER BY b.added_at", new { ChatId = chatId });
            return rows.Select(r => new BannedFile(r.MediaUniqueId, r.MediaType, r.AddedBy, r.Source,
                new DateTimeOffset(DateTime.SpecifyKind(r.AddedAt, DateTimeKind.Utc)))).ToList();
        }

        public async Task<bool> AddAsync(long chatId, BannedFile file, int limit)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId,
                chatType: PostgresBanHistoryRepository.ChatType(chatId));
            long? by = file.AddedBy is { } admin ? (await PrincipalResolver.EnsureUserAsync(connection, transaction, admin)).AccountId : null;
            var added = await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO banned_media (group_id, media_unique_id, media_type, added_by_account_id, source, added_at)
VALUES (@GroupId, @Id, @Type, @By, @Source, @At) ON CONFLICT DO NOTHING",
                new { group.GroupId, Id = file.MediaUniqueId, Type = file.MediaType, By = by, file.Source, At = file.AddedAt.UtcDateTime },
                transaction));
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM banned_media WHERE group_id = @GroupId AND media_unique_id IN (
    SELECT media_unique_id FROM banned_media WHERE group_id = @GroupId ORDER BY added_at DESC OFFSET @Limit)",
                new { group.GroupId, Limit = limit }, transaction));
            await transaction.CommitAsync();
            return added == 1;
        }

        public async Task<bool> RemoveAsync(long chatId, string mediaUniqueId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync("DELETE FROM banned_media WHERE media_unique_id = @Id AND " + Group.Trim(),
                new { ChatId = chatId, Id = mediaUniqueId }) > 0;
        }

        public async Task<int> ClearAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await connection.ExecuteAsync("DELETE FROM banned_media WHERE " + Group.Trim(), new { ChatId = chatId });
        }
    }
}
