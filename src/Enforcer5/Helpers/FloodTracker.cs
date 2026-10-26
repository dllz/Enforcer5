using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Counting for the group flood check (OnMessage.AntiFlood): how many messages someone sent in
    /// the current window, and which, so the flood can be deleted.
    /// <list type="bullet">
    /// <item>The count is <c>spam:{chat}:{user}</c> in Redis, incremented atomically, expiring
    /// <see cref="Window"/> after the first message of the window. Shared by both editions.</item>
    /// <item>The message ids are kept in memory, per process, for the same window: one edition
    /// serves a group, and losing them on a restart only means a flood is not deleted.</item>
    /// </list>
    /// </summary>
    internal static class FloodTracker
    {
        internal static readonly TimeSpan Window = TimeSpan.FromSeconds(6);
        private const int MaxKept = 100;

        private static readonly ConcurrentDictionary<(long Chat, long User), Queue<(int Id, DateTime At)>> RecentMessages = new();
        private static DateTime _nextPrune;

        /// <summary>Raised once per flood, with the chat and the user: raid detection listens.</summary>
        internal static event Action<long, long> Flooded;

        internal static void Seen(long chatId, long userId, int messageId)
        {
            var now = Clock.UtcNow;
            var queue = RecentMessages.GetOrAdd((chatId, userId), _ => new Queue<(int, DateTime)>());
            lock (queue)
            {
                queue.Enqueue((messageId, now));
                Trim(queue, now);
            }
            PruneIfDue(now);
        }

        /// <summary>The message ids someone sent in the current window, oldest first.</summary>
        internal static int[] Recent(long chatId, long userId)
        {
            if (!RecentMessages.TryGetValue((chatId, userId), out var queue)) return Array.Empty<int>();
            lock (queue)
            {
                Trim(queue, Clock.UtcNow);
                return queue.Select(m => m.Id).ToArray();
            }
        }

        /// <summary>Counts one more message in the window and returns the count.</summary>
        internal static async Task<long> CountAsync(long chatId, long userId)
        {
            var key = $"spam:{chatId}:{userId}";
            var db = Redis.db;
            var count = await db.StringIncrementAsync(key, 1, CommandFlags.None);
            // The window starts with its first message. Every tenth also sets it, which heals a
            // key whose expiry was lost (the process died between the two calls).
            if (count == 1 || count % 10 == 0)
                await db.KeyExpireAsync(key, Window, CommandFlags.None);
            return count;
        }

        internal static void OnFlood(long chatId, long userId)
        {
            try
            {
                Flooded?.Invoke(chatId, userId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Flood listener");
            }
        }

        private static void Trim(Queue<(int Id, DateTime At)> queue, DateTime now)
        {
            while (queue.Count > 0 && (queue.Count > MaxKept || now - queue.Peek().At > Window)) queue.Dequeue();
        }

        private static void PruneIfDue(DateTime now)
        {
            if (now < _nextPrune) return;
            _nextPrune = now.AddMinutes(1);
            foreach (var entry in RecentMessages)
            {
                lock (entry.Value)
                {
                    Trim(entry.Value, now);
                    if (entry.Value.Count == 0) RecentMessages.TryRemove(entry.Key, out _);
                }
            }
        }

        /// <summary>Forgets everything, for tests.</summary>
        internal static void ResetForTests() => RecentMessages.Clear();
    }
}
