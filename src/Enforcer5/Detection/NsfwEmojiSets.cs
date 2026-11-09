using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Custom emoji packs found NSFW, for every group (Daniel, 2026-11-09): one of their emoji scored NSFW somewhere
    /// (<see cref="EmojiCheck"/>), or a dev marked them (/nsfwemoji). In a group with the NSFW check on, an emoji from such a
    /// pack is deleted and reported without scoring, and the pack is banned in that group. A dev's clear takes a pack off
    /// for every group, and the bot never marks it again (its emoji can still be flagged one by one).
    /// The list is small: kept in memory, read from PostgreSQL every <see cref="RefreshEvery"/>, which is when the other
    /// edition's finds show.
    /// </summary>
    internal static class NsfwEmojiSets
    {
        internal static TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

        private static readonly object Lock = new();
        private static Dictionary<string, NsfwEmojiSet> _sets = new(StringComparer.Ordinal);
        private static DateTime _freshUntil;
        private static Task _loading;

        /// <summary>The pack is NSFW (and not cleared).</summary>
        internal static async Task<bool> IsNsfwAsync(string setName) =>
            setName != null && (await GetAsync(setName)) is { Cleared: false };

        /// <summary>What is known about the pack; null when it was never marked.</summary>
        internal static async Task<NsfwEmojiSet> GetAsync(string setName)
        {
            await LoadAsync();
            lock (Lock) return setName != null && _sets.TryGetValue(setName, out var set) ? set : null;
        }

        /// <summary>
        /// Marks the pack NSFW. The bot's own mark (<paramref name="source"/> auto) never undoes a dev's clear. True when
        /// it was not marked before.
        /// </summary>
        internal static async Task<bool> MarkAsync(string setName, string emojiId, float? nsfw, string model, string source)
        {
            if (setName == null) return false;
            var before = await GetAsync(setName);
            if (before != null && (!before.Cleared || source != "dev")) return false;
            lock (Lock)
                _sets[setName] = new NsfwEmojiSet(setName, emojiId, nsfw, source, new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero), false);
            if (!PostgresStorage.Ready) return true;
            try
            {
                await Repositories.CustomEmoji.MarkNsfwAsync(setName, emojiId, nsfw, model, source);
            }
            catch (Exception e)
            {
                // This edition knows; the other finds it on its own.
                ErrorReporter.Report(e, $"Saving the NSFW emoji pack {setName}");
            }
            return true;
        }

        /// <summary>A dev says the pack is not NSFW. False when it was not marked (or was cleared already).</summary>
        internal static async Task<bool> ClearAsync(string setName, long devId)
        {
            var before = await GetAsync(setName);
            if (before == null || before.Cleared) return false;
            lock (Lock) _sets[setName] = before with { Cleared = true };
            if (PostgresStorage.Ready) await Repositories.CustomEmoji.ClearAsync(setName, devId);
            return true;
        }

        private static Task LoadAsync()
        {
            Task loading;
            lock (Lock)
            {
                if (Clock.UtcNow < _freshUntil || !PostgresStorage.Ready) return Task.CompletedTask;
                if (_loading == null || _loading.IsCompleted) _loading = ReadAsync();
                loading = _loading;
            }
            return loading;
        }

        private static async Task ReadAsync()
        {
            try
            {
                var rows = await Repositories.CustomEmoji.NsfwSetsAsync();
                var sets = new Dictionary<string, NsfwEmojiSet>(StringComparer.Ordinal);
                foreach (var row in rows) sets[row.SetName] = row;
                lock (Lock)
                {
                    _sets = sets;
                    _freshUntil = Clock.UtcNow + RefreshEvery;
                }
            }
            catch (Exception e)
            {
                // What is in memory stands; read again next time.
                ErrorReporter.Report(e, "Reading the NSFW emoji packs");
            }
        }

        internal static void ResetForTests()
        {
            lock (Lock)
            {
                _sets = new Dictionary<string, NsfwEmojiSet>(StringComparer.Ordinal);
                _freshUntil = DateTime.MinValue;
                _loading = null;
            }
        }
    }
}
