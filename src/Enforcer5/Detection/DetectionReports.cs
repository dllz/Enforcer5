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
    /// Reports never hold up the message handler or the enforcement queue: they are dispatched work, and
    /// a failure is reported, not thrown. Never message text: what was seen, by its sign.
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
            internal int Score;
            internal long? MessageId;
            internal readonly List<ReportCopy> Copies = new();
            internal string Decision;
            internal long? DecidedBy;
            internal bool EditPending;
            internal DateTime OpenedUtc => Key.OpenedAt.UtcDateTime;

            internal void Add(string sign)
            {
                var at = Signs.FindIndex(s => s.Sign == sign);
                if (at >= 0) Signs[at] = (sign, Signs[at].Count + 1);
                else Signs.Add((sign, 1));
            }
        }

        private static readonly ConcurrentDictionary<(long Chat, long User), OpenReport> Open = new();
        private static readonly ConcurrentDictionary<(long Chat, long User), SemaphoreSlim> Gates = new();

        private sealed record Held(long UserId, string Name, List<string> Signs);
        private static readonly ConcurrentDictionary<long, List<Held>> HeldDuringRaid = new();

        private static SemaphoreSlim Gate(long chatId, long userId) => Gates.GetOrAdd((chatId, userId), _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// Tells the admins about <paramref name="userId"/>: <paramref name="sign"/> fired for them, about
        /// <paramref name="messageId"/> if there is one. Dispatched; returns at once.
        /// </summary>
        /// <param name="member">Who they are, when the caller has it; else their name is looked up.</param>
        /// <param name="score">Their score now, 0 when the sign does not score.</param>
        internal static void Report(long chatId, long userId, long? messageId, string sign, User member = null, int score = 0)
        {
            if (chatId > 0 || userId == 0) return;
            Bot.Dispatch(() => ReportAsync(chatId, userId, messageId, sign, member, score), "DetectionReport");
        }

        internal static async Task ReportAsync(long chatId, long userId, long? messageId, string sign, User member, int score)
        {
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
                await gate.WaitAsync();
                try
                {
                    var report = await CurrentAsync(chatId, userId);
                    if (report != null)
                    {
                        if (report.Decision != null) return; // decided: nothing to add
                        report.Add(sign);
                        report.Score = Math.Max(report.Score, score);
                        report.MessageId = messageId ?? report.MessageId;
                        ScheduleEdit(report);
                        return;
                    }

                    var now = Clock.UtcNow;
                    report = new OpenReport
                    {
                        Key = ReportKey.ForMember(chatId, userId, DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(now).ToUnixTimeSeconds())),
                        Name = name, Score = score, MessageId = messageId
                    };
                    // What made up their score, then what fired now.
                    foreach (var scored in MemberScores.Signs(chatId, userId).Where(s => s != sign)) report.Add(scored);
                    report.Add(sign);
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
            foreach (var (sign, count) in await Repositories.DetectionLog.SignsSinceAsync(key.ChatId, key.UserId.Value, key.OpenedAt))
                report.Signs.Add((sign, count));
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
                await gate.WaitAsync();
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
        /// A button under a report: <c>dreport:{chat}:{user}:{opened}:{ban|mute|fine}</c>. Only the group's
        /// admins get here (the callback is GroupAdminOnly on the chat id).
        /// </summary>
        internal static async Task PressAsync(CallbackQuery call, long chatId, long userId, long opened, string decision)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var actor = call.From.Id;
            var key = ReportKey.ForMember(chatId, userId, DateTimeOffset.FromUnixTimeSeconds(opened));
            var gate = Gate(chatId, userId);
            OpenReport report;
            string standing;
            long? by;
            await gate.WaitAsync();
            try
            {
                report = Open.TryGetValue((chatId, userId), out var open) && open.Key.OpenedAt == key.OpenedAt ? open : null;
                if (report == null && PostgresStorage.Ready && await Repositories.DetectionReports.GetAsync(key) is { } stored)
                    report = await RestoreAsync(stored);
                if (report == null)
                {
                    await Answer(call, Methods.GetLocaleString(lang, "reportGone"));
                    return;
                }
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

            if (standing == decision && by == actor && report.Decision == decision)
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
                    if (Methods.BanUser(chatId, userId, lang, Reason, actor)) Methods.SaveBan(userId, "ban");
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

        internal static string Text(XDocument lang, long userId, string name, string title, IReadOnlyList<(string Sign, int Count)> signs,
            int score, string decisionLine)
        {
            var lines = new List<string>
            {
                // Not GetLocaleString: it would escape the link. Every argument is escaped here.
                Methods.GetLocaleStringNoFormat(lang, "reportTitle", title.FormatHTML()),
                Methods.GetLocaleStringNoFormat(lang, "reportMember", Mention(userId, name), userId),
                "",
                Methods.GetLocaleString(lang, "reportSigns")
            };
            lines.AddRange(signs.Select(s => s.Count > 1
                ? $"• {SignLabel(lang, s.Sign).FormatHTML()} ×{s.Count}"
                : $"• {SignLabel(lang, s.Sign).FormatHTML()}"));
            if (score > 0) lines.Add(Methods.GetLocaleString(lang, "reportScore", score));
            if (decisionLine != null) lines.AddRange(new[] { "", decisionLine });
            return string.Join("\n", lines);
        }

        private static string Text(XDocument lang, OpenReport report, string title) =>
            Text(lang, report.Key.UserId.Value, report.Name, title, report.Signs, report.Score,
                report.Decision != null ? DecisionLine(lang, report.Decision, report.DecidedBy) : null);

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
            var link = report.MessageId is { } m ? Methods.GetChatMessageLink(key.ChatId, m.ToString()) : null;
            var menu = new Menu(2);
            if (report.Decision == null)
            {
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportBan"), $"{data}:ban") { Style = KeyboardButtonStyle.Danger });
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportMute"), $"{data}:mute"));
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportFine"), $"{data}:fine") { Style = KeyboardButtonStyle.Success });
            }
            if (!string.IsNullOrEmpty(link))
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "goToMessage")) { Url = link });
            return menu.Buttons.Count == 0 ? null : Key.CreateMarkupFromMenu(menu);
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

        private static string StoredName(long userId)
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

        private static async Task<string> TitleAsync(long chatId)
        {
            try
            {
                return (await ChatDetails.GetAsync(chatId))?.Title ?? chatId.ToString();
            }
            catch (Exception)
            {
                // A title is only shown; the id does as well.
                return chatId.ToString();
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
}
