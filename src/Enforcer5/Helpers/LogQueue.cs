using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Every log channel post goes through here: queued and journaled at once (<see cref="ILogJournal"/>), then
    /// posted in the background, in order, per log channel, as Telegram's limits allow. Nothing that logs waits
    /// for the post: an admin's button press used to wait up to 10 s for a slot in a busy log channel before
    /// the menu redrew (The Origin, 2026-10-29).
    /// <list type="bullet">
    /// <item>A backlog goes out merged, up to <see cref="MaxLength"/> characters a post, each entry's "go to
    /// message" button turned into a link in its text: a busy channel costs fewer posts, never fewer entries.</item>
    /// <item>A 429 waits its retry_after and tries again; nothing is dropped for it. While a group of the post
    /// is in a raid (<see cref="Raids.RaidGuard.Quiet"/>), posts are spaced <see cref="QuietSpacing"/> apart,
    /// so moderation keeps the room and the entries pile up into fewer posts.</item>
    /// <item>Lost access to the channel removes the setting and tells the group, as before; any other failure
    /// is tried <see cref="MaxAttempts"/> times, then reported and dropped.</item>
    /// <item>A restart reloads the journal and posts what was waiting.</item>
    /// </list>
    /// </summary>
    internal static class LogQueue
    {
        internal const int MaxLength = 3800;
        internal const int MaxEntriesPerPost = 50;
        internal const int MaxAttempts = 5;
        internal static TimeSpan QuietSpacing = TimeSpan.FromSeconds(30);

        private static readonly object Lock = new();
        private static readonly Dictionary<long, List<LogEntry>> ByChannel = new();
        private static readonly HashSet<long> Draining = new();
        private static long _sequence;
        // Bumped by ResetForTests: a drain from before a simulated crash stops instead of posting twice.
        private static int _generation;

        internal static ILogJournal Journal => Repositories.LogJournal;

        /// <summary>
        /// Queues <paramref name="text"/> (HTML) for <paramref name="logChat"/>, the log channel of
        /// <paramref name="groupId"/> (0 for none), with an optional "go to message" link. Never waits, never throws.
        /// </summary>
        internal static void Enqueue(long groupId, long logChat, string text, string linkUrl = null, string linkLabel = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var sequence = NextSequence();
                var entry = new LogEntry($"{sequence:x}", sequence, groupId, logChat, text, linkUrl, linkLabel);
                Journal.Save(entry);
                Add(entry);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Queueing a log channel post for {groupId}");
            }
        }

        /// <summary>Posts what the journal held from before a restart.</summary>
        internal static async Task StartAsync()
        {
            try
            {
                foreach (var entry in await Journal.LoadAsync())
                {
                    lock (Lock) _sequence = Math.Max(_sequence, entry.Sequence);
                    Add(entry);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Loading the log queue");
            }
        }

        /// <summary>Entries waiting for a log channel, for tests and /status.</summary>
        internal static int WaitingFor(long logChat)
        {
            lock (Lock) return ByChannel.TryGetValue(logChat, out var l) ? l.Count : 0;
        }

        // Each post tried (sent, failed or waited out), for /queues.
        private static long _tries;
        private static long _posts;

        /// <summary>For /queues and the watchdog (Helpers/QueueStatus): stuck when entries wait and no post is tried for 2 minutes.</summary>
        internal static readonly QueueStatus Status = QueueStatus.Watch("log channels", () =>
        {
            int entries, channels;
            lock (Lock)
            {
                entries = ByChannel.Values.Sum(l => l.Count);
                channels = ByChannel.Count(c => c.Value.Count > 0);
            }
            return new QueueStatus.Reading(entries, Interlocked.Read(ref _tries),
                $"{entries} entries waiting for {channels} channels, {Interlocked.Read(ref _posts)} posts");
        });

        internal static int Count
        {
            get { lock (Lock) return ByChannel.Values.Sum(l => l.Count); }
        }

        /// <summary>Forgets what is in memory (the journal stays), as a crash would.</summary>
        internal static void ResetForTests()
        {
            lock (Lock)
            {
                ByChannel.Clear();
                Draining.Clear();
                _generation++;
            }
        }

        private static long NextSequence()
        {
            lock (Lock)
            {
                // Ticks, so entries queued after a restart sort after the ones reloaded from the journal.
                _sequence = Math.Max(_sequence + 1, Clock.UtcNow.Ticks);
                return _sequence;
            }
        }

        private static void Add(LogEntry entry)
        {
            bool start;
            lock (Lock)
            {
                if (!ByChannel.TryGetValue(entry.LogChat, out var list)) ByChannel[entry.LogChat] = list = new List<LogEntry>();
                if (list.Any(e => e.Id == entry.Id)) return;
                list.Add(entry);
                start = Draining.Add(entry.LogChat);
            }
            if (!start) return;
            int generation;
            lock (Lock) generation = _generation;
            Bot.Dispatch(() => DrainAsync(entry.LogChat, generation), "LogQueue");
        }

        /// <summary>The next post: the oldest entry, and as many after it as fit.</summary>
        internal static List<LogEntry> NextBatch(IReadOnlyList<LogEntry> waiting)
        {
            var batch = new List<LogEntry>();
            var length = 0;
            foreach (var entry in waiting)
            {
                var size = entry.Text.Length + (entry.LinkUrl == null ? 0 : entry.LinkUrl.Length + 40) + 2;
                if (batch.Count > 0 && (length + size > MaxLength || batch.Count >= MaxEntriesPerPost)) break;
                batch.Add(entry);
                length += size;
            }
            return batch;
        }

        /// <summary>One entry as it was always posted (with its button); several merged, their links in the text.</summary>
        internal static (string Text, InlineKeyboardMarkup Buttons) Compose(IReadOnlyList<LogEntry> batch)
        {
            if (batch.Count == 1)
            {
                var only = batch[0];
                return (only.Text, only.LinkUrl == null ? null
                    : new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl(only.LinkLabel ?? "↗", only.LinkUrl)));
            }
            var post = new StringBuilder();
            foreach (var entry in batch)
            {
                if (post.Length > 0) post.Append("\n\n");
                post.Append(entry.Text);
                if (entry.LinkUrl != null)
                    post.Append("\n<a href=\"").Append(WebUtility.HtmlEncode(entry.LinkUrl)).Append("\">")
                        .Append(WebUtility.HtmlEncode(entry.LinkLabel ?? "↗")).Append("</a>");
            }
            return (post.ToString(), null);
        }

        private static async Task DrainAsync(long logChat, int generation)
        {
            var attempts = 0;
            var parseMode = ParseMode.Html;
            while (true)
            {
                List<LogEntry> batch;
                lock (Lock)
                {
                    if (generation != _generation) return;
                    if (!ByChannel.TryGetValue(logChat, out var list) || list.Count == 0)
                    {
                        ByChannel.Remove(logChat);
                        Draining.Remove(logChat);
                        return;
                    }
                    batch = NextBatch(list);
                }
                var (text, buttons) = Compose(batch);
                try
                {
                    await Bot.Api.SendMessage(logChat, text, parseMode, linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                        replyMarkup: buttons);
                    Interlocked.Increment(ref Bot.MessagesSent);
                    Interlocked.Increment(ref _posts);
                    Interlocked.Increment(ref _tries);
                    Done(logChat, batch);
                    attempts = 0;
                    parseMode = ParseMode.Html;
                    if (batch.Any(e => e.GroupId != 0 && Raids.RaidGuard.Quiet(e.GroupId))) await Clock.Delay(QuietSpacing);
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _tries);
                    var api = Bot.AsApiError(e);
                    if (Bot.IsRateLimited(api))
                    {
                        // Telegram's or the gateway's own: wait it out and try again; a log line is never lost to a 429.
                        await Clock.Delay(TimeSpan.FromSeconds(Math.Max(1, api.Parameters?.RetryAfter ?? 1)));
                        continue;
                    }
                    if (Bot.IsParseFailure(api) && parseMode == ParseMode.Html)
                    {
                        parseMode = ParseMode.None; // unformatted rather than lost
                        continue;
                    }
                    var kind = TelegramErrors.Classify(e);
                    if (kind == TelegramErrorKind.Migrated && TelegramErrors.MigratedTo(e) is { } to)
                    {
                        // A log group that became a supergroup: its groups log to the new id, and so does what waits.
                        Moved(logChat, to);
                        continue;
                    }
                    if (kind is TelegramErrorKind.ChatGone or TelegramErrorKind.NoSendRights or TelegramErrorKind.NoAdminRights
                        or TelegramErrorKind.NoMediaRights or TelegramErrorKind.TopicClosed)
                    {
                        // Removed, or there but may not post (no admin in a channel, muted in a group): the same
                        // setup problem. Before 2026-11-06 the second kind was tried five times per post, reported
                        // and told to the group each time, without end.
                        LostAccess(logChat);
                        continue;
                    }
                    if (++attempts < MaxAttempts)
                    {
                        await Clock.Delay(TimeSpan.FromSeconds(5 * attempts));
                        continue;
                    }
                    ErrorReporter.Report(e, $"Posting {batch.Count} entries to log channel {logChat}, dropped");
                    foreach (var group in batch.Select(b => b.GroupId).Where(g => g != 0).Distinct())
                        Bot.Send(Methods.GetLocaleString(Methods.GetGroupLanguage(group).Doc, "logSendError"), group);
                    Done(logChat, batch);
                    attempts = 0;
                    parseMode = ParseMode.Html;
                }
            }
        }

        private static void Done(long logChat, List<LogEntry> batch)
        {
            lock (Lock)
            {
                if (ByChannel.TryGetValue(logChat, out var list))
                    list.RemoveAll(e => batch.Contains(e));
            }
            Journal.Remove(batch.Select(e => e.Id));
        }

        // The log group became the supergroup <paramref name="to"/>: the groups logging there now log to it, and what
        // waits goes there.
        private static void Moved(long logChat, long to)
        {
            List<LogEntry> waiting;
            lock (Lock)
            {
                waiting = ByChannel.TryGetValue(logChat, out var list) ? list.ToList() : new List<LogEntry>();
                list?.Clear();
            }
            foreach (var group in waiting.Select(e => e.GroupId).Where(g => g != 0).Distinct())
            {
                try
                {
                    if (GroupConfigs.For(group).LogChat == logChat) GroupConfigs.Change(group, Data.ConfigChange.SetLogChat(to));
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Moving the log channel of {group} to {to}");
                }
            }
            Journal.Remove(waiting.Select(e => e.Id));
            foreach (var entry in waiting)
            {
                var moved = entry with { LogChat = to };
                Journal.Save(moved);
                Add(moved);
            }
        }

        // The bot was removed from the channel, or it is gone: every group logging there loses the setting and is
        // told, and what was waiting for it is dropped. A setup change, not an error.
        private static void LostAccess(long logChat)
        {
            List<LogEntry> dropped;
            lock (Lock)
            {
                dropped = ByChannel.TryGetValue(logChat, out var list) ? list.ToList() : new List<LogEntry>();
                list?.Clear();
            }
            Journal.Remove(dropped.Select(e => e.Id));
            foreach (var group in dropped.Select(e => e.GroupId).Where(g => g != 0).Distinct())
            {
                try
                {
                    if (GroupConfigs.For(group).LogChat != logChat) continue;
                    GroupConfigs.Change(group, Data.ConfigChange.SetLogChat(null));
                    Bot.Send(Methods.GetLocaleString(Methods.GetGroupLanguage(group).Doc, "logchannelAutoRemoved"), group);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Removing the lost log channel of {group}");
                }
            }
        }
    }
}
