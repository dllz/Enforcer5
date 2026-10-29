using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>The admin chats groups linked for their Detection reports, from the groups' <c>engine</c> settings.</summary>
    internal interface IAdminChatRepository
    {
        /// <summary>Every linked admin chat with the group it belongs to.</summary>
        Task<IReadOnlyList<(long Group, long AdminChat)>> AllAsync();
    }

    internal sealed class PostgresAdminChatRepository : IAdminChatRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresAdminChatRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        public async Task<IReadOnlyList<(long Group, long AdminChat)>> AllAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(long, long)>(@"
SELECT c.telegram_chat_id, (p.settings->>'admin_chat_id')::bigint
FROM detection_policies p
JOIN groups g ON g.group_id = p.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE p.check_key = @Engine AND p.settings->>'admin_chat_id' IS NOT NULL",
                new { Engine = PostgresDetectionSettingsRepository.Engine });
            return rows.ToList();
        }
    }
}
