using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Each group's admins, from one getChatAdministrators call shared by everything that asks:
    /// admin checks for commands and buttons, raid detection, tagging the admins, @admin reports and
    /// the admin list. Kept for <see cref="FreshFor"/> only, because a newly promoted admin must be
    /// one within the minute; and kept current in between from the member updates Telegram sends
    /// when someone is promoted or demoted (<see cref="Promoted"/>, <see cref="Demoted"/>). One call
    /// at a time per group: everyone asking while it runs waits for the same answer.
    /// <para>
    /// When Telegram does not answer, the last list is used (and asked again after
    /// <see cref="RetryAfterFailure"/>); with no list at all the caller hears null and falls back to
    /// asking about the one member.
    /// </para>
    /// </summary>
    internal static class AdminDirectory
    {
        internal static TimeSpan FreshFor = TimeSpan.FromSeconds(60);
        internal static TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

        private sealed class Entry
        {
            internal List<ChatMember> Admins;
            internal DateTime FreshUntil;
            internal Task<List<ChatMember>> Loading;
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<long, Entry> Entries = new();
        private static int _calls;

        /// <summary>getChatAdministrators calls made, for tests.</summary>
        internal static int Calls => _calls;

        /// <summary>The group's admins (owner included); null when Telegram has never answered.</summary>
        internal static async Task<IReadOnlyList<ChatMember>> GetAsync(long chatId)
        {
            Task<List<ChatMember>> loading;
            lock (Lock)
            {
                Entries.TryGetValue(chatId, out var entry);
                if (entry?.Admins != null && entry.FreshUntil > Clock.UtcNow) return entry.Admins;
                if (entry == null) Entries[chatId] = entry = new Entry();
                loading = entry.Loading ??= LoadAsync(chatId);
            }
            return await loading;
        }

        /// <summary>Blocking <see cref="GetAsync"/>, for the synchronous command code.</summary>
        internal static IReadOnlyList<ChatMember> Get(long chatId) => GetAsync(chatId).GetAwaiter().GetResult();

        /// <summary>True or false when the list is known, null when Telegram has never answered for the group.</summary>
        internal static async Task<bool?> IsAdminAsync(long chatId, long userId) =>
            await GetAsync(chatId) is { } admins ? admins.Any(a => a.User.Id == userId) : null;

        /// <summary>From the list at hand only, however old, never a lookup: for paths that must not wait.</summary>
        internal static bool IsKnownAdmin(long chatId, long userId)
        {
            lock (Lock)
                return Entries.TryGetValue(chatId, out var entry) && entry.Admins != null && entry.Admins.Any(a => a.User.Id == userId);
        }

        private static async Task<List<ChatMember>> LoadAsync(long chatId)
        {
            // Off the caller's stack, so the lock is never held across the call.
            await Task.Yield();
            List<ChatMember> admins = null;
            System.Threading.Interlocked.Increment(ref _calls);
            try
            {
                admins = (await Bot.Api.GetChatAdministrators(chatId)).ToList();
            }
            catch (Exception e)
            {
                LogHelper.Error($"Could not list the admins of {chatId}: {e.Message}");
            }
            lock (Lock)
            {
                var entry = Entries[chatId];
                entry.Loading = null;
                if (admins != null)
                {
                    entry.Admins = admins;
                    entry.FreshUntil = Clock.UtcNow + FreshFor;
                }
                else
                {
                    // The last list it had, for a little longer; null if there never was one.
                    entry.FreshUntil = Clock.UtcNow + RetryAfterFailure;
                }
                return entry.Admins;
            }
        }

        /// <summary>Telegram says <paramref name="member"/> is now an admin (or the owner) of the group.</summary>
        internal static void Promoted(long chatId, ChatMember member)
        {
            lock (Lock)
            {
                if (!Entries.TryGetValue(chatId, out var entry) || entry.Admins == null) return;
                entry.Admins = entry.Admins.Where(a => a.User.Id != member.User.Id).Append(member).ToList();
            }
        }

        /// <summary>Telegram says <paramref name="userId"/> is no longer an admin of the group.</summary>
        internal static void Demoted(long chatId, long userId)
        {
            lock (Lock)
            {
                if (Entries.TryGetValue(chatId, out var entry) && entry.Admins != null)
                    entry.Admins = entry.Admins.Where(a => a.User.Id != userId).ToList();
            }
        }

        /// <summary>Forgets the group's list: asked again next time (the bot's own rights changed).</summary>
        internal static void Forget(long chatId)
        {
            lock (Lock)
                if (Entries.TryGetValue(chatId, out var entry)) entry.FreshUntil = DateTime.MinValue;
        }

        /// <summary>Drops lists not used for an hour; the raid check calls it.</summary>
        internal static void Prune()
        {
            var cut = Clock.UtcNow - TimeSpan.FromHours(1);
            lock (Lock)
                foreach (var chat in Entries.Where(e => e.Value.Loading == null && e.Value.FreshUntil < cut).Select(e => e.Key).ToList())
                    Entries.Remove(chat);
        }

        /// <summary>Forgets everything, for tests.</summary>
        internal static void ResetForTests()
        {
            lock (Lock) Entries.Clear();
            _calls = 0;
        }
    }
}
