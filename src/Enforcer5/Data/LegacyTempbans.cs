using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// The old Redis tempban schedule, read once for the import into PostgreSQL (rollout step 13) and never written:
    /// <c>{index}</c> (<c>tempbanned</c>, premium <c>tempbannedPremium</c>), a hash of the end (Unix seconds) to
    /// <c>{chatId}:{userId}:{name}:{group}</c>. The per-group keys beside it (<c>chat:{id}:{index}</c>,
    /// <c>chat:{id}:tempbanned:{user}</c>) were checked on prod before this step (2026-11-06): no end keys left, and
    /// 4 members listed in 2 groups without one, which the old code did not count as tempbanned either; so the hash
    /// alone is read. Left in place, so a binary rollback finds it as it was.
    /// </summary>
    internal sealed class LegacyTempbans
    {
        private readonly Func<IDatabaseAsync> _database;

        internal LegacyTempbans(Func<IDatabaseAsync> database, string index)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            Index = index;
        }

        internal string Index { get; }

        /// <summary>Every entry; ones that cannot be read are left out.</summary>
        internal async Task<IReadOnlyList<TempbanEntry>> ReadAsync()
        {
            var list = new List<TempbanEntry>();
            foreach (var entry in await _database().HashGetAllAsync(Index, CommandFlags.None) ?? Array.Empty<HashEntry>())
            {
                if (long.TryParse(entry.Name.ToString(), out _) && TryParseEntry(entry.Value.ToString(), out var chatId, out var userId))
                    list.Add(new TempbanEntry(entry.Name.ToString(), chatId, userId));
            }
            return list;
        }

        /// <summary>Chat and user from a hash value, <c>{chatId}:{userId}:{name}:{group}</c>.</summary>
        internal static bool TryParseEntry(string value, out long chatId, out long userId)
        {
            chatId = 0;
            userId = 0;
            var parts = value?.Split(':');
            return parts != null && parts.Length >= 2 &&
                   long.TryParse(parts[0], out chatId) && long.TryParse(parts[1], out userId);
        }
    }
}
