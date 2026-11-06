using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Detection's reports to a group's admins (design section 6, build step 7).
    /// <list type="bullet">
    /// <item>They go to each admin who started the bot, in private, unless they opted out in the Reports
    /// menu; or to the group's admin chat, or both (<see cref="EngineSettings.ReportTo"/>).</item>
    /// <item>One report per member per <see cref="Window"/>: a sign that comes later edits every copy, at
    /// most once per <see cref="EditAfter"/>.</item>
    /// <item>Buttons Ban, Mute, Fine and Go to message; only the group's admins can press them, the first
    /// decision is written on every copy (<c>report_copies</c>) and every copy is edited to say it. Every
    /// press is a label (<see cref="Decisions"/>, source <c>report</c>). Fine clears the member's score
    /// and exempts them from the signs they were reported for (<see cref="Fines"/>).</item>
    /// <item>During a raid (<see cref="Raids.RaidGuard.Quiet"/>) nothing is sent: the members are held and
    /// summed up in one report when it ends.</item>
    /// </list>
    /// <item>A message Detection deleted is never linked to (the link would go nowhere): the report quotes the
    /// latest one instead (<see cref="DeletedPost"/>), and Show deleted post sends its file to the admin who
    /// asks, in private (Daniel, 2026-11-03).</item>
    /// Reports never hold up the message handler or the enforcement queue: they are dispatched work, and
    /// a failure is reported, not thrown. What was seen, by its sign; message text only from a deleted post.
    /// </summary>
    internal static class DetectionReports
    {
        internal const string Trigger = "dreport";
        internal static readonly TimeSpan Window = TimeSpan.FromHours(24);
        internal static TimeSpan EditAfter = TimeSpan.FromSeconds(30);

        /// <summary>The most members named in the summary after a raid.</summary>
        internal const int SummaryNames = 20;

        private sealed class OpenReport
        {
            internal ReportKey Key;
            internal string Name;
            internal readonly List<(string Sign, int Count)> Signs = new();
            internal readonly Dictionary<string, string> Details = new();
            /// <summary>The last message each sign was seen on; signs seen on none (a join, a profile) are not here.</summary>
            internal readonly Dictionary<string, long> Messages = new();
            internal int Score;
            /// <summary>The latest message the report is about: the file a Ban bans.</summary>
            internal long? MessageId;
            /// <summary>The messages Detection deleted: nothing links to them.</summary>
            internal readonly HashSet<long> Gone = new();
            /// <summary>The latest deleted post, quoted in the text; null when none (or not known after a restart).</summary>
            internal DeletedPost Post;
            /// <summary>The inline bot the latest post sent through one went through; null when none. Its line and Trust button.</summary>
            internal ViaBot Via;
            internal readonly List<ReportCopy> Copies = new();
            internal string Decision;
            internal long? DecidedBy;
            internal bool EditPending;
            internal DateTime OpenedUtc => Key.OpenedAt.UtcDateTime;

            internal void Add(string sign, long? messageId, bool deleted = false)
            {
                var at = Signs.FindIndex(s => s.Sign == sign);
                if (at >= 0) Signs[at] = (sign, Signs[at].Count + 1);
                else Signs.Add((sign, 1));
                if (messageId is not { } id) return;
                Messages[sign] = id;
                if (deleted) Gone.Add(id);
            }

            /// <summary>Each sign's link to its message, for the text; none to a deleted one.</summary>
            internal Dictionary<string, string> Links() =>
                Messages.Where(m => !Gone.Contains(m.Value))
                    .ToDictionary(m => m.Key, m => Methods.GetChatMessageLink(Key.ChatId, m.Value.ToString()));

            /// <summary>Where Go to message goes: the latest message still there; null when none is.</summary>
            internal long? LinkTarget =>
                MessageId is { } latest && !Gone.Contains(latest) ? latest
                : Messages.Values.Where(id => !Gone.Contains(id)).Select(id => (long?)id).Max();
        }

        private static readonly ConcurrentDictionary<(long Chat, long User), OpenReport> Open = new();
        private static readonly ConcurrentDictionary<(long Chat, long User), SemaphoreSlim> Gates = new();

        private sealed record Held(long UserId, string Name, List<string> Signs);
        private static readonly ConcurrentDictionary<long, List<Held>> HeldDuringRaid = new();

        private static SemaphoreSlim Gate(long chatId, long userId) => Gates.GetOrAdd((chatId, userId), _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// Enters a member's gate. Waiting for it is waiting on whoever holds it, which may be waiting on the clock
        /// (a message slot, the edit delay): parked, so the harness's simulated time can move on.
        /// </summary>
        private static async Task EnterAsync(SemaphoreSlim gate)
        {
            if (gate.Wait(0)) return;
            Clock.Park();
            try
            {
                await gate.WaitAsync();
            }
            finally
            {
                Clock.Unpark();
            }
        }

        /// <summary>
        /// Tells the admins about <paramref name="userId"/>: <paramref name="sign"/> fired for them, about
        /// <paramref name="messageId"/> if there is one. Dispatched; returns at once.
        /// </summary>
        /// <param name="member">Who they are, when the caller has it; else their name is looked up.</param>
        /// <param name="score">Their score now, 0 when the sign does not score.</param>
        /// <param name="detail">What the report says about the sign (what an edit changed), in the group's language.</param>
        /// <param name="deleted">The message, when Detection deleted it: quoted rather than linked to.</param>
        /// <param name="viaBot">The inline bot the message was sent through, if any: the report names it and offers to trust it.</param>
        internal static void Report(long chatId, long userId, long? messageId, string sign, User member = null, int score = 0,
            string detail = null, DeletedPost deleted = null, User viaBot = null)
        {
            if (chatId > 0 || userId == 0) return;
            Bot.Dispatch(() => ReportAsync(chatId, userId, messageId, sign, member, score, detail, deleted, viaBot), "DetectionReport");
        }

        internal static async Task ReportAsync(long chatId, long userId, long? messageId, string sign, User member, int score,
            string detail = null, DeletedPost deleted = null, User viaBot = null)
        {
            var via = viaBot is { IsBot: true } ? new ViaBot(viaBot.Id, viaBot.Username) : null;
            try
            {
                if (await Fines.ExemptAsync(chatId, userId, sign)) return;
                var name = member != null ? Name(member) : StoredName(userId);
                if (Raids.RaidGuard.Quiet(chatId))
                {
                    Hold(chatId, userId, name, sign);
                    return;
                }

                var gate = Gate(chatId, userId);
                await EnterAsync(gate);
                try
                {
                    // A flagged profile image is not a post: nothing to keep by message (not shown after a restart).
                    if (deleted is { Flagged: false }) await KeepAsync(chatId, deleted);
                    var report = await CurrentAsync(chatId, userId);
                    if (report != null)
                    {
                        if (report.Decision != null) return; // decided: nothing to add
                        report.Add(sign, messageId, deleted != null);
                        if (deleted != null) report.Post = deleted;
                        if (detail != null) report.Details[sign] = detail;
                        report.Score = Math.Max(report.Score, score);
                        report.MessageId = messageId ?? report.MessageId;
                        report.Via = via ?? report.Via;
                        ScheduleEdit(report);
                        return;
                    }

                    var now = Clock.UtcNow;
                    report = new OpenReport
                    {
                        Key = ReportKey.ForMember(chatId, userId, DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(now).ToUnixTimeSeconds())),
                        Name = name, Score = score, MessageId = messageId, Post = deleted, Via = via
                    };
                    // What made up their score, then what fired now.
                    foreach (var (scored, seenOn, gone, said) in MemberScores.Seen(chatId, userId).Where(s => s.Sign != sign))
                    {
                        report.Add(scored, seenOn, gone);
                        if (said != null) report.Details[scored] = said;
                    }
                    report.Add(sign, messageId, deleted != null);
                    if (detail != null) report.Details[sign] = detail;
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    var copies = await SendAsync(chatId, Text(lang, report, await TitleAsync(chatId)), Buttons(lang, report));
                    report.Copies.AddRange(copies);
                    Open[(chatId, userId)] = report;
                    if (copies.Count > 0 && PostgresStorage.Ready)
                        await Repositories.DetectionReports.AddCopiesAsync(report.Key, copies);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Reporting {userId} in {chatId} for {sign}");
            }
        }

        /// <summary>Keeps a deleted post for its report after a restart. Failing only loses that: reported, never thrown.</summary>
        private static async Task KeepAsync(long chatId, DeletedPost post)
        {
            try
            {
                await Repositories.DeletedPosts.SaveAsync(chatId, post);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Keeping the deleted post {post.MessageId} of {chatId} for its report");
            }
        }

        /// <summary>The kept deleted post <paramref name="messageId"/>, or null (none, expired, or Redis failed: reported).</summary>
        private static async Task<DeletedPost> KeptAsync(long chatId, long messageId)
        {
            try
            {
                return await Repositories.DeletedPosts.GetAsync(chatId, messageId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Reading the deleted post {messageId} of {chatId} for its report");
                return null;
            }
        }

        /// <summary>The member's report from the last day: in memory, or (after a restart) from PostgreSQL.</summary>
        private static async Task<OpenReport> CurrentAsync(long chatId, long userId)
        {
            var now = Clock.UtcNow;
            if (Open.TryGetValue((chatId, userId), out var report))
            {
                if (now - report.OpenedUtc < Window) return report;
                Open.TryRemove((chatId, userId), out _);
                return null;
            }
            if (!PostgresStorage.Ready) return null;
            var stored = await Repositories.DetectionReports.LatestMemberReportAsync(chatId, userId, now - Window);
            return stored == null ? null : await RestoreAsync(stored);
        }

        private static async Task<OpenReport> RestoreAsync(StoredReport stored)
        {
            var key = stored.Key;
            var report = new OpenReport
            {
                Key = key, Name = StoredName(key.UserId.Value), Decision = stored.Decision, DecidedBy = stored.DecidedBy
            };
            report.Copies.AddRange(stored.Copies);
            foreach (var (sign, count, messageId, deleted, detail) in await Repositories.DetectionLog.SignsSinceAsync(key.ChatId, key.UserId.Value, key.OpenedAt))
            {
                report.Signs.Add((sign, count));
                if (detail != null) report.Details[sign] = detail;
                if (messageId is not { } id) continue;
                report.Messages[sign] = id;
                if (deleted) report.Gone.Add(id);
            }
            // The latest of them, for Ban; the latest deleted one is quoted.
            if (report.Messages.Count > 0) report.MessageId = report.Messages.Values.Max();
            if (report.Gone.Count > 0) report.Post = await KeptAsync(key.ChatId, report.Gone.Max());
            if (await Repositories.DetectionLog.ViaBotSinceAsync(key.ChatId, key.UserId.Value, key.OpenedAt) is { } via)
                report.Via = new ViaBot(via.BotId, via.Username);
            report.Score = MemberScores.Of(key.ChatId, key.UserId.Value);
            Open[(key.ChatId, key.UserId.Value)] = report;
            return report;
        }

        private static void ScheduleEdit(OpenReport report)
        {
            if (report.EditPending) return;
            report.EditPending = true;
            Bot.Dispatch(async () =>
            {
                await Clock.Delay(EditAfter);
                var chatId = report.Key.ChatId;
                var gate = Gate(chatId, report.Key.UserId.Value);
                await EnterAsync(gate);
                try
                {
                    report.EditPending = false;
                    await EditCopiesAsync(report);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Updating the report about {report.Key.UserId} in {chatId}");
                }
                finally
                {
                    gate.Release();
                }
            }, "DetectionReportEdit");
        }

        private static async Task EditCopiesAsync(OpenReport report)
        {
            var chatId = report.Key.ChatId;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Text(lang, report, await TitleAsync(chatId));
            var markup = Buttons(lang, report);
            await EditAsync(report.Copies, text, markup);
        }

        /// <summary>Edits every copy; one that is gone or unchanged is left as it is.</summary>
        internal static async Task EditAsync(IEnumerable<ReportCopy> copies, string text, InlineKeyboardMarkup markup)
        {
            foreach (var copy in copies)
            {
                try
                {
                    await PacedAsync(() => Bot.Api.EditMessageText(copy.ChatId, (int)copy.MessageId, text, ParseMode.Html, replyMarkup: markup,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }));
                }
                catch (Exception e)
                {
                    // The admin deleted it, blocked the bot, or it already says this: nothing to change.
                    var api = Bot.AsApiError(e);
                    if (api?.ErrorCode is not (400 or 403))
                        ErrorReporter.Report(e, $"Editing a Detection report in {copy.ChatId}");
                }
            }
        }

        // ------------------------------------------------------------------ where reports go

        /// <summary>
        /// Sends <paramref name="text"/> to the group's report destinations and returns the copies sent. An
        /// admin who never started the bot or blocked it is skipped, as is an admin chat the bot left.
        /// </summary>
        internal static async Task<List<ReportCopy>> SendAsync(long chatId, string text, InlineKeyboardMarkup markup)
        {
            var copies = new List<ReportCopy>();
            foreach (var destination in await DestinationsAsync(chatId))
            {
                try
                {
                    var sent = await PacedAsync(() => Bot.Api.SendMessage(destination, text, ParseMode.Html, replyMarkup: markup,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }));
                    copies.Add(new ReportCopy(destination, sent.MessageId));
                }
                catch (Exception e)
                {
                    // 403: an admin who never started the bot or blocked it, or an admin chat the bot was
                    // removed from; 400: a chat that no longer exists. They are simply not told.
                    var api = Bot.AsApiError(e);
                    if (api?.ErrorCode is not (400 or 403))
                        ErrorReporter.Report(e, $"Sending a Detection report about {chatId} to {destination}");
                }
            }
            return copies;
        }

        /// <summary>How often a send or edit that Telegram (or our own pacing) answered 429 is tried again.</summary>
        internal const int Attempts = 6;

        /// <summary>
        /// <paramref name="call"/>, tried again after the wait a 429 asks for: a burst of reports to one admin
        /// goes out a little later rather than not at all. Reports are dispatched work, so waiting holds up
        /// nothing else.
        /// </summary>
        private static async Task<T> PacedAsync<T>(Func<Task<T>> call)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await call();
                }
                catch (Exception e) when (attempt < Attempts && Bot.AsApiError(e) is { ErrorCode: 429 } limited)
                {
                    await Clock.Delay(TimeSpan.FromSeconds(Math.Clamp(limited.Parameters?.RetryAfter ?? 5, 1, 60)));
                }
            }
        }

        /// <summary>The admin chat (if chosen and linked), then each admin in private unless they opted out.</summary>
        internal static async Task<List<long>> DestinationsAsync(long chatId)
        {
            var engine = (await DetectionSettings.GetAsync(chatId)).Engine;
            var destinations = new List<long>();
            if (engine.ReportChat is { } adminChat) destinations.Add(adminChat);
            if (!engine.ReportsToAdmins) return destinations;
            var optedOut = await OptedOutAsync(chatId);
            foreach (var admin in await AdminDirectory.GetAsync(chatId) ?? Array.Empty<ChatMember>())
            {
                var user = admin.User;
                // Bots cannot be messaged by bots; Telegram's anonymous admin is nobody's private chat.
                if (user == null || user.IsBot || user.Id == Decisions.AnonymousAdmin || optedOut.Contains(user.Id)) continue;
                if (!destinations.Contains(user.Id)) destinations.Add(user.Id);
            }
            return destinations;
        }

        private static readonly ConcurrentDictionary<long, (IReadOnlyCollection<long> Ids, DateTime Until)> OptOuts = new();
        private static readonly TimeSpan OptOutsFresh = TimeSpan.FromMinutes(10);

        /// <summary>The admins who do not want the group's reports in private; cached for 10 minutes.</summary>
        internal static async Task<IReadOnlyCollection<long>> OptedOutAsync(long chatId)
        {
            if (OptOuts.TryGetValue(chatId, out var cached) && cached.Until > Clock.UtcNow) return cached.Ids;
            if (!PostgresStorage.Ready) return Array.Empty<long>();
            var ids = await Repositories.DetectionReports.OptedOutAsync(chatId);
            OptOuts[chatId] = (ids, Clock.UtcNow + OptOutsFresh);
            return ids;
        }

        /// <summary>An admin turned the group's reports to them off or on. False when it could not be saved.</summary>
        internal static bool SetOptedOut(long chatId, long userId, bool optedOut)
        {
            if (!PostgresStorage.Ready) return false;
            try
            {
                Repositories.DetectionReports.SetOptedOutAsync(chatId, userId, optedOut).GetAwaiter().GetResult();
                OptOuts.TryRemove(chatId, out _);
                return true;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Saving whether {userId} gets the reports of {chatId}");
                return false;
            }
        }

        // ------------------------------------------------------------------ decisions

        /// <summary>
        /// A button under a report: <c>dreport:{chat}:{user}:{opened}:{ban|mute|fine|trustbot}</c>. Only the group's
        /// admins get here (the callback is GroupAdminOnly on the chat id).
        /// <para>
        /// <c>trustbot</c>: the inline bot the post went through is trusted in this group (<see cref="BotGuard.TrustAsync"/>,
        /// source <c>report</c>: never everywhere, premium or not), and the member is marked fine if nobody decided yet: what
        /// was seen was the bot's doing (Daniel, 2026-11-04).
        /// </para>
        /// </summary>
        internal static async Task PressAsync(CallbackQuery call, long chatId, long userId, long opened, string decision)
        {
            var trustBot = decision == "trustbot";
            if (trustBot) decision = "fine";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var actor = call.From.Id;
            var key = ReportKey.ForMember(chatId, userId, DateTimeOffset.FromUnixTimeSeconds(opened));
            var gate = Gate(chatId, userId);
            OpenReport report;
            string standing;
            long? by;
            await EnterAsync(gate);
            try
            {
                report = Open.TryGetValue((chatId, userId), out var open) && open.Key.OpenedAt == key.OpenedAt ? open : null;
                if (report == null && PostgresStorage.Ready && await Repositories.DetectionReports.GetAsync(key) is { } stored)
                    report = await RestoreAsync(stored);
                if (report == null || trustBot && report.Via == null)
                {
                    await Answer(call, Methods.GetLocaleString(lang, "reportGone"));
                    return;
                }
                // A button from before the admins or a dev removed the bot: their decision stands, nothing changes.
                if (trustBot && BotGuard.IsBlockedHere(chatId, report.Via.Id))
                {
                    await Answer(call, Methods.GetLocaleString(lang, "reportTrustBotBlocked", report.Via.Name));
                    return;
                }
                // The press is in the log channel as every report button is (Service.LogCallback).
                if (trustBot && !BotGuard.IsChosenHere(chatId, report.Via.Id))
                    await BotGuard.TrustAsync(chatId, report.Via.Id, actor, source: "report");
                if (report.Decision == null)
                {
                    (standing, by) = PostgresStorage.Ready
                        ? await Repositories.DetectionReports.DecideAsync(key, decision, actor, new DateTimeOffset(Clock.UtcNow))
                        : (decision, actor);
                    report.Decision = standing;
                    report.DecidedBy = by;
                    if (standing == decision && by == actor) Act(chatId, userId, actor, decision, report, lang);
                }
                else (standing, by) = (report.Decision, report.DecidedBy);
            }
            finally
            {
                gate.Release();
            }

            if (trustBot)
                await Answer(call, Methods.GetLocaleString(lang, "reportTrustBotDone", report.Via.Name));
            else if (standing == decision && by == actor && report.Decision == decision)
                await Answer(call, Methods.GetLocaleString(lang, "reportDone"));
            else
                await Answer(call, DecisionLine(lang, standing, by, html: false));
            await EditCopiesAsync(report);
        }

        private static void Act(long chatId, long userId, long actor, string decision, OpenReport report, XDocument lang)
        {
            const string Reason = "Detection report";
            switch (decision)
            {
                case "ban":
                    Methods.BanUser(chatId, userId, lang, Reason, actor, stat: "ban");
                    // The file of the post it was about, if any, is banned in the group (step 12).
                    if (report.MessageId is { } posted)
                        Bot.Dispatch(() => BannedMedia.FromReportAsync(chatId, posted, actor), "BannedMedia.FromReport");
                    break;
                case "mute":
                    Methods.MuteUser(chatId, userId, lang, reason: Reason, actorId: actor);
                    break;
                case "fine":
                    var signs = report.Signs.Select(s => s.Sign).ToList();
                    Decisions.Fine(chatId, userId, actor, signs);
                    Fines.Add(chatId, userId, signs);
                    MemberScores.Forgive(chatId, userId);
                    break;
            }
        }

        private static Task Answer(CallbackQuery call, string text) => Bot.Api.AnswerCallbackQuery(call.Id, text);

        /// <summary>
        /// Show deleted post under a report: <c>dreport:{chat}:{user}:{opened}:show</c>. The file goes to the admin who
        /// pressed it, alone, in private, protected from forwarding and saving; nothing else changes. Only the group's
        /// admins get here (GroupAdminOnly), and the log channel's line for the press says who looked.
        /// </summary>
        internal static async Task ShowAsync(CallbackQuery call, long chatId, long userId, long opened)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var key = ReportKey.ForMember(chatId, userId, DateTimeOffset.FromUnixTimeSeconds(opened));
            var report = Open.TryGetValue((chatId, userId), out var open) && open.Key.OpenedAt == key.OpenedAt ? open : null;
            if (report == null && PostgresStorage.Ready && await Repositories.DetectionReports.GetAsync(key) is { } stored)
            {
                var gate = Gate(chatId, userId);
                await EnterAsync(gate);
                try
                {
                    report = await RestoreAsync(stored);
                }
                finally
                {
                    gate.Release();
                }
            }
            var post = report?.Post;
            if (post?.FileId == null)
            {
                await Answer(call, Methods.GetLocaleString(lang, "reportShowGone"));
                return;
            }
            try
            {
                await post.SendAsync(call.From.Id);
            }
            catch (Exception e)
            {
                var api = Bot.AsApiError(e);
                // 403: they never started the bot in private; 400: Telegram no longer has the file. Both are answered.
                if (api?.ErrorCode == 403)
                {
                    await Answer(call, Methods.GetLocaleString(lang, "reportShowStart"));
                    return;
                }
                if (api?.ErrorCode != 400) ErrorReporter.Report(e, $"Showing the deleted post {post.MessageId} of {chatId} to {call.From.Id}");
                await Answer(call, Methods.GetLocaleString(lang, "reportShowGone"));
                return;
            }
            await Answer(call, Methods.GetLocaleString(lang, "reportShowSent"));
        }

        /// <summary>
        /// Show messages under a report: <c>dreport:{chat}:{user}:{opened}:texts</c> (Daniel, 2026-11-06: no report about
        /// something a member did without a way to see it). The text of every message a sign fired on in the report's day
        /// (from a day before it opened to a day after), hidden links written out (<see cref="FlaggedText"/>), goes to the
        /// admin who pressed it, in private, protected from forwarding and saving. Only the group's admins get here, and the
        /// log channel's line for the press says who looked.
        /// </summary>
        internal static async Task ShowMessagesAsync(CallbackQuery call, long chatId, long userId, long opened)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var openedAt = DateTimeOffset.FromUnixTimeSeconds(opened);
            var flagged = await FlaggedAsync(chatId, userId, openedAt - Window, openedAt + Window);
            if (flagged.Count == 0)
            {
                await Answer(call, Methods.GetLocaleString(lang, "reportMessagesNone"));
                return;
            }
            var heading = Methods.GetLocaleStringNoFormat(lang, "reportMessagesTitle", Mention(userId, StoredName(userId)),
                (await TitleAsync(chatId)).FormatHTML());
            try
            {
                foreach (var part in MessagesText(lang, heading, flagged))
                    await PacedAsync(() => Bot.Api.SendMessage(call.From.Id, part, ParseMode.Html, protectContent: true,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }));
            }
            catch (Exception e)
            {
                var api = Bot.AsApiError(e);
                // 403: they never started the bot in private, which they are told.
                if (api?.ErrorCode == 403)
                {
                    await Answer(call, Methods.GetLocaleString(lang, "reportShowStart"));
                    return;
                }
                ErrorReporter.Report(e, $"Showing the flagged messages of {userId} in {chatId} to {call.From.Id}");
                await Answer(call, Methods.GetLocaleString(lang, "reportMessagesNone"));
                return;
            }
            await Answer(call, Methods.GetLocaleString(lang, "reportShowSent"));
        }

        /// <summary>
        /// The member's flagged messages between <paramref name="from"/> and <paramref name="to"/>, oldest first: those written
        /// to <c>detections</c>, and those still in memory (detections are written once a minute). One entry per message,
        /// with every sign it set off.
        /// </summary>
        internal static async Task<IReadOnlyList<FlaggedMessage>> FlaggedAsync(long chatId, long userId, DateTimeOffset from, DateTimeOffset to)
        {
            var all = new List<FlaggedMessage>();
            if (PostgresStorage.Ready)
            {
                try
                {
                    all.AddRange(await Repositories.DetectionLog.FlaggedAsync(chatId, userId, from, to));
                }
                catch (Exception e)
                {
                    // What is in memory is still shown.
                    ErrorReporter.Report(e, $"Reading the flagged messages of {userId} in {chatId}");
                }
            }
            all.AddRange(MemberScores.WithText(chatId, userId)
                .Select(h => new FlaggedMessage(h.Sign, new DateTimeOffset(DateTime.SpecifyKind(h.At, DateTimeKind.Utc)), h.MessageId, h.Text, h.Detail))
                .Where(h => h.At >= from && h.At < to));
            return Merge(all);
        }

        /// <summary>One entry per message (or per sign and moment, for a hit without a message id), its signs joined, oldest first. Pure, for tests.</summary>
        internal static IReadOnlyList<FlaggedMessage> Merge(IEnumerable<FlaggedMessage> flagged) =>
            flagged.Where(f => !string.IsNullOrWhiteSpace(f.Text))
                .GroupBy(f => f.MessageId is { } id ? $"m{id}" : $"s{f.Sign}:{f.At.ToUnixTimeSeconds()}")
                .Select(g =>
                {
                    var first = g.OrderBy(f => f.At).First();
                    var signs = string.Join(",", g.Select(f => f.Sign).Distinct());
                    var details = g.Select(f => f.Detail).Where(d => d != null).Distinct().ToList();
                    return first with { Sign = signs, Detail = details.Count == 0 ? null : string.Join("; ", details) };
                })
                .OrderBy(f => f.At)
                .ToList();

        /// <summary>Longest a part of Show messages may be: a Telegram message holds 4096 characters, entities included.</summary>
        internal const int MessagesPartLength = 3800;

        /// <summary>
        /// Show messages' text: the heading, then each message with its signs, time and details, its text quoted (collapsed);
        /// split into parts that each fit a Telegram message. HTML, every part escaped. Pure, for tests.
        /// </summary>
        internal static IReadOnlyList<string> MessagesText(XDocument lang, string heading, IReadOnlyList<FlaggedMessage> flagged)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder(heading);
            foreach (var f in flagged)
            {
                var labels = string.Join(", ", f.Sign.Split(',').Select(sign => SignLabel(lang, sign).FormatHTML()));
                var line = $"• <b>{labels}</b> · {Methods.ToDisplay(f.At.UtcDateTime):d MMM HH:mm}";
                if (f.Detail != null) line += $" ({f.Detail.FormatHTML()})";
                var text = f.Text;
                // Room for the line and the quote's tags; a text longer than that is cut, as it would not fit one message.
                var room = MessagesPartLength - line.Length - 64;
                if (text.Length > room) text = text[..Math.Max(1, room - 1)] + "…";
                var block = $"\n\n{line}\n<blockquote expandable>{text.FormatHTML()}</blockquote>";
                if (current.Length + block.Length > MessagesPartLength && current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    block = block.TrimStart('\n');
                }
                current.Append(block);
            }
            if (current.Length > 0) parts.Add(current.ToString());
            return parts;
        }

        // ------------------------------------------------------------------ raids

        private static void Hold(long chatId, long userId, string name, string sign)
        {
            var held = HeldDuringRaid.GetOrAdd(chatId, _ => new List<Held>());
            lock (held)
            {
                var member = held.FirstOrDefault(h => h.UserId == userId);
                if (member == null) held.Add(new Held(userId, name, new List<string> { sign }));
                else if (!member.Signs.Contains(sign)) member.Signs.Add(sign);
            }
        }

        /// <summary>A raid in the group is over: the members held back during it, in one report.</summary>
        internal static void RaidEnded(long chatId)
        {
            if (!HeldDuringRaid.TryRemove(chatId, out var held)) return;
            List<Held> members;
            lock (held) members = held.ToList();
            if (members.Count == 0) return;
            Bot.Dispatch(async () =>
            {
                try
                {
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    var lines = members.Take(SummaryNames)
                        .Select(m => $"• {Mention(m.UserId, m.Name)}: {string.Join(", ", m.Signs.Select(s => SignLabel(lang, s).FormatHTML()))}");
                    var text = Methods.GetLocaleStringNoFormat(lang, "reportRaidSummary", (await TitleAsync(chatId)).FormatHTML(), members.Count) +
                               "\n\n" + string.Join("\n", lines);
                    if (members.Count > SummaryNames)
                        text += "\n" + Methods.GetLocaleString(lang, "reportRaidSummaryMore", members.Count - SummaryNames);
                    await SendAsync(chatId, text, null);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Sending the Detection summary after the raid in {chatId}");
                }
            }, "DetectionRaidSummary");
        }

        // ------------------------------------------------------------------ text

        /// <param name="links">For each sign seen on a message, a link to it: the sign's name links there.</param>
        /// <param name="deleted">The latest post Detection deleted, quoted after the signs.</param>
        /// <param name="viaLine">Which inline bot the post went through (<see cref="ViaLine"/>), after the score; HTML.</param>
        internal static string Text(XDocument lang, long userId, string name, string title, IReadOnlyList<(string Sign, int Count)> signs,
            int score, string decisionLine, IReadOnlyDictionary<string, string> details = null, IReadOnlyDictionary<string, string> links = null,
            DeletedPost deleted = null, string viaLine = null)
        {
            var lines = new List<string>
            {
                // Not GetLocaleString: it would escape the link. Every argument is escaped here.
                Methods.GetLocaleStringNoFormat(lang, "reportTitle", title.FormatHTML()),
                Methods.GetLocaleStringNoFormat(lang, "reportMember", Mention(userId, name), userId),
                "",
                Methods.GetLocaleString(lang, "reportSigns")
            };
            lines.AddRange(signs.Select(s =>
            {
                var label = SignLabel(lang, s.Sign).FormatHTML();
                if (links != null && links.TryGetValue(s.Sign, out var link) && !string.IsNullOrEmpty(link))
                    label = $"<a href=\"{link.FormatHTML()}\">{label}</a>";
                var line = s.Count > 1 ? $"• {label} ×{s.Count}" : $"• {label}";
                return details != null && details.TryGetValue(s.Sign, out var detail) ? $"{line} ({detail.FormatHTML()})" : line;
            }));
            if (score > 0) lines.Add(Methods.GetLocaleString(lang, "reportScore", score));
            if (viaLine != null) lines.Add(viaLine);
            if (deleted != null) lines.AddRange(new[] { "", deleted.Html(lang) });
            if (decisionLine != null) lines.AddRange(new[] { "", decisionLine });
            return string.Join("\n", lines);
        }

        private static string Text(XDocument lang, OpenReport report, string title) =>
            Text(lang, report.Key.UserId.Value, report.Name, title, report.Signs, report.Score,
                report.Decision != null ? DecisionLine(lang, report.Decision, report.DecidedBy) : null, report.Details, report.Links(), report.Post,
                report.Via == null ? null : ViaLine(lang, report.Via, BotGuard.IsChosenHere(report.Key.ChatId, report.Via.Id)));

        /// <summary>"Sent through the inline bot @x", and whether the group trusts it now; HTML.</summary>
        internal static string ViaLine(XDocument lang, ViaBot via, bool trusted) =>
            Methods.GetLocaleStringNoFormat(lang, trusted ? "reportViaBotTrusted" : "reportViaBot", via.Html);

        /// <summary>"Banned by …" for a decision: HTML with a link to the admin, or plain text for a button's answer.</summary>
        internal static string DecisionLine(XDocument lang, string decision, long? by, bool html = true)
        {
            var name = by is { } id ? StoredName(id) : "?";
            return html
                ? Methods.GetLocaleStringNoFormat(lang, $"reportDecided_{decision}", by is { } admin ? Mention(admin, name) : "?")
                : Methods.GetLocaleStringNoFormat(lang, $"reportDecided_{decision}", name);
        }

        private static InlineKeyboardMarkup Buttons(XDocument lang, OpenReport report)
        {
            var key = report.Key;
            var data = $"{Trigger}:{key.ChatId}:{key.UserId}:{key.OpenedAt.ToUnixTimeSeconds()}";
            var link = report.LinkTarget is { } m ? Methods.GetChatMessageLink(key.ChatId, m.ToString()) : null;
            var menu = new Menu(2);
            if (report.Decision == null)
            {
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportBan"), $"{data}:ban") { Style = KeyboardButtonStyle.Danger });
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportMute"), $"{data}:mute"));
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportFine"), $"{data}:fine") { Style = KeyboardButtonStyle.Success });
            }
            if (!string.IsNullOrEmpty(link))
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "goToMessage")) { Url = link });
            if (report.Post?.FileId != null)
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, report.Post.Flagged ? "reportShowImage" : "reportShowPost"),
                    $"{data}:show"));
            // Whatever was decided: what they posted stays there to see (Daniel, 2026-11-06).
            if (report.Messages.Count > 0)
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportShowMessages"), $"{data}:texts"));
            // Whatever was decided: an admin who banned the member may still want the bot.
            if (report.Via is { } via && !BotGuard.IsKnown(via.AsUser()) && !BotGuard.IsChosenHere(key.ChatId, via.Id) &&
                !BotGuard.IsBlockedHere(key.ChatId, via.Id))
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportTrustBot", via.Name), $"{data}:trustbot"));
            return menu.Buttons.Count == 0 ? null : Key.CreateMarkupFromMenu(menu);
        }

        /// <summary>The most chats, mentions or links a sign names in a report (and keeps in its evidence).</summary>
        internal const int MaxNamed = 5;

        /// <summary>
        /// What a sign pointed at, for its report line: the first <see cref="MaxNamed"/> (each cut at 60 characters),
        /// then how many more; null for none. Pure, for tests.
        /// </summary>
        internal static string Named(IReadOnlyList<string> items)
        {
            if (items == null || items.Count == 0) return null;
            var shown = items.Take(MaxNamed).Select(i => i.Length > 60 ? i[..59] + "…" : i);
            var text = string.Join(", ", shown);
            return items.Count > MaxNamed ? $"{text} +{items.Count - MaxNamed}" : text;
        }

        /// <summary>How a sign is named to admins: its own string, a filter preset's name, or the rule's number.</summary>
        internal static string SignLabel(XDocument lang, string sign)
        {
            if (sign.StartsWith(SignCatalog.FilterPrefix, StringComparison.Ordinal))
            {
                var rule = sign.Substring(SignCatalog.FilterPrefix.Length);
                return FilterPresets.Get(rule) != null
                    ? Methods.GetLocaleString(lang, $"filterPreset_{rule}")
                    : Methods.GetLocaleString(lang, "reportSignRule", rule);
            }
            var definition = SignCatalog.Get(sign);
            // The checks from before the engine never report through here; named as they are stored.
            return definition == null || definition.Ported || !InEnglish($"sign_{sign}") ? sign : Methods.GetLocaleString(lang, $"sign_{sign}");
        }

        // A sign without a string of its own (one a test added) is shown by its name.
        private static bool InEnglish(string key) =>
            Program.LangaugeList.FirstOrDefault(l => l.Name == "English")?.Doc.Descendants("string")
                .Any(s => (string)s.Attribute("key") == key) == true;

        internal static string Mention(long userId, string name) =>
            $"<a href=\"tg://user?id={userId}\">{(string.IsNullOrWhiteSpace(name) ? userId.ToString() : name).FormatHTML()}</a>";

        private static string Name(User user) =>
            user.Username != null ? $"{user.FirstName} (@{user.Username})" : $"{user.FirstName} {user.LastName}".Trim();

        internal static string StoredName(long userId)
        {
            try
            {
                var name = Redis.db.HashGetAsync($"user:{userId}", "name").GetAwaiter().GetResult();
                return name.HasValue && !name.IsNullOrEmpty ? name.ToString() : userId.ToString();
            }
            catch (Exception)
            {
                // A name is only shown; the id does as well.
                return userId.ToString();
            }
        }

        internal static async Task<string> TitleAsync(long chatId)
        {
            try
            {
                return (await ChatDetails.GetAsync(chatId))?.Title ?? Ids.Mark(chatId);
            }
            catch (Exception)
            {
                // A title is only shown; the id does as well.
                return Ids.Mark(chatId);
            }
        }

        internal static void ResetForTests()
        {
            Open.Clear();
            Gates.Clear();
            HeldDuringRaid.Clear();
            OptOuts.Clear();
            EditAfter = TimeSpan.FromSeconds(30);
        }

        /// <summary>A restart: what was in memory is gone.</summary>
        internal static void ForgetForTests()
        {
            Open.Clear();
            Gates.Clear();
            OptOuts.Clear();
        }
    }

    /// <summary>An inline bot a reported post was sent through (<c>Message.ViaBot</c>).</summary>
    internal sealed record ViaBot(long Id, string Username)
    {
        /// <summary>@username, or the id for a bot without one.</summary>
        internal string Name => Username != null ? $"@{Username}" : Id.ToString();

        /// <summary>A link to the bot, for a report's text.</summary>
        internal string Html => Username != null
            ? $"<a href=\"https://t.me/{Username.FormatHTML()}\">@{Username.FormatHTML()}</a>"
            : $"<code>{Id}</code>";

        internal User AsUser() => new() { Id = Id, IsBot = true, Username = Username, FirstName = Username ?? Id.ToString() };
    }
}
