using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Redis layout. These keys predate the repository and are shared by both editions.
    /// <list type="bullet">
    /// <item><c>chat:{chatId}:muted</c>: set of user ids muted with /mute.</item>
    /// <item><c>chat:{chatId}:mutedJoiners</c>: set of user ids muted by Mute On Join.</item>
    /// <item><c>chat:{chatId}:tempmuted</c>: hash of user id to the tempmute's end, in Unix seconds.</item>
    /// </list>
    /// </summary>
    internal sealed class RedisMuteRepository : IMuteRepository
    {
        // Resolved per call: the connection only exists once Redis.Start() has run.
        private readonly Func<IDatabaseAsync> _database;

        public RedisMuteRepository(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public Task MarkMutedAsync(long chatId, long userId)
        {
            var db = _database();
            return Task.WhenAll(
                db.SetAddAsync(MutedKey(chatId), userId),
                db.SetRemoveAsync(JoinersKey(chatId), userId),
                db.HashDeleteAsync(TempMutedKey(chatId), userId));
        }

        public Task MarkTempMutedAsync(long chatId, long userId, DateTime untilUtc)
        {
            var db = _database();
            return Task.WhenAll(
                db.HashSetAsync(TempMutedKey(chatId), userId, untilUtc.ToUnixTime()),
                db.SetRemoveAsync(MutedKey(chatId), userId),
                db.SetRemoveAsync(JoinersKey(chatId), userId));
        }

        public async Task MarkJoinerMutedAsync(long chatId, long userId)
        {
            var db = _database();
            if (await db.SetContainsAsync(MutedKey(chatId), userId)) return;
            await Task.WhenAll(
                db.SetAddAsync(JoinersKey(chatId), userId),
                db.HashDeleteAsync(TempMutedKey(chatId), userId));
        }

        public Task ClearAsync(long chatId, long userId)
        {
            var db = _database();
            return Task.WhenAll(
                db.SetRemoveAsync(MutedKey(chatId), userId),
                db.SetRemoveAsync(JoinersKey(chatId), userId),
                db.HashDeleteAsync(TempMutedKey(chatId), userId));
        }

        public Task<IReadOnlyList<long>> GetMutedAsync(long chatId) => ReadSetAsync(MutedKey(chatId), chatId);

        public Task<IReadOnlyList<long>> GetMutedJoinersAsync(long chatId) => ReadSetAsync(JoinersKey(chatId), chatId);

        public async Task<IReadOnlyList<TimedRestriction>> GetTempMutedAsync(long chatId, DateTime nowUtc)
        {
            var db = _database();
            var key = TempMutedKey(chatId);
            var entries = await db.HashGetAllAsync(key);
            var running = new List<TimedRestriction>(entries.Length);
            var ended = new List<RedisValue>();
            var now = nowUtc.ToUnixTime();
            foreach (var entry in entries)
            {
                if (long.TryParse(entry.Name.ToString(), out var userId) &&
                    long.TryParse(entry.Value.ToString(), out var until) && until > now)
                    running.Add(new TimedRestriction(userId, DateTimeOffset.FromUnixTimeSeconds(until).UtcDateTime));
                else
                    ended.Add(entry.Name);
            }
            // Telegram lifts a tempmute on its own and nothing else ever removed these entries.
            if (ended.Count > 0) await db.HashDeleteAsync(key, ended.ToArray());
            return running;
        }

        private async Task<IReadOnlyList<long>> ReadSetAsync(RedisKey key, long chatId)
        {
            var members = await _database().SetMembersAsync(key);
            var ids = new List<long>(members.Length);
            foreach (var member in members)
            {
                if (long.TryParse(member.ToString(), out var id)) ids.Add(id);
                else LogHelper.Error($"Ignoring unreadable entry '{member}' in {key} for {chatId}");
            }
            return ids;
        }

        private static RedisKey MutedKey(long chatId) => $"chat:{chatId}:muted";
        private static RedisKey JoinersKey(long chatId) => $"chat:{chatId}:mutedJoiners";
        private static RedisKey TempMutedKey(long chatId) => $"chat:{chatId}:tempmuted";
    }
}
