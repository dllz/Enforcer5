using System;
using System.Collections.Generic;
using StackExchange.Redis;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// A busy group's <c>chat:{id}:settings</c> hash, kept for <see cref="SettingsFor"/>: a read per
    /// message is a read per <see cref="SettingsFor"/> during a raid. (The automatic checks' settings
    /// are in memory already; see Detection/DetectionSettings.) Only a group with more than <see cref="BusyMessages"/>
    /// messages in the last <see cref="SettingsFor"/> is served from the snapshot, so in a normal
    /// group a change of settings applies to the very next message, as before; in a busy one, within
    /// <see cref="SettingsFor"/>. The per-member watch list is always read.
    /// </summary>
    internal static class GroupSettings
    {
        internal static TimeSpan SettingsFor = TimeSpan.FromSeconds(10);
        internal static int BusyMessages = 20;

        internal sealed record Snapshot(HashEntry[] Settings);

        private sealed class Entry
        {
            internal Snapshot Value;
            internal DateTime TakenAt;
            internal DateTime WindowStart;
            internal int Messages;
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<long, Entry> Entries = new();

        /// <summary>The snapshot to use for this message, or null to read Redis. Counts the message.</summary>
        internal static Snapshot Get(long chatId)
        {
            var now = Clock.UtcNow;
            lock (Lock)
            {
                if (!Entries.TryGetValue(chatId, out var entry))
                {
                    if (Entries.Count > 50_000) Entries.Clear();
                    Entries[chatId] = entry = new Entry { WindowStart = now };
                }
                if (now - entry.WindowStart >= SettingsFor)
                {
                    entry.WindowStart = now;
                    entry.Messages = 0;
                }
                entry.Messages++;
                var busy = entry.Messages > BusyMessages;
                return busy && entry.Value != null && now - entry.TakenAt < SettingsFor ? entry.Value : null;
            }
        }

        /// <summary>What was just read from Redis for <paramref name="chatId"/>.</summary>
        internal static void Store(long chatId, Snapshot value)
        {
            lock (Lock)
            {
                if (!Entries.TryGetValue(chatId, out var entry)) return;
                entry.Value = value;
                entry.TakenAt = Clock.UtcNow;
            }
        }

        internal static void ResetForTests()
        {
            lock (Lock) Entries.Clear();
        }
    }
}
