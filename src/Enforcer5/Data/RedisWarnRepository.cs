using System;
using System.Threading.Tasks;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// The existing warn hashes: <c>chat:{chatId}:warns</c>, <c>chat:{chatId}:prewarns</c> and
    /// <c>chat:{chatId}:mediawarn</c>, each mapping user id to count.
    /// </summary>
    internal sealed class RedisWarnRepository : IWarnRepository
    {
        // Resolved per call: the connection is only established by Redis.Start(), after this
        // repository has been constructed.
        private readonly Func<IDatabaseAsync> _database;

        public RedisWarnRepository(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public async Task<long> GetCountAsync(long chatId, WarnKind kind, long userId)
        {
            var value = await _database().HashGetAsync(Key(chatId, kind), userId, CommandFlags.None);
            return value.TryParse(out long count) && count > 0 ? count : 0;
        }

        public async Task<long> RemoveOneAsync(long chatId, WarnKind kind, long userId)
        {
            var key = Key(chatId, kind);
            var left = await _database().HashDecrementAsync(key, userId, 1, CommandFlags.None);
            if (left >= 0) return left;

            // There was nothing to take off; put it back to 0 rather than leave a negative count
            // that would cancel out the user's next warn.
            await _database().HashSetAsync(key, userId, 0, When.Always, CommandFlags.None);
            return 0;
        }

        public Task ResetAsync(long chatId, WarnKind kind, long userId) =>
            _database().HashDeleteAsync(Key(chatId, kind), userId, CommandFlags.None);

        private static RedisKey Key(long chatId, WarnKind kind) => kind switch
        {
            WarnKind.Warn => $"chat:{chatId}:warns",
            WarnKind.Prewarn => $"chat:{chatId}:prewarns",
            WarnKind.Media => $"chat:{chatId}:mediawarn",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
