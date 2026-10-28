using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using StackExchange.Redis;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Banned media (design section 3, build step 12). A file an admin banned in a group is deleted when anyone but an
    /// admin or an approved member posts it again (or edits a post into it): the <c>banned_media</c> sign, by the file's
    /// unique id, so the same image, sticker, GIF or video from any account. A file is banned when an admin
    /// <list type="bullet">
    /// <item>bans or kicks someone with a command replying to it (<see cref="Decisions"/>),</item>
    /// <item>presses Ban on a Detection report about a post that had it (the post's record from <see cref="EditCheck"/>),</item>
    /// <item>or replies to it with /banmedia.</item>
    /// </list>
    /// Live from day one, since an admin chose it, also while the group only watches. Each group's list is kept in memory
    /// (read from PostgreSQL once and every <see cref="RefreshEvery"/>, which is when the other edition's additions show),
    /// so a media message costs a lookup in a set and nothing else. At most <see cref="PerGroup"/> files a group, the
    /// oldest dropped. Never the file itself.
    /// </summary>
    internal static class BannedMedia
    {
        internal const int PerGroup = 2000;
        internal static TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

        private sealed class Entry
        {
            internal readonly List<string> Order = new();
            internal readonly HashSet<string> Files = new(StringComparer.Ordinal);
            internal DateTime FreshUntil;
            internal bool Loaded;
            internal Task Loading;
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<long, Entry> Groups = new();

        // ------------------------------------------------------------------ the check

        /// <summary>A group message or edit, on the moderation path. Never waits.</summary>
        internal static void OnMessage(MessageContext ctx)
        {
            try
            {
                var message = ctx.Message;
                if (ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
                var file = MessageFingerprint.MediaIdOf(message);
                if (file == null || AdminDirectory.IsKnownAdmin(ctx.ChatId, ctx.UserId)) return;
                var chatId = ctx.ChatId;
                var userId = ctx.UserId;
                switch (Contains(chatId, file))
                {
                    case false:
                        return;
                    case true:
                        Bot.Dispatch(() => HitAsync(chatId, userId, message, file), "BannedMedia");
                        return;
                    default:
                        // The group's list is not in memory yet: read it, then look.
                        Bot.Dispatch(async () =>
                        {
                            await LoadAsync(chatId);
                            if (Contains(chatId, file) == true) await HitAsync(chatId, userId, message, file);
                        }, "BannedMedia.Load");
                        return;
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Banned media in {ctx.ChatId}");
            }
        }

        private static Task HitAsync(long chatId, long userId, Message message, string file) =>
            DetectionEngine.HitAsync(chatId, userId, message, new SignHit(SignCatalog.BannedMedia.Name,
                JsonSerializer.Serialize(new { type = MessageFingerprint.ContentTypeOf(message) })));

        /// <summary>Whether the file is banned in the group; null when the list has not been read (it is being read now).</summary>
        internal static bool? Contains(long chatId, string file)
        {
            lock (Lock)
            {
                Groups.TryGetValue(chatId, out var entry);
                if (entry == null || !entry.Loaded)
                {
                    StartLoad(chatId);
                    return null;
                }
                if (entry.FreshUntil <= Clock.UtcNow) StartLoad(chatId);
                return entry.Files.Contains(file);
            }
        }

        // Under Lock.
        private static Task StartLoad(long chatId)
        {
            if (!Groups.TryGetValue(chatId, out var entry)) Groups[chatId] = entry = new Entry();
            if (entry.Loading != null) return entry.Loading;
            if (!PostgresStorage.Ready)
            {
                // Nothing kept: what admins ban now is only in memory.
                entry.Loaded = true;
                entry.FreshUntil = Clock.UtcNow + RefreshEvery;
                return Task.CompletedTask;
            }
            entry.Loading = ReadAsync(chatId);
            Bot.Dispatch(() => entry.Loading, "BannedMedia.Read");
            return entry.Loading;
        }

        private static async Task LoadAsync(long chatId)
        {
            Task loading;
            lock (Lock) loading = StartLoad(chatId);
            await loading;
        }

        private static async Task ReadAsync(long chatId)
        {
            await Task.Yield();
            IReadOnlyList<BannedFile> files = null;
            try
            {
                files = await Repositories.BannedMedia.ListAsync(chatId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Reading the banned media of {chatId}");
            }
            lock (Lock)
            {
                var entry = Groups[chatId];
                entry.Loading = null;
                if (files != null)
                {
                    // What was added here meanwhile stays; the stored list comes first.
                    var added = entry.Order.Where(f => files.All(s => s.MediaUniqueId != f)).ToList();
                    entry.Order.Clear();
                    entry.Order.AddRange(files.Select(f => f.MediaUniqueId).Concat(added));
                    Trim(entry);
                    entry.Files.Clear();
                    entry.Files.UnionWith(entry.Order);
                    entry.Loaded = true;
                    entry.FreshUntil = Clock.UtcNow + RefreshEvery;
                }
                else
                {
                    // Asked again in a minute; meanwhile what is in memory (possibly nothing) is used.
                    entry.Loaded = true;
                    entry.FreshUntil = Clock.UtcNow + TimeSpan.FromMinutes(1);
                }
            }
        }

        private static void Trim(Entry entry)
        {
            if (entry.Order.Count > PerGroup) entry.Order.RemoveRange(0, entry.Order.Count - PerGroup);
        }

        // ------------------------------------------------------------------ banning

        /// <summary>Bans a file in the group, at once in memory and then in PostgreSQL. False when it was banned already.</summary>
        /// <param name="source">ban (a ban or kick replying to it), report (Ban on a report) or command (/banmedia).</param>
        internal static bool Add(long chatId, string file, string type, long? by, string source)
        {
            if (string.IsNullOrEmpty(file)) return false;
            bool added;
            lock (Lock)
            {
                if (!Groups.TryGetValue(chatId, out var entry)) Groups[chatId] = entry = new Entry();
                added = entry.Files.Add(file);
                if (added)
                {
                    entry.Order.Add(file);
                    if (entry.Order.Count > PerGroup)
                    {
                        var dropped = entry.Order[0];
                        entry.Order.RemoveAt(0);
                        entry.Files.Remove(dropped);
                    }
                }
            }
            if (!added || !PostgresStorage.Ready) return added;
            var row = new BannedFile(file, type ?? "other", by, source, new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero));
            HistoryWriter.Enqueue(() => Repositories.BannedMedia.AddAsync(chatId, row, PerGroup), $"Banning a file in {chatId}");
            return true;
        }

        /// <summary>Lifts the ban on a file. False when it was not banned.</summary>
        internal static bool Remove(long chatId, string file)
        {
            bool removed;
            lock (Lock)
            {
                removed = Groups.TryGetValue(chatId, out var entry) && entry.Files.Remove(file);
                if (removed) entry.Order.Remove(file);
            }
            if (PostgresStorage.Ready)
                HistoryWriter.Enqueue(() => Repositories.BannedMedia.RemoveAsync(chatId, file), $"Unbanning a file in {chatId}");
            return removed;
        }

        /// <summary>Lifts every ban on files in the group; how many there were.</summary>
        internal static int Clear(long chatId)
        {
            int count;
            lock (Lock)
            {
                count = Groups.TryGetValue(chatId, out var entry) ? entry.Files.Count : 0;
                if (entry != null)
                {
                    entry.Files.Clear();
                    entry.Order.Clear();
                }
            }
            if (PostgresStorage.Ready)
                HistoryWriter.Enqueue(() => Repositories.BannedMedia.ClearAsync(chatId), $"Unbanning every file in {chatId}");
            return count;
        }

        /// <summary>How many files are banned in the group (read first if need be).</summary>
        internal static async Task<int> CountAsync(long chatId)
        {
            await LoadAsync(chatId);
            lock (Lock) return Groups.TryGetValue(chatId, out var entry) ? entry.Files.Count : 0;
        }

        /// <summary>
        /// An admin pressed Ban on a Detection report: the post it was about, if it had a file (recorded by
        /// <see cref="EditCheck"/> in the last two days), is banned too.
        /// </summary>
        internal static async Task FromReportAsync(long chatId, long messageId, long by)
        {
            try
            {
                var today = Clock.UtcNow;
                foreach (var day in new[] { today, today.AddDays(-1), today.AddDays(-2) })
                {
                    var record = await Redis.db.HashGetAsync(EditCheck.RecordKey(chatId, day), messageId);
                    if (!record.HasValue) continue;
                    var parts = record.ToString().Split(':', 2);
                    if (parts.Length == 2) Add(chatId, parts[1], parts[0], by, "report");
                    return;
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Banning the file of a reported post in {chatId}");
            }
        }

        internal static void ResetForTests()
        {
            lock (Lock) Groups.Clear();
        }
    }
}
