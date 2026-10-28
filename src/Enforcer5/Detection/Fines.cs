using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Members an admin marked fine on a report (design section 6): for <see cref="ExemptFor"/> the
    /// signs they were reported for neither count nor report again in that group. Kept in memory; the
    /// Fine labels in <c>member_labels</c> (their reason holds the signs) bring them back after a restart.
    /// </summary>
    internal static class Fines
    {
        internal static readonly TimeSpan ExemptFor = TimeSpan.FromDays(30);
        private const int MaxMembers = 100_000;

        private sealed class Entry
        {
            internal readonly Dictionary<string, DateTime> Until = new();
            internal DateTime? LastFine;
            internal bool Loaded;
        }

        private static readonly ConcurrentDictionary<(long Chat, long User), Entry> Entries = new();

        /// <summary>Marks them fine for <paramref name="signs"/> from now.</summary>
        internal static void Add(long chatId, long userId, IEnumerable<string> signs)
        {
            var now = Clock.UtcNow;
            if (Entries.Count > MaxMembers) Prune(now);
            var entry = Entries.GetOrAdd((chatId, userId), _ => new Entry());
            lock (entry)
            {
                foreach (var sign in signs) entry.Until[sign] = now + ExemptFor;
                entry.LastFine = now;
            }
        }

        /// <summary>Whether <paramref name="sign"/> is one an admin marked them fine for; loads what is stored first.</summary>
        internal static async Task<bool> ExemptAsync(long chatId, long userId, string sign)
        {
            var entry = await EntryAsync(chatId, userId);
            var now = Clock.UtcNow;
            lock (entry) return entry.Until.TryGetValue(sign, out var until) && until > now;
        }

        /// <summary>When an admin last marked them fine, if in the last <see cref="ExemptFor"/>: their score starts after it.</summary>
        internal static async Task<DateTime?> LastFineAsync(long chatId, long userId)
        {
            var entry = await EntryAsync(chatId, userId);
            lock (entry) return entry.LastFine;
        }

        private static async Task<Entry> EntryAsync(long chatId, long userId)
        {
            var entry = Entries.GetOrAdd((chatId, userId), _ => new Entry());
            bool loaded;
            lock (entry) loaded = entry.Loaded;
            if (loaded || !PostgresStorage.Ready) return entry;
            IReadOnlyList<(string Signs, DateTimeOffset At)> stored;
            try
            {
                stored = await Repositories.DetectionLog.FinesAsync(chatId, userId, Clock.UtcNow - ExemptFor);
            }
            catch (Exception e)
            {
                // Without them the member is only treated as not fined; asked again next time.
                ErrorReporter.Report(e, $"Loading the Fine decisions about {userId} in {chatId}");
                return entry;
            }
            lock (entry)
            {
                if (entry.Loaded) return entry;
                foreach (var (signs, at) in stored)
                {
                    var until = at.UtcDateTime + ExemptFor;
                    foreach (var sign in (signs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!entry.Until.TryGetValue(sign, out var known) || known < until) entry.Until[sign] = until;
                    if (entry.LastFine == null || entry.LastFine < at.UtcDateTime) entry.LastFine = at.UtcDateTime;
                }
                entry.Loaded = true;
            }
            return entry;
        }

        private static void Prune(DateTime now)
        {
            foreach (var (key, entry) in Entries)
                lock (entry)
                    if (entry.Loaded && entry.Until.Values.All(u => u <= now)) Entries.TryRemove(key, out _);
        }

        internal static void ResetForTests() => Entries.Clear();
    }
}
