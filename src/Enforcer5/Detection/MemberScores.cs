using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Enforcer5.Helpers;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Each member's suspicion score per group: the weights of the signs they set off in the last
    /// <see cref="Window"/>, each sign counting once per day (design section 3). Kept in memory; after a
    /// restart a member's is rebuilt from <c>detections</c> when their next sign fires.
    /// </summary>
    internal static class MemberScores
    {
        internal static readonly TimeSpan Window = TimeSpan.FromHours(24);
        private const int MaxMembers = 200_000;

        internal sealed record Hit(string Sign, int Weight, DateTime At);

        private sealed class Entry
        {
            internal readonly List<Hit> Hits = new();
            internal bool Loaded;
        }

        private static readonly ConcurrentDictionary<(long Chat, long User), Entry> Entries = new();

        /// <summary>The result of adding a sign.</summary>
        /// <param name="Counted">False when the sign already counted for them today.</param>
        internal sealed record Change(int Before, int After, bool Counted);

        /// <summary>True when nothing is known about the member yet (not even "nothing"): a restart, or new.</summary>
        internal static bool NeedsLoad(long chatId, long userId) =>
            !Entries.TryGetValue((chatId, userId), out var entry) || !entry.Loaded;

        /// <summary>What <c>detections</c> held for them, after a restart. Hits already here are kept.</summary>
        internal static void Load(long chatId, long userId, IEnumerable<Hit> stored)
        {
            var entry = Entries.GetOrAdd((chatId, userId), _ => new Entry());
            lock (entry)
            {
                if (entry.Loaded) return;
                foreach (var hit in stored)
                    if (!entry.Hits.Any(h => h.Sign == hit.Sign && h.At.Date == hit.At.Date))
                        entry.Hits.Add(hit);
                entry.Loaded = true;
            }
        }

        /// <summary>Adds <paramref name="sign"/> unless it already counted for them today.</summary>
        internal static Change Add(long chatId, long userId, string sign, int weight)
        {
            var now = Clock.UtcNow;
            if (Entries.Count > MaxMembers) Prune(now);
            var entry = Entries.GetOrAdd((chatId, userId), _ => new Entry());
            lock (entry)
            {
                entry.Hits.RemoveAll(h => now - h.At >= Window);
                var before = entry.Hits.Sum(h => h.Weight);
                if (entry.Hits.Any(h => h.Sign == sign && h.At.Date == now.Date))
                    return new Change(before, before, false);
                entry.Hits.Add(new Hit(sign, weight, now));
                return new Change(before, before + weight, true);
            }
        }

        /// <summary>The signs that make up their score now, oldest first.</summary>
        internal static IReadOnlyList<string> Signs(long chatId, long userId)
        {
            if (!Entries.TryGetValue((chatId, userId), out var entry)) return Array.Empty<string>();
            var now = Clock.UtcNow;
            lock (entry) return entry.Hits.Where(h => now - h.At < Window).Select(h => h.Sign).Distinct().ToList();
        }

        internal static int Of(long chatId, long userId)
        {
            if (!Entries.TryGetValue((chatId, userId), out var entry)) return 0;
            var now = Clock.UtcNow;
            lock (entry) return entry.Hits.Where(h => now - h.At < Window).Sum(h => h.Weight);
        }

        /// <summary>Forgets the member's score (an admin said they are fine, or they became established).</summary>
        internal static void Clear(long chatId, long userId) => Entries.TryRemove((chatId, userId), out _);

        /// <summary>
        /// An admin marked them fine: their score starts again from nothing, and what <c>detections</c>
        /// holds from before is not loaded back (the engine starts their score after the Fine).
        /// </summary>
        internal static void Forgive(long chatId, long userId)
        {
            var entry = new Entry { Loaded = true };
            Entries[(chatId, userId)] = entry;
        }

        private static void Prune(DateTime now)
        {
            foreach (var (key, entry) in Entries)
            {
                lock (entry)
                    if (entry.Hits.All(h => now - h.At >= Window)) Entries.TryRemove(key, out _);
            }
        }

        internal static void ResetForTests() => Entries.Clear();
    }
}
