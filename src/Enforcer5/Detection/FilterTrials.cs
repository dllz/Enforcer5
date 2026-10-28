using System;
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
    /// When an admin's own filter rule ends its trial (design section 5, build step 7), the group's report
    /// destinations get what it found (how often it matched, and how many of those members were later
    /// banned or removed) with Make live, 7 more days and Delete. Until someone decides, the rule keeps
    /// recording only. Checked every <see cref="Every"/>; a rule is claimed in PostgreSQL before its notice
    /// goes out, so with both editions in a group it is sent once, by whichever is in the group.
    /// </summary>
    internal static class FilterTrials
    {
        internal const string Trigger = "ftrial";
        internal static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
        internal const int ExtendDays = 7;

        /// <summary>Trials that ended longer ago than this are not announced any more (the bot was gone meanwhile).</summary>
        internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);

        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Clock.WaitAsync(Every, ct);
                    if (ct.IsCancellationRequested) return;
                    await CheckOnceAsync();
                }
            }, stop, "FilterTrials");

        /// <summary>Tells the admins about every trial that has ended and nobody was told about yet.</summary>
        internal static async Task CheckOnceAsync()
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
                foreach (var due in await Repositories.DetectionReports.DueTrialsAsync(now, now - GiveUpAfter, 50))
                {
                    // A group of the other edition only: it tells them.
                    if (await ChatDetails.GetAsync(due.ChatId) == null) continue;
                    if (!await Repositories.DetectionReports.ClaimTrialAsync(due.FilterId, due.TrialUntil, now)) continue;
                    await NotifyAsync(due, now);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Telling admins about filter trials that ended");
            }
        }

        private static async Task NotifyAsync(DueTrial due, DateTimeOffset now)
        {
            var chatId = due.ChatId;
            var rule = (await DetectionSettings.GetAsync(chatId)).Filters.OwnRules.FirstOrDefault(r => r.Id == due.FilterId);
            if (rule == null) return;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var stats = await Repositories.DetectionLog.SignStatsAsync(chatId, new[] { rule.Sign });
            var (hits, removed) = stats.TryGetValue(rule.Sign, out var found) ? found : (0, 0);
            var title = (await ChatDetails.GetAsync(chatId))?.Title ?? chatId.ToString();
            var text = Text(lang, rule, title, hits, removed, null);
            var key = ReportKey.ForTrial(chatId, rule.Id, DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()));
            var copies = await DetectionReports.SendAsync(chatId, text, Buttons(lang, chatId, rule.Id, key.OpenedAt));
            if (copies.Count > 0) await Repositories.DetectionReports.AddCopiesAsync(key, copies);
        }

        internal static string Text(XDocument lang, FilterRule rule, string title, long hits, long removed, string decisionLine)
        {
            // Not GetLocaleString: it would escape the markup around the pattern. Every argument is escaped here.
            var text = Methods.GetLocaleStringNoFormat(lang, "filterTrialEnded", rule.Id, title.FormatHTML(),
                // The target as /filter takes it: name, username or text.
                rule.Target, rule.Pattern.FormatHTML(),
                Methods.GetLocaleString(lang, $"filterAction_{rule.Action ?? "off"}"), hits, removed);
            return decisionLine == null ? text : text + "\n\n" + decisionLine;
        }

        private static InlineKeyboardMarkup Buttons(XDocument lang, long chatId, long filterId, DateTimeOffset opened)
        {
            var data = $"{Trigger}:{chatId}:{filterId}:{opened.ToUnixTimeSeconds()}";
            var menu = new Menu(1)
            {
                Buttons =
                {
                    new InlineButton(Methods.GetLocaleString(lang, "filterTrialMakeLive"), $"{data}:live") { Style = KeyboardButtonStyle.Success },
                    new InlineButton(Methods.GetLocaleString(lang, "filterTrialExtend", ExtendDays), $"{data}:extend"),
                    new InlineButton(Methods.GetLocaleString(lang, "filterTrialDelete"), $"{data}:delete") { Style = KeyboardButtonStyle.Danger }
                }
            };
            return Key.CreateMarkupFromMenu(menu);
        }

        /// <summary>
        /// A button under a notice: <c>ftrial:{chat}:{filter}:{sent}:{live|extend|delete}</c>, from an admin
        /// of the group. The first decision stands and every copy says it.
        /// </summary>
        internal static async Task PressAsync(CallbackQuery call, long chatId, long filterId, long sent, string decision)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var actor = call.From.Id;
            if (!PostgresStorage.Ready)
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            var key = ReportKey.ForTrial(chatId, filterId, DateTimeOffset.FromUnixTimeSeconds(sent));
            var stored = await Repositories.DetectionReports.GetAsync(key);
            var rule = (await DetectionSettings.GetAsync(chatId)).Filters.OwnRules.FirstOrDefault(r => r.Id == filterId);
            if (stored == null)
            {
                await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "reportGone"));
                return;
            }
            var (standing, by) = await Repositories.DetectionReports.DecideAsync(key, decision, actor, new DateTimeOffset(Clock.UtcNow));
            var mine = standing == decision && by == actor && stored.Decision == null;
            if (mine && rule != null)
            {
                var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
                FilterRule changed = decision switch
                {
                    "live" => rule with { Mode = FilterRule.Live, TrialUntil = null },
                    "extend" => rule with { Mode = FilterRule.Trial, TrialUntil = DateTimeOffset.FromUnixTimeSeconds(now.AddDays(ExtendDays).ToUnixTimeSeconds()) },
                    _ => null
                };
                var saved = DetectionSettings.TryUpdate(chatId, p => p with
                {
                    Filters = p.Filters with
                    {
                        Rules = p.Filters.Rules
                            .Select(r => r.Kind == FilterRule.RegexKind && r.Id == filterId ? changed : r)
                            .Where(r => r != null).ToList()
                    }
                });
                if (saved == null)
                {
                    await Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                    return;
                }
            }
            var line = DetectionReports.DecisionLine(lang, $"trial_{standing}", by);
            await Bot.Api.AnswerCallbackQuery(call.Id, mine
                ? Methods.GetLocaleString(lang, "reportDone")
                : DetectionReports.DecisionLine(lang, $"trial_{standing}", by, html: false));
            // The notice as it was, with who decided what and no buttons.
            var title = (await ChatDetails.GetAsync(chatId))?.Title ?? chatId.ToString();
            var stats = await Repositories.DetectionLog.SignStatsAsync(chatId, new[] { $"filter:{filterId}" });
            var (hits, removed) = stats.TryGetValue($"filter:{filterId}", out var found) ? found : (0, 0);
            var shown = rule ?? new FilterRule(filterId, FilterRule.RegexKind, null, "text", "", null, false, FilterRule.Trial, null, null);
            await DetectionReports.EditAsync(stored.Copies, Text(lang, shown, title, hits, removed, line), null);
        }
    }
}
