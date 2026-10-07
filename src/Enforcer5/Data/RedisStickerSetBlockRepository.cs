using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Redis-backed sticker pack blocklist: one set per chat at <c>chat:{chatId}:blockedstickersets</c>
    /// whose members are the lowercase pack names.
    /// </summary>
    internal sealed class RedisStickerSetBlockRepository : IStickerSetBlockRepository
    {
        // Resolved per call: the connection is only established by Redis.Start(), after this
        // repository has been constructed.
        private readonly Func<IDatabaseAsync> _database;

        public RedisStickerSetBlockRepository(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public async Task<IReadOnlyList<string>> GetAsync(long chatId)
        {
            var members = await _database().SetMembersAsync(Key(chatId));
            var names = new List<string>(members.Length);
            foreach (var member in members)
            {
                var name = (string)member;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names;
        }

        public Task<long> CountAsync(long chatId) => _database().SetLengthAsync(Key(chatId));

        public Task<bool> ContainsAsync(long chatId, string setName) =>
            _database().SetContainsAsync(Key(chatId), Member(setName));

        public Task<bool> AddAsync(long chatId, string setName) =>
            _database().SetAddAsync(Key(chatId), Member(setName));

        public Task<bool> RemoveAsync(long chatId, string setName) =>
            _database().SetRemoveAsync(Key(chatId), Member(setName));

        private static RedisKey Key(long chatId) => $"chat:{chatId}:blockedstickersets";

        private static RedisValue Member(string setName)
        {
            if (string.IsNullOrEmpty(setName)) throw new ArgumentException("A pack name is required.", nameof(setName));
            return setName;
        }
    }
}
