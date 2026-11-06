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

        /// <summary>The most hits kept per member: past it, repeats of a sign that counted already are only in <c>detections</c>.</summary>
        internal const int MaxHits = 50;

        /// <param name="MessageId">The message it was seen on, if any: the report links each sign to it.</param>
        /// <param name="Detail">What the report says about it (<see cref="SignHit.Detail"/>); loaded back from <c>detections</c>.</param>
        /// <param name="Deleted">The message was deleted: the report does not link to it.</param>
        /// <param name="Text">The message's text (<see cref="FlaggedText"/>), for Show messages before it is written.</param>
        internal sealed record Hit(string Sign, int Weight, DateTime At, long? MessageId = null, string Detail = null, bool Deleted = false,
            string Text = null);

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
        internal static Change Add(long chatId, long userId, string sign, int weight, long? messageId = null, string detail = null,
            string text = null)
        {
            var now = Clock.UtcNow;
            if (Entries.Count > MaxMembers) Prune(now);
            var entry = Entries.GetOrAdd((chatId, userId), _ => new Entry());
            lock (entry)
            {
                entry.Hits.RemoveAll(h => now - h.At >= Window);
                var before = entry.Hits.Sum(h => h.Weight);
                if (entry.Hits.Any(h => h.Sign == sign && h.At.Date == now.Date))
                {
                    // Counted already today; its message is still one the report can show.
                    if (text != null && entry.Hits.Count < MaxHits) entry.Hits.Add(new Hit(sign, 0, now, messageId, detail, Text: text));
                    return new Change(before, before, false);
                }
                entry.Hits.Add(new Hit(sign, weight, now, messageId, detail, Text: text));
                return new Change(before, before + weight, true);
            }
        }

        /// <summary>Detection deleted <paramref name="messageId"/>: the hits seen on it say so.</summary>
        internal static void MarkDeleted(long chatId, long userId, long messageId)
        {
            if (!Entries.TryGetValue((chatId, userId), out var entry)) return;
            lock (entry)
                for (var i = 0; i < entry.Hits.Count; i++)
                    if (entry.Hits[i].MessageId == messageId) entry.Hits[i] = entry.Hits[i] with { Deleted = true };
        }

        /// <summary>The signs that make up their score now, oldest first.</summary>
        internal static IReadOnlyList<string> Signs(long chatId, long userId)
        {
            if (!Entries.TryGetValue((chatId, userId), out var entry)) return Array.Empty<string>();
            var now = Clock.UtcNow;
            lock (entry) return entry.Hits.Where(h => now - h.At < Window).Select(h => h.Sign).Distinct().ToList();
        }

        /// <summary>
        /// The signs that make up their score now, oldest first, each with the last message it was seen on (null when
        /// none: a join), whether Detection deleted that message, and its last detail (null when none, or loaded back
        /// after a restart).
        /// </summary>
        internal static IReadOnlyList<(string Sign, long? MessageId, bool Deleted, string Detail)> Seen(long chatId, long userId)
        {
            if (!Entries.TryGetValue((chatId, userId), out var entry)) return Array.Empty<(string, long?, bool, string)>();
            var now = Clock.UtcNow;
            lock (entry)
                return entry.Hits.Where(h => now - h.At < Window).GroupBy(h => h.Sign)
                    .Select(g =>
                    {
                        var seenOn = g.LastOrDefault(h => h.MessageId != null);
                        return (g.Key, seenOn?.MessageId, seenOn?.Deleted == true, g.LastOrDefault(h => h.Detail != null)?.Detail);
                    })
                    .ToList();
        }

        /// <summary>The hits of the last <see cref="Window"/> with a message text, oldest first: Show messages before they are written.</summary>
        internal static IReadOnlyList<Hit> WithText(long chatId, long userId)
        {
            if (!Entries.TryGetValue((chatId, userId), out var entry)) return Array.Empty<Hit>();
            var now = Clock.UtcNow;
            lock (entry) return entry.Hits.Where(h => now - h.At < Window && h.Text != null).ToList();
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
