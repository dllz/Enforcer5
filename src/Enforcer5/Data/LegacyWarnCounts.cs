using System;
using System.Threading.Tasks;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// The warn counts as Redis held them until rollout step 12: <c>chat:{chatId}:warns</c>,
    /// <c>chat:{chatId}:prewarns</c> and <c>chat:{chatId}:mediawarn</c>, each mapping user id to count, and
    /// <c>chat:{chatId}:totalWarns</c>, the lifetime count of warns. Read only, and only for a member PostgreSQL has no
    /// balance for yet: that member's first change starts from here (the import, member by member). Nothing writes
    /// these hashes any more, so a binary rollback finds them as they were at the deploy.
    /// </summary>
    internal sealed class LegacyWarnCounts
    {
        // Resolved per call: the connection is only established by Redis.Start().
        private readonly Func<IDatabaseAsync> _database;

        internal LegacyWarnCounts(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// The count of <paramref name="kind"/> and its lifetime count. Negative counts (old code left some) read as 0.
        /// Redis kept a lifetime count (<c>totalWarns</c>) for warns only, as it was (0 when missing; /user showed the
        /// count then); for the others the lifetime count is the count itself.
        /// </summary>
        internal async Task<(long Active, long Lifetime)> ReadAsync(long chatId, WarnKind kind, long userId)
        {
            var db = _database();
            var active = Positive(await db.HashGetAsync(Key(chatId, kind), userId, CommandFlags.None));
            if (kind != WarnKind.Warn) return (active, active);
            var lifetime = Positive(await db.HashGetAsync($"chat:{chatId}:totalWarns", userId, CommandFlags.None));
            return (active, lifetime);
        }

        private static long Positive(RedisValue value) => value.TryParse(out long count) && count > 0 ? count : 0;

        internal static string Key(long chatId, WarnKind kind) => kind switch
        {
            WarnKind.Warn => $"chat:{chatId}:warns",
            WarnKind.Prewarn => $"chat:{chatId}:prewarns",
            WarnKind.Media => $"chat:{chatId}:mediawarn",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
