using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// /filter: a group's own regex rules (Detection step 5, design section 5). A new rule starts on
    /// trial for <see cref="TrialDays"/>: it is recorded, never acted on, and stays so until an admin
    /// makes it live. The presets are in the filters menu.
    /// <code>
    /// /filter add &lt;name|username|text&gt; &lt;action&gt; &lt;regex&gt;
    /// /filter try &lt;regex&gt; &lt;text&gt;
    /// /filter test &lt;id|regex&gt;
    /// /filter list | live &lt;id&gt; | trial &lt;id&gt; [days] | del &lt;id&gt;
    /// </code>
    /// </summary>
    public static partial class Commands
    {
        internal const int TrialDays = 7;
        internal const int MaxTrialDays = 90;
        private const int TestMembers = 5000;
        private const int TestExamples = 10;

        // Admin-only: the rules are what spammers would want to read.
        [Command(Trigger = "filter", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Filter(Update update, string[] args)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var input = (args.Length > 1 ? args[1] : null)?.Trim() ?? "";
            var space = input.IndexOf(' ');
            var verb = (space < 0 ? input : input[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : input[(space + 1)..].Trim();

            string reply;
            switch (verb)
            {
                case "add":
                    reply = FilterAdd(chatId, message.From.Id, rest, lang);
                    break;
                case "try":
                    reply = FilterTry(rest, lang);
                    break;
                case "test":
                    reply = FilterTest(chatId, message.From.Id, rest, lang);
                    break;
                case "list":
                    reply = FilterList(chatId, lang);
                    break;
                case "live":
                    reply = FilterChange(chatId, rest, lang, (rule, _) => rule with { Mode = FilterRule.Live, TrialUntil = null }, "filterMadeLive");
                    break;
                case "trial":
                    reply = FilterChange(chatId, rest, lang, (rule, days) => rule with
                    {
                        Mode = FilterRule.Trial,
                        TrialUntil = Seconds(Clock.UtcNow.AddDays(days))
                    }, "filterTrialSet");
                    break;
                case "del":
                    reply = FilterChange(chatId, rest, lang, (_, _) => null, "filterDeleted");
                    break;
                default:
                    reply = Methods.GetLocaleString(lang, "filterUsage", TrialDays);
                    break;
            }
            if (reply != null) Bot.SendReply(reply, update);
            if (verb is "add" or "live" or "trial" or "del") Service.LogCommand(update, message.Text);
        }

        private static DateTimeOffset Seconds(DateTime utc) =>
            DateTimeOffset.FromUnixTimeSeconds(new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds());

        /// <summary>Splits off the first word.</summary>
        private static (string Word, string Remainder) FirstWord(string text)
        {
            var space = text.IndexOf(' ');
            return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
        }

        private static string FilterAdd(long chatId, long adminId, string rest, XDocument lang)
        {
            var (target, afterTarget) = FirstWord(rest);
            var (action, pattern) = FirstWord(afterTarget);
            target = target.ToLowerInvariant();
            action = action.ToLowerInvariant();
            if (pattern.Length == 0) return Methods.GetLocaleString(lang, "filterUsage", TrialDays);
            if (!FilterRule.Targets.Contains(target))
                return Methods.GetLocaleString(lang, "filterBadTarget", string.Join(", ", FilterRule.Targets));
            if (!DetectionPolicy.Actions.Contains(action))
                return Methods.GetLocaleString(lang, "filterBadAction", string.Join(", ", DetectionPolicy.Actions));
            if (!FilterRegex.TryCompile(pattern, out _, out var error))
                return Methods.GetLocaleString(lang, "filterInvalid", error);

            var current = DetectionSettings.For(chatId).Filters;
            if (current.OwnRules.Any(r => r.Target == target && r.Pattern == pattern))
                return Methods.GetLocaleString(lang, "filterExists");
            if (current.OwnRules.Count() >= FilterRegex.MaxRulesPerGroup)
                return Methods.GetLocaleString(lang, "filterLimit", FilterRegex.MaxRulesPerGroup);

            var rule = new FilterRule(0, FilterRule.RegexKind, null, target, pattern, action, true, FilterRule.Trial,
                Seconds(Clock.UtcNow.AddDays(TrialDays)), adminId);
            var saved = DetectionSettings.TryUpdate(chatId, p => p with
            {
                Filters = p.Filters with { Rules = p.Filters.Rules.Append(rule).ToList() }
            });
            var added = saved?.Filters.OwnRules.FirstOrDefault(r => r.Target == target && r.Pattern == pattern);
            return added == null
                ? Methods.GetLocaleString(lang, "failed")
                : Methods.GetLocaleString(lang, "filterAdded", added.Id, TrialDays);
        }

        /// <summary>
        /// The regex and the text after it: the first word, or a regex with spaces between slashes
        /// (<c>/free +crypto/ Get FREE crypto</c>).
        /// </summary>
        internal static (string Pattern, string Text) RegexAndText(string rest)
        {
            if (rest.StartsWith('/'))
            {
                var end = rest.IndexOf("/ ", 1, StringComparison.Ordinal);
                if (end > 1) return (rest[1..end], rest[(end + 2)..].Trim());
            }
            return FirstWord(rest);
        }

        private static string FilterTry(string rest, XDocument lang)
        {
            var (pattern, text) = RegexAndText(rest);
            if (pattern.Length == 0 || text.Length == 0) return Methods.GetLocaleString(lang, "filterUsage", TrialDays);
            if (!FilterRegex.TryCompile(pattern, out _, out var error))
                return Methods.GetLocaleString(lang, "filterInvalid", error);
            return Methods.GetLocaleString(lang, FilterRegex.Matches(pattern, text) ? "filterTryMatch" : "filterTryNoMatch");
        }

        /// <summary>A rule of the group by its id, or null.</summary>
        private static FilterRule OwnRule(long chatId, string id) =>
            long.TryParse(id.TrimStart('#'), out var number)
                ? DetectionSettings.For(chatId).Filters.OwnRules.FirstOrDefault(r => r.Id == number)
                : null;

        /// <summary>
        /// Tries a rule of the group (by id) or a regex on the names and usernames of the members seen in
        /// the last 30 days, and sends the admin the result in private: names stay out of the group.
        /// </summary>
        private static string FilterTest(long chatId, long adminId, string rest, XDocument lang)
        {
            if (rest.Length == 0) return Methods.GetLocaleString(lang, "filterUsage", TrialDays);
            var rule = OwnRule(chatId, rest);
            var pattern = rule?.Pattern ?? rest;
            if (!FilterRegex.TryCompile(pattern, out _, out var error))
                return Methods.GetLocaleString(lang, "filterInvalid", error);
            if (!PostgresStorage.Ready) return Methods.GetLocaleString(lang, "failed");

            IReadOnlyList<(string Name, string Username)> members;
            try
            {
                members = Repositories.DetectionLog.RecentMemberNamesAsync(chatId, Clock.UtcNow.AddDays(-30), TestMembers)
                    .GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"/filter test in {chatId}");
                return Methods.GetLocaleString(lang, "failed");
            }

            var checkName = rule == null || rule.Target == "name";
            var checkUsername = rule == null || rule.Target == "username";
            var matches = members.Where(m => checkName && FilterRegex.Matches(pattern, m.Name) ||
                                             checkUsername && FilterRegex.Matches(pattern, m.Username)).ToList();
            var examples = string.Join("\n", matches.Take(TestExamples)
                .Select(m => WebUtility.HtmlEncode(m.Username != null ? $"{m.Name} (@{m.Username})" : m.Name)));
            var result = matches.Count == 0
                ? Methods.GetLocaleString(lang, "filterTestNone", members.Count)
                : Methods.GetLocaleString(lang, "filterTestResult", matches.Count, members.Count) + "\n" + examples;
            var sent = Bot.Send(result, adminId);
            return Methods.GetLocaleString(lang, sent != null ? "filterTestSent" : "filterTestStartMe");
        }

        private static string FilterList(long chatId, XDocument lang)
        {
            var own = DetectionSettings.For(chatId).Filters.OwnRules.ToList();
            if (own.Count == 0) return Methods.GetLocaleString(lang, "filterListEmpty", TrialDays);
            IReadOnlyDictionary<string, (long Hits, long Removed)> stats = new Dictionary<string, (long, long)>();
            if (PostgresStorage.Ready)
            {
                try
                {
                    stats = Repositories.DetectionLog.SignStatsAsync(chatId, own.Select(r => r.Sign).ToList()).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    // The list without the counts.
                    ErrorReporter.Report(e, $"Counting the filter hits of {chatId}");
                }
            }
            var lines = own.Select(r =>
            {
                stats.TryGetValue(r.Sign, out var s);
                var mode = !r.OnTrial ? Methods.GetLocaleString(lang, "filterModeLive")
                    : r.TrialUntil > Clock.UtcNow ? Methods.GetLocaleString(lang, "filterModeTrial", r.TrialUntil.Value.UtcDateTime.ToString("yyyy-MM-dd"))
                    : Methods.GetLocaleString(lang, "filterModeTrialOver");
                return Methods.GetLocaleString(lang, "filterListRule", r.Id, r.Target, r.Action, mode, s.Hits, s.Removed) +
                       $"\n<code>{WebUtility.HtmlEncode(r.Pattern)}</code>";
            });
            return Methods.GetLocaleString(lang, "filterListHeader") + "\n\n" + string.Join("\n\n", lines);
        }

        /// <summary>live, trial and del: <paramref name="change"/> returns the rule as it becomes, or null to remove it.</summary>
        private static string FilterChange(long chatId, string rest, XDocument lang, Func<FilterRule, int, FilterRule> change, string done)
        {
            var (id, more) = FirstWord(rest);
            var rule = OwnRule(chatId, id);
            if (rule == null) return Methods.GetLocaleString(lang, "filterNotFound", id);
            var days = int.TryParse(more, out var d) ? Math.Clamp(d, 1, MaxTrialDays) : TrialDays;
            var changed = change(rule, days);
            var saved = DetectionSettings.TryUpdate(chatId, p => p with
            {
                Filters = p.Filters with
                {
                    Rules = p.Filters.Rules
                        .Select(r => r.Kind == FilterRule.RegexKind && r.Id == rule.Id ? changed : r)
                        .Where(r => r != null).ToList()
                }
            });
            return saved == null ? Methods.GetLocaleString(lang, "failed") : Methods.GetLocaleString(lang, done, rule.Id, days);
        }
    }
}
