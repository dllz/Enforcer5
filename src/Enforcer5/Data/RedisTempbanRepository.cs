using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// The legacy tempban keys. <c>{index}</c> is
    /// <c>tempbanned</c> for the normal edition and <c>tempbannedPremium</c> for premium.
    /// <list type="bullet">
    /// <item><c>{index}</c>: global hash of the unban time (Unix seconds) to
    /// <c>{chatId}:{userId}:{name}:{group}</c>. The expiry checker works through it. Two tempbans
    /// ending in the same second collide, and entries can outlive their tempban.</item>
    /// <item><c>chat:{chatId}:{index}</c>: set of user ids tempbanned in the chat.</item>
    /// <item><c>chat:{chatId}:tempbanned:{userId}</c>: the unban time, expiring with the tempban.
    /// Not split by edition.</item>
    /// </list>
    /// Neither store is complete on its own, so both are read and merged. The per-chat expiry wins
    /// because each new tempban rewrites it; the global hash fills in users it lacks.
    /// </summary>
    internal sealed class RedisTempbanRepository : ITempbanRepository
    {
        private readonly Func<IDatabaseAsync> _database;
        private readonly string _index;

        public RedisTempbanRepository(Func<IDatabaseAsync> database, string index)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _index = string.IsNullOrEmpty(index) ? throw new ArgumentException("index is required", nameof(index)) : index;
        }

        public async Task<IReadOnlyList<TimedRestriction>> GetActiveAsync(long chatId, DateTime nowUtc)
        {
            var db = _database();
            var membersTask = db.SetMembersAsync($"chat:{chatId}:{_index}");
            var globalTask = db.HashGetAllAsync(_index);
            await Task.WhenAll(membersTask, globalTask);

            var users = membersTask.Result
                .Select(m => long.TryParse(m.ToString(), out var id) ? id : 0)
                .Where(id => id != 0)
                .Distinct()
                .ToList();
            var expiries = await Task.WhenAll(users.Select(u => db.StringGetAsync($"chat:{chatId}:tempbanned:{u}")));

            var until = new Dictionary<long, long>();
            for (var i = 0; i < users.Count; i++)
            {
                if (long.TryParse(expiries[i].ToString(), out var seconds)) until[users[i]] = seconds;
            }

            var fromGlobal = new Dictionary<long, long>();
            foreach (var entry in globalTask.Result)
            {
                if (!long.TryParse(entry.Name.ToString(), out var seconds)) continue;
                if (!TryParseEntry(entry.Value.ToString(), out var entryChat, out var userId) || entryChat != chatId) continue;
                if (!fromGlobal.TryGetValue(userId, out var latest) || seconds > latest) fromGlobal[userId] = seconds;
            }
            foreach (var pair in fromGlobal)
            {
                until.TryAdd(pair.Key, pair.Value);
            }

            var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var latestRepresentable = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
            return until
                .Where(p => p.Value > now && p.Value <= latestRepresentable)
                .OrderBy(p => p.Value)
                .Select(p => new TimedRestriction(p.Key, DateTimeOffset.FromUnixTimeSeconds(p.Value).UtcDateTime))
                .ToList();
        }

        public async Task<long> ScheduleAsync(long chatId, long userId, DateTime untilUtc, DateTime nowUtc, string label)
        {
            var db = _database();
            var slot = Unix(untilUtc);
            var value = $"{chatId}:{userId}:{label}";
            // Two tempbans ending in the same second share a slot, so the later one moves on a second.
            // Claimed with HSETNX: a read then a write let two tempbans running side by side take the same
            // slot, and the one overwritten was never lifted.
            while (!await db.HashSetAsync(_index, slot, value, When.NotExists)) slot++;
            var ttl = untilUtc - nowUtc;
            await db.StringSetAsync($"chat:{chatId}:tempbanned:{userId}", slot, ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(1));
            await db.SetAddAsync($"chat:{chatId}:{_index}", userId);
            return slot;
        }

        public async Task CancelAsync(long chatId, long userId)
        {
            var db = _database();
            var expiryKey = $"chat:{chatId}:tempbanned:{userId}";
            var listed = db.SetContainsAsync($"chat:{chatId}:{_index}", userId);
            var expiry = db.StringGetAsync(expiryKey);
            await Task.WhenAll(listed, expiry);
            if (!listed.Result && !expiry.Result.HasValue) return;
            // The whole schedule is read (about a hundred entries): the expiry key only names the latest
            // tempban, and an older entry left behind would unban them when it ends.
            foreach (var entry in await db.HashGetAllAsync(_index))
            {
                if (TryParseEntry(entry.Value.ToString(), out var entryChat, out var entryUser) &&
                    entryChat == chatId && entryUser == userId)
                    await db.HashDeleteAsync(_index, entry.Name);
            }
            await db.SetRemoveAsync($"chat:{chatId}:{_index}", userId);
            await db.KeyDeleteAsync(expiryKey);
        }

        public async Task<IReadOnlyList<TempbanEntry>> DueAsync(DateTime nowUtc)
        {
            var now = Unix(nowUtc);
            var due = new List<TempbanEntry>();
            foreach (var entry in await _database().HashGetAllAsync(_index))
            {
                if (long.TryParse(entry.Name.ToString(), out var seconds) && seconds <= now &&
                    TryParseEntry(entry.Value.ToString(), out var chatId, out var userId))
                    due.Add(new TempbanEntry(entry.Name.ToString(), chatId, userId));
            }
            return due;
        }

        public async Task RemoveAsync(TempbanEntry entry)
        {
            var db = _database();
            await db.HashDeleteAsync(_index, entry.Slot);
            await db.SetRemoveAsync($"chat:{entry.ChatId}:{_index}", entry.UserId);
        }

        private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        /// <summary>Chat and user from a global hash value, <c>{chatId}:{userId}:{name}:{group}</c>.</summary>
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
