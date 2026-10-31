using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Why a member is banned in a chat, shown by /status. Written by the Ban action when its caller gives a
    /// reason for the list, cleared by the Unban action.
    /// </summary>
    internal interface IBanListRepository
    {
        Task SetAsync(long chatId, long userId, string name, string why);

        /// <summary>The reason, or null.</summary>
        Task<string> GetReasonAsync(long chatId, long userId);

        Task ClearAsync(long chatId, long userId);
    }

    /// <summary>
    /// <c>chat:{chatId}:bannedlist:{userId}</c>, a hash with <c>why</c> and <c>nick</c>. The old code also
    /// set <c>chat:{chatId}:bannedlist</c> to the last user id as a string, and the unban removed from it as
    /// if it were a set (which Redis refuses); nothing read it, so it is no longer written.
    /// </summary>
    internal sealed class RedisBanListRepository : IBanListRepository
    {
        private readonly Func<IDatabaseAsync> _database;

        public RedisBanListRepository(Func<IDatabaseAsync> database) =>
            _database = database ?? throw new ArgumentNullException(nameof(database));

        private static string Key(long chatId, long userId) => $"chat:{chatId}:bannedlist:{userId}";

        public Task SetAsync(long chatId, long userId, string name, string why) =>
            _database().HashSetAsync(Key(chatId, userId), new[] { new HashEntry("why", why ?? ""), new HashEntry("nick", name ?? "") });

        public async Task<string> GetReasonAsync(long chatId, long userId)
        {
            var why = await _database().HashGetAsync(Key(chatId, userId), "why");
            return why.IsNullOrEmpty ? null : why.ToString();
        }

        public Task ClearAsync(long chatId, long userId) => _database().KeyDeleteAsync(Key(chatId, userId));
    }
}
