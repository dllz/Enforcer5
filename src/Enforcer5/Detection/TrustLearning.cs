using System;
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
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Detection learns two of a group's levels from what its admins decide, and tells them every time
    /// (build step 7b, Daniel 2026-10-28: "be transparent about the learning").
    /// <list type="bullet">
    /// <item><b>Established.</b> An admin bans, kicks or mutes an established member after Detection saw a sign
    /// about them in the day before (<see cref="TrustSigns"/>), or sets one back to new with /untrust: the
    /// group's established level goes halfway up towards that member's messages and days, at most
    /// <see cref="CapFactor"/> times the usual level (and <see cref="CapDaysFactor"/> times its days). A
    /// member already past that cap looks like a taken-over account: the level stays and the admins are told
    /// why. After <see cref="EaseAfter"/> without another raise the level eases back a quarter of the way each
    /// night until it is the usual one again. The admins are not asked what a decision was about: signs only,
    /// and the classifiers of later steps add theirs.</item>
    /// <item><b>Report level.</b> Each night, from the last 30 days of decisions about members with a score:
    /// the lowest score from which most (<see cref="Precision"/>) were removed or muted rather than found
    /// fine, one step a night, between 2 and 8.</item>
    /// <item>A member set back to new stays new until an admin restores them; after <see cref="RestoreAfter"/>
    /// of talking with nothing against them the admins are asked whether to.</item>
    /// </list>
    /// Changes apply at once with Undo and Keep buttons; while the group is only watching they are proposed
    /// and not applied. Each is a row of <c>detection_adjustments</c>, shown under /menu → Detection →
    /// Learned from you, where learning can be turned off.
    /// </summary>
    internal static class TrustLearning
    {
        internal const string Trigger = "dlearn";
        internal const string RestoreTrigger = "dtrust";

        internal static readonly TimeSpan SignsBefore = TimeSpan.FromHours(24);
        internal const int CapFactor = 3;
        internal const int CapMessages = 1000;
        internal const int CapDaysFactor = 2;
        internal const int CapDays = 60;
        internal static readonly TimeSpan EaseAfter = TimeSpan.FromDays(7);
        internal const double EaseShare = 0.25;

        internal const int MinReportAt = 2;
        internal const int MaxReportAt = 8;
        internal const int MinDecisions = 10;
        internal const int MinAtLevel = 5;
        internal const double Precision = 0.7;
        internal static readonly TimeSpan DecisionsOver = TimeSpan.FromDays(30);

        internal static readonly TimeSpan RestoreAfter = TimeSpan.FromDays(30);

        /// <summary>The nightly work starts from this hour (UTC), like the norms.</summary>
        internal const int NightlyHour = 3;

        private static readonly string[] Strong = { "ban", "tempban", "kick", "mute", "tempmute" };
        private static readonly string[] AdminSources = { "command", "button", "report", "app", "raid" };

        // Signs about how a member behaves in any group (flood) or about the group's own limits: they say
        // nothing about whether "established" was set too low.
        private static readonly string[] NotAboutTrust =
            { SignCatalog.Flood.Name, SignCatalog.MediaLock.Name, SignCatalog.TextLength.Name, SignCatalog.NameLength.Name };

        /// <summary>The signs that make an admin's decision about an established member a reason to raise the level.</summary>
        internal static IReadOnlyCollection<string> TrustSigns =>
            SignCatalog.All.Select(s => s.Name).Where(n => !NotAboutTrust.Contains(n)).ToList();

        // ------------------------------------------------------------------ established

        /// <summary>A decision was recorded (once, by one edition): learn from it if it says something.</summary>
        internal static void Decided(LabelEvent label, long labelId)
        {
            if (label.Label != "bad" || !Strong.Contains(label.Action) || !AdminSources.Contains(label.Source)) return;
            if (label.ChatId > 0 || !PostgresStorage.Ready) return;
            Bot.Dispatch(() => LearnFromDecisionAsync(label, labelId), "TrustLearning.Decided");
        }

        private static async Task LearnFromDecisionAsync(LabelEvent label, long labelId)
        {
            try
            {
                var chatId = label.ChatId;
                var userId = label.UserId;
                var policy = await DetectionSettings.GetAsync(chatId);
                if (!policy.Engine.Learn || TrustOverrides.IsSet(chatId, userId)) return;
                if (await MemberTrust.OfAsync(chatId, userId) != TrustLevel.Established) return;
                // Detections are written once a minute: what was seen just before the decision first.
                await DetectionRecorder.FlushAsync();
                var signs = await Repositories.DetectionLearning.SignsBetweenAsync(chatId, userId, label.At - SignsBefore,
                    label.At.AddSeconds(1), TrustSigns);
                if (signs.Count == 0) return;
                await RaiseAsync(chatId, userId, label.Action, signs, labelId, "decision");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Learning from the {label.Action} of {label.UserId} in {label.ChatId}");
            }
        }

        /// <summary>The level a group has now: the usual one, or the learned one if higher.</summary>
        internal static (int Messages, int Days) Effective(EngineSettings engine, (int Messages, int Days) usual) =>
            engine.Learn
                ? (Math.Max(usual.Messages, engine.TrustMessages ?? 0), Math.Max(usual.Days, engine.TrustDays ?? 0))
                : usual;

        /// <summary>The highest level learning goes to.</summary>
        internal static (int Messages, int Days) Cap((int Messages, int Days) usual) =>
            (Math.Min(CapMessages, Math.Max(usual.Messages * CapFactor, usual.Messages + 1)),
             Math.Min(CapDays, Math.Max(usual.Days * CapDaysFactor, usual.Days + 1)));

        /// <summary>
        /// The established level after a member with <paramref name="messages"/> and <paramref name="days"/>
        /// turned out not to be trusted; null when the member is past the cap (a taken-over account) or the
        /// level is at the cap already. Pure, for tests.
        /// </summary>
        internal static (int Messages, int Days)? Raised((int Messages, int Days) current, (int Messages, int Days) usual,
            long messages, int? days, out bool takeover)
        {
            var cap = Cap(usual);
            takeover = messages >= cap.Messages;
            if (takeover) return null;
            // A level already past the cap (the usual one fell since) is left where it is.
            var m = current.Messages >= cap.Messages
                ? current.Messages
                : (int)Math.Clamp(current.Messages + (long)Math.Ceiling((messages + 1 - current.Messages) / 2.0), current.Messages + 1L, cap.Messages);
            var d = days is { } has && has >= current.Days && current.Days < cap.Days
                ? (int)Math.Clamp(current.Days + (long)Math.Ceiling((has + 1 - current.Days) / 2.0), current.Days, cap.Days)
                : current.Days;
            return m == current.Messages && d == current.Days ? null : (m, d);
        }

        /// <summary>
        /// Raises the group's established level because of <paramref name="userId"/>, or proposes it while the
        /// group is only watching, and tells the admins.
        /// </summary>
        /// <param name="action">What was done to them: ban, kick, mute... or untrust.</param>
        internal static async Task RaiseAsync(long chatId, long userId, string action, IReadOnlyList<string> signs, long? labelId,
            string reason)
        {
            var policy = await DetectionSettings.GetAsync(chatId);
            var engine = policy.Engine;
            if (!engine.Learn) return;
            var usual = MemberTrust.UsualThresholds(chatId);
            var current = Effective(engine, usual);
            var (messages, since) = await MemberTrust.CountsAsync(chatId, userId);
            var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
            int? days = since is { } s ? (int)(now.UtcDateTime - s).TotalDays : null;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var title = await DetectionReports.TitleAsync(chatId);
            var who = DetectionReports.Mention(userId, DetectionReports.StoredName(userId));
            var daysText = days?.ToString() ?? "?";

            var raised = Raised(current, usual, messages, days, out var takeover);
            if (takeover)
            {
                await Repositories.DetectionLearning.AddAdjustmentAsync(chatId, Adjustment.Established, "takeover", engine.TrustMessages,
                    engine.TrustDays, engine.TrustMessages, engine.TrustDays, userId, labelId, false, now);
                await NotifyAsync(chatId, Methods.GetLocaleStringNoFormat(lang, "learnTakeover", title.FormatHTML(), ActionWord(lang, action), who,
                    messages, daysText, current.Messages, current.Days), null);
                return;
            }
            if (raised is not { } to) return; // at the highest level already: nothing to change

            var watching = engine.Observing(now);
            if (!watching)
            {
                await DetectionSettings.UpdateAsync(chatId, p => p with
                {
                    Engine = p.Engine with { TrustMessages = to.Messages, TrustDays = to.Days, TrustChangedAt = now }
                });
            }
            var id = await Repositories.DetectionLearning.AddAdjustmentAsync(chatId, Adjustment.Established, reason, engine.TrustMessages,
                engine.TrustDays, to.Messages, to.Days, userId, labelId, !watching, now);

            var text = signs.Count > 0
                ? Methods.GetLocaleStringNoFormat(lang, "learnRaised", title.FormatHTML(), ActionWord(lang, action), who, messages, daysText,
                    string.Join(", ", signs.Select(sign => DetectionReports.SignLabel(lang, sign).FormatHTML())), to.Messages, to.Days,
                    current.Messages, current.Days)
                : Methods.GetLocaleStringNoFormat(lang, "learnRaisedUntrust", title.FormatHTML(), who, messages, daysText, to.Messages, to.Days,
                    current.Messages, current.Days);
            await NotifyAsync(chatId, Watching(lang, text, engine, watching), watching ? null : id);
        }

        private static string Watching(XDocument lang, string text, EngineSettings engine, bool watching) =>
            watching
                ? text + "\n\n" + Methods.GetLocaleString(lang, "learnWatching",
                    (engine.ObserveUntil ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture))
                : text;

        private static string ActionWord(XDocument lang, string action) =>
            Methods.GetLocaleString(lang, $"learnAction_{(action is "tempban" ? "ban" : action is "tempmute" ? "mute" : action)}");

        // ------------------------------------------------------------------ telling the admins

        /// <summary>Sends <paramref name="text"/> to the group's report destinations; with Undo and Keep for a change that can be undone.</summary>
        private static async Task NotifyAsync(long chatId, string text, long? adjustmentId)
        {
            var opened = DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(Clock.UtcNow).ToUnixTimeSeconds());
            InlineKeyboardMarkup markup = null;
            if (adjustmentId is { } id)
            {
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var data = $"{Trigger}:{chatId}:{id}:{opened.ToUnixTimeSeconds()}";
                markup = Key.CreateMarkupFromMenu(new Menu(2)
                {
                    Buttons =
                    {
                        new InlineButton(Methods.GetLocaleString(lang, "learnUndoButton"), $"{data}:undo") { Style = KeyboardButtonStyle.Danger },
                        new InlineButton(Methods.GetLocaleString(lang, "learnKeepButton"), $"{data}:keep") { Style = KeyboardButtonStyle.Success }
                    }
                });
            }
            var copies = await DetectionReports.SendAsync(chatId, text, markup);
            if (adjustmentId is { } adjustment && copies.Count > 0)
                await Repositories.DetectionReports.AddCopiesAsync(ReportKey.ForLearn(chatId, adjustment, opened), copies);
        }

        /// <summary>Undo or Keep under a change: <c>dlearn:{chat}:{adjustment}:{sent}:{undo|keep}</c>, from an admin of the group.</summary>
        internal static async Task PressAsync(CallbackQuery call, long chatId, long adjustmentId, long sent, string decision)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (!PostgresStorage.Ready)
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            var adjustment = await Repositories.DetectionLearning.GetAdjustmentAsync(adjustmentId);
            var key = ReportKey.ForLearn(chatId, adjustmentId, DateTimeOffset.FromUnixTimeSeconds(sent));
            var stored = adjustment?.ChatId == chatId ? await Repositories.DetectionReports.GetAsync(key) : null;
            if (stored == null)
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "reportGone"));
                return;
            }
            if (decision == "undo" && stored.Decision == null && !await UndoAsync(adjustment, call.From.Id))
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "learnUndoStale"));
                return;
            }
            var (standing, by) = await Repositories.DetectionReports.DecideAsync(key, decision, call.From.Id, new DateTimeOffset(Clock.UtcNow));
            var mine = stored.Decision == null && standing == decision && by == call.From.Id;
            await Bot.Api.AnswerCallbackQuery(call.Id, mine
                ? Methods.GetLocaleString(lang, "reportDone")
                : DetectionReports.DecisionLine(lang, standing, by, html: false));
            await EditDecidedAsync(call, stored.Copies, DetectionReports.DecisionLine(lang, standing, by));
        }

        /// <summary>The message as it was, with who decided what and no buttons, on every copy.</summary>
        private static Task EditDecidedAsync(CallbackQuery call, IEnumerable<ReportCopy> copies, string line)
        {
            var message = call.Message;
            var html = message?.Entities is { Length: > 0 }
                ? Telegram.Bot.Extensions.HtmlText.ToHtml(message.Text, message.Entities)
                : message?.Text?.FormatHTML() ?? "";
            return DetectionReports.EditAsync(copies, html + "\n\n" + line, null);
        }

        /// <summary>Puts back what the change replaced, if nothing changed the level since. False otherwise.</summary>
        private static async Task<bool> UndoAsync(Adjustment adjustment, long by)
        {
            if (!adjustment.Applied || adjustment.UndoneAt != null) return false;
            var current = (await DetectionSettings.GetAsync(adjustment.ChatId)).Engine;
            Func<DetectionPolicy, DetectionPolicy> change;
            if (adjustment.Kind == Adjustment.Established)
            {
                if (current.TrustMessages != adjustment.ToValue || current.TrustDays != adjustment.ToDays) return false;
                change = p => p with { Engine = p.Engine with { TrustMessages = adjustment.FromValue, TrustDays = adjustment.FromDays } };
            }
            else
            {
                if (current.ReportAt != adjustment.ToValue) return false;
                change = p => p with { Engine = p.Engine with { ReportAt = adjustment.FromValue ?? EngineSettings.DefaultReportAt } };
            }
            await DetectionSettings.UpdateAsync(adjustment.ChatId, change);
            await Repositories.DetectionLearning.MarkUndoneAsync(adjustment.Id, by, new DateTimeOffset(Clock.UtcNow));
            return true;
        }

        /// <summary>Undo all, from the menu: the usual established level and the default report level again.</summary>
        internal static async Task<bool> ResetAsync(long chatId)
        {
            if (!PostgresStorage.Ready) return false;
            try
            {
                var engine = (await DetectionSettings.GetAsync(chatId)).Engine;
                var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
                await DetectionSettings.UpdateAsync(chatId, p => p with
                {
                    Engine = p.Engine with { TrustMessages = null, TrustDays = null, ReportAt = EngineSettings.DefaultReportAt, ProposedReportAt = null }
                });
                if (engine.TrustMessages != null || engine.TrustDays != null)
                    await Repositories.DetectionLearning.AddAdjustmentAsync(chatId, Adjustment.Established, "reset", engine.TrustMessages,
                        engine.TrustDays, null, null, null, null, true, now);
                if (engine.ReportAt != EngineSettings.DefaultReportAt)
                    await Repositories.DetectionLearning.AddAdjustmentAsync(chatId, Adjustment.ReportAt, "reset", engine.ReportAt, null,
                        EngineSettings.DefaultReportAt, null, null, null, true, now);
                return true;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Undoing what Detection learned in {chatId}");
                return false;
            }
        }

        // ------------------------------------------------------------------ nightly

        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Clock.WaitAsync(TimeSpan.FromHours(1), ct);
                    if (ct.IsCancellationRequested) return;
                    await NightlyAsync();
                }
            }, stop, "TrustLearning");

        /// <summary>
        /// Once a day per group, from <see cref="NightlyHour"/> on (any hour with <paramref name="anyHour"/>, for
        /// tests): the established level eases back, the report level is learned, and the admins are asked
        /// about restoring members who have done well. Groups of the other edition are left to it; with both in
        /// a group, whichever claims the day does it.
        /// </summary>
        internal static async Task NightlyAsync(bool anyHour = false)
        {
            if (!PostgresStorage.Ready) return;
            var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
            if (!anyHour && now.Hour < NightlyHour) return;
            IReadOnlyList<long> groups;
            try
            {
                groups = await Repositories.DetectionLearning.GroupsToLearnAsync(now - DecisionsOver);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Listing the groups Detection learns for");
                return;
            }
            var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            foreach (var chatId in groups)
            {
                try
                {
                    if (await ChatDetails.GetAsync(chatId) == null) continue; // not in this edition's groups
                    var claimed = false;
                    var policy = await DetectionSettings.UpdateAsync(chatId, p =>
                    {
                        claimed = p.Engine.LearnedOn != today;
                        return claimed ? p with { Engine = p.Engine with { LearnedOn = today } } : p;
                    });
                    if (!claimed) continue;
                    if (policy.Engine.Learn)
                    {
                        await EaseAsync(chatId, policy.Engine, now);
                        await LearnReportAtAsync(chatId, now);
                    }
                    await PromptRestoresAsync(chatId, now);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Detection's nightly learning in {chatId}");
                }
            }
        }

        /// <summary>A quarter of the way back to the usual level, a week after the last raise.</summary>
        internal static (int? Messages, int? Days) Eased((int? Messages, int? Days) learned, (int Messages, int Days) usual)
        {
            static int? Ease(int? value, int usual)
            {
                if (value is not { } v || v <= usual) return null;
                var next = v - (int)Math.Ceiling((v - usual) * EaseShare);
                return next <= usual ? null : next;
            }
            return (Ease(learned.Messages, usual.Messages), Ease(learned.Days, usual.Days));
        }

        private static async Task EaseAsync(long chatId, EngineSettings engine, DateTimeOffset now)
        {
            if (engine.TrustMessages == null && engine.TrustDays == null) return;
            if (engine.TrustChangedAt is { } changed && now - changed < EaseAfter) return;
            var usual = MemberTrust.UsualThresholds(chatId);
            var (m, d) = Eased((engine.TrustMessages, engine.TrustDays), usual);
            if (m == engine.TrustMessages && d == engine.TrustDays) return;
            await DetectionSettings.UpdateAsync(chatId, p => p with { Engine = p.Engine with { TrustMessages = m, TrustDays = d } });
            await Repositories.DetectionLearning.AddAdjustmentAsync(chatId, Adjustment.Established, "decay", engine.TrustMessages,
                engine.TrustDays, m, d, null, null, true, now);
            if (m == null && d == null)
            {
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                await NotifyAsync(chatId, Methods.GetLocaleStringNoFormat(lang, "learnBackToUsual",
                    (await DetectionReports.TitleAsync(chatId)).FormatHTML(), usual.Messages, usual.Days), null);
            }
        }

        /// <summary>
        /// The report level the decisions point to: the lowest score from which at least <see cref="Precision"/>
        /// of the decided members were removed or muted (with at least <see cref="MinAtLevel"/> of them); one
        /// higher when even the current one falls short. Null when there is too little to go on or nothing to
        /// change. One step at a time. Pure, for tests.
        /// </summary>
        internal static int? NextReportAt(IReadOnlyList<ScoredDecision> decisions, int current)
        {
            if (decisions.Count < MinDecisions) return null;
            (int N, double Bad) At(int level)
            {
                var from = decisions.Where(d => d.Score >= level).ToList();
                return (from.Count, from.Count == 0 ? 0 : (double)from.Count(d => d.Bad) / from.Count);
            }
            int? target = null;
            for (var level = MinReportAt; level <= MaxReportAt; level++)
            {
                var (n, bad) = At(level);
                if (n >= MinAtLevel && bad >= Precision)
                {
                    target = level;
                    break;
                }
            }
            if (target == null)
            {
                var (n, bad) = At(current);
                if (n >= MinAtLevel && bad < Precision) target = current + 1;
            }
            if (target is not { } t) return null;
            t = Math.Clamp(t, MinReportAt, MaxReportAt);
            var step = current + Math.Sign(t - current);
            return step == current ? null : step;
        }

        private static async Task LearnReportAtAsync(long chatId, DateTimeOffset now)
        {
            var decisions = await Repositories.DetectionLearning.ScoredDecisionsAsync(chatId, now - DecisionsOver);
            var engine = (await DetectionSettings.GetAsync(chatId)).Engine;
            if (NextReportAt(decisions, engine.ReportAt) is not { } next) return;
            var watching = engine.Observing(now);
            if (watching && engine.ProposedReportAt == next) return; // proposed already
            await DetectionSettings.UpdateAsync(chatId, p => p with
            {
                Engine = watching ? p.Engine with { ProposedReportAt = next } : p.Engine with { ReportAt = next, ProposedReportAt = null }
            });
            var id = await Repositories.DetectionLearning.AddAdjustmentAsync(chatId, Adjustment.ReportAt, "decisions", engine.ReportAt, null,
                next, null, null, null, !watching, now);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var bad = decisions.Count(d => d.Bad);
            var text = Methods.GetLocaleStringNoFormat(lang, "learnReportAt", (await DetectionReports.TitleAsync(chatId)).FormatHTML(),
                decisions.Count, bad, decisions.Count - bad, next, engine.ReportAt);
            await NotifyAsync(chatId, Watching(lang, text, engine, watching), watching ? null : id);
        }

        // ------------------------------------------------------------------ members set back to new

        private static async Task PromptRestoresAsync(long chatId, DateTimeOffset now)
        {
            foreach (var o in TrustOverrides.ForChat(chatId))
            {
                if (now - o.SetAt < RestoreAfter) continue;
                if (o.RestorePromptedAt is { } asked && now - asked < RestoreAfter) continue;
                var (messages, _) = await MemberTrust.CountsAsync(chatId, o.UserId);
                var since = messages - o.MessagesAtSet;
                if (since < MemberTrust.Thresholds(chatId).Messages) continue;
                if (await Repositories.DetectionLearning.AnythingAgainstSinceAsync(chatId, o.UserId, o.SetAt)) continue;
                await Repositories.DetectionLearning.MarkRestorePromptedAsync(chatId, o.UserId, now);
                TrustOverrides.Prompted(chatId, o.UserId, now);

                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var opened = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
                var data = $"{RestoreTrigger}:{chatId}:{o.UserId}:{opened.ToUnixTimeSeconds()}";
                var markup = Key.CreateMarkupFromMenu(new Menu(2)
                {
                    Buttons =
                    {
                        new InlineButton(Methods.GetLocaleString(lang, "trustRestoreButton"), $"{data}:restore") { Style = KeyboardButtonStyle.Success },
                        new InlineButton(Methods.GetLocaleString(lang, "trustKeepNewButton"), $"{data}:keepnew")
                    }
                });
                var text = Methods.GetLocaleStringNoFormat(lang, "trustRestorePrompt", (await DetectionReports.TitleAsync(chatId)).FormatHTML(),
                    DetectionReports.Mention(o.UserId, DetectionReports.StoredName(o.UserId)),
                    o.SetAt.UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), since);
                var copies = await DetectionReports.SendAsync(chatId, text, markup);
                if (copies.Count > 0)
                    await Repositories.DetectionReports.AddCopiesAsync(ReportKey.ForRestore(chatId, o.UserId, opened), copies);
            }
        }

        /// <summary>Restore or Keep new: <c>dtrust:{chat}:{user}:{sent}:{restore|keepnew}</c>, from an admin of the group.</summary>
        internal static async Task PressRestoreAsync(CallbackQuery call, long chatId, long userId, long sent, string decision)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (!PostgresStorage.Ready)
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            var key = ReportKey.ForRestore(chatId, userId, DateTimeOffset.FromUnixTimeSeconds(sent));
            var stored = await Repositories.DetectionReports.GetAsync(key);
            if (stored == null)
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "reportGone"));
                return;
            }
            var (standing, by) = await Repositories.DetectionReports.DecideAsync(key, decision, call.From.Id, new DateTimeOffset(Clock.UtcNow));
            var mine = stored.Decision == null && standing == decision && by == call.From.Id;
            if (mine && decision == "restore" && await TrustOverrides.RemoveAsync(chatId, userId) == null)
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            await Bot.Api.AnswerCallbackQuery(call.Id, mine
                ? Methods.GetLocaleString(lang, "reportDone")
                : DetectionReports.DecisionLine(lang, standing, by, html: false));
            await EditDecidedAsync(call, stored.Copies, DetectionReports.DecisionLine(lang, standing, by));
        }
    }
}
