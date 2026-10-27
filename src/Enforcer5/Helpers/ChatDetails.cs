using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// A group's own details from getChat (its linked channel, above all), kept
    /// <see cref="FreshFor"/> and shared by everything that asks: channel posts, raid detection
    /// banning channels, mention lookups. One call at a time per group. Not for what must be
    /// current to the second, such as the permissions saved before a raid lock: that reads Telegram
    /// itself and hands the answer here (<see cref="Remember"/>).
    /// </summary>
    internal static class ChatDetails
    {
        internal static TimeSpan FreshFor = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

        private sealed class Entry
        {
            internal ChatFullInfo Info;
            internal DateTime FreshUntil;
            internal Task<ChatFullInfo> Loading;
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<long, Entry> Entries = new();

        /// <summary>The group's details; null when Telegram would not say (asked again after a minute).</summary>
        internal static async Task<ChatFullInfo> GetAsync(long chatId)
        {
            Task<ChatFullInfo> loading;
            lock (Lock)
            {
                Entries.TryGetValue(chatId, out var entry);
                if (entry != null && entry.FreshUntil > Clock.UtcNow) return entry.Info;
                if (entry == null) Entries[chatId] = entry = new Entry();
                loading = entry.Loading ??= LoadAsync(chatId);
            }
            return await loading;
        }

        /// <summary>The group's linked channel, 0 for none or not known.</summary>
        internal static async Task<long> LinkedChannelAsync(long chatId) => (await GetAsync(chatId))?.LinkedChatId ?? 0;

        /// <summary>Details just read from Telegram by someone else.</summary>
        internal static void Remember(ChatFullInfo info)
        {
            if (info == null) return;
            lock (Lock)
            {
                if (!Entries.TryGetValue(info.Id, out var entry)) Entries[info.Id] = entry = new Entry();
                entry.Info = info;
                entry.FreshUntil = Clock.UtcNow + FreshFor;
            }
        }

        private static async Task<ChatFullInfo> LoadAsync(long chatId)
        {
            await Task.Yield();
            ChatFullInfo info = null;
            try
            {
                info = await Bot.Api.GetChat(chatId);
            }
            catch (Exception e)
            {
                LogHelper.Error($"Could not read the details of {chatId}: {e.Message}");
            }
            lock (Lock)
            {
                var entry = Entries[chatId];
                entry.Loading = null;
                if (info != null) entry.Info = info;
                entry.FreshUntil = Clock.UtcNow + (info != null ? FreshFor : RetryAfterFailure);
                return entry.Info;
            }
        }

        /// <summary>Drops details not asked for in an hour; the raid check calls it.</summary>
        internal static void Prune()
        {
            var cut = Clock.UtcNow - TimeSpan.FromHours(1);
            lock (Lock)
                foreach (var chat in new List<long>(Entries.Keys))
                    if (Entries[chat].Loading == null && Entries[chat].FreshUntil < cut) Entries.Remove(chat);
        }

        internal static void ResetForTests()
        {
            lock (Lock) Entries.Clear();
        }
    }
}
