using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The command menus Telegram shows: private chats, groups, group admins, and each staff member's
    /// own private chat. Set by the bot at startup from the <see cref="Attributes.Command"/>
    /// attributes, so the menus follow the code instead of whatever was last typed into BotFather.
    /// <see cref="Attributes.Command.Menu"/> places the exceptions. Copied from blackwolf's
    /// Werewolf Control/Onboarding/CommandMenus.cs.
    /// <para>
    /// Descriptions are the <c>cmd{trigger}</c> strings (English.xml). A language file that translates
    /// any of them gets its own menus under its language code; any other language code has its menus
    /// removed, so Telegram shows English. Staff menus are English, from the attribute's Description.
    /// </para>
    /// </summary>
    internal static class CommandMenus
    {
        /// <summary>Telegram's limit per menu (scope and language).</summary>
        internal const int MaxCommands = 100;

        internal enum Audience { Hidden, Private, PrivateAndGroups, Groups, GroupAdmins, GlobalAdmins, Devs }

        internal enum StaffRole { GlobalAdmin, Dev }

        internal sealed record MenuCommand(string Command, Command Attribute, int Order)
        {
            internal Audience Audience => AudienceOf(Attribute);
        }

        internal static Audience AudienceOf(Command c)
        {
            if (c.Menu == CommandMenu.Hidden || c.UploadAdmin) return Audience.Hidden;
            if (c.DevOnly || c.Menu == CommandMenu.Devs) return Audience.Devs;
            if (c.GlobalAdminOnly) return Audience.GlobalAdmins;
            if (c.GroupAdminOnly || c.Menu == CommandMenu.GroupAdmins) return Audience.GroupAdmins;
            if (c.Menu == CommandMenu.Private) return Audience.Private;
            if (c.InGroupOnly || c.Menu == CommandMenu.Groups) return Audience.Groups;
            return Audience.PrivateAndGroups;
        }

        /// <summary>Every command, in the order of the code. Telegram only takes lower case; triggers match in any case.</summary>
        internal static IReadOnlyList<MenuCommand> All { get; } = typeof(Commands).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(Command), false).Cast<Command>()
                .Select(a => new MenuCommand(a.Trigger.ToLowerInvariant(), a, m.MetadataToken)))
            .OrderBy(c => c.Order)
            .ToList();

        /// <summary>The most used come first in each menu, in this order; the rest follow in the order of the code.</summary>
        private static readonly string[] First =
        {
            "rules", "admin", "report", "kickme", "id", "info", "link", "about", "adminlist",
            "warn", "ban", "kick", "mute", "tempban", "unban", "menu", "raid",
            "start", "help", "support",
        };

        private static string[] For(params Audience[] audiences) => All
            .Where(c => audiences.Contains(c.Audience))
            .OrderBy(c => c.Audience == Audience.Groups ? 0 : c.Audience == Audience.GroupAdmins ? 1 : 2)
            .ThenBy(c => Array.IndexOf(First, c.Command) is var i && i >= 0 ? i : First.Length)
            .ThenBy(c => c.Order)
            .Select(c => c.Command)
            .ToArray();

        internal static readonly string[] PrivateCommands = For(Audience.Private, Audience.PrivateAndGroups);
        internal static readonly string[] GroupCommands = For(Audience.Groups, Audience.PrivateAndGroups);
        internal static readonly string[] AdminCommands = For(Audience.Groups, Audience.PrivateAndGroups, Audience.GroupAdmins);

        /// <summary>The three public menus. Admins see the group menu plus the admin commands.</summary>
        internal static IReadOnlyList<(BotCommandScope Scope, string[] Commands)> Menus { get; } = new List<(BotCommandScope, string[])>
        {
            (BotCommandScope.AllPrivateChats(), PrivateCommands),
            (BotCommandScope.AllGroupChats(), GroupCommands),
            (BotCommandScope.AllChatAdministrators(), AdminCommands),
        };

        /// <summary>The staff commands a rank may use, devs' first (devs see every staff command).</summary>
        internal static IReadOnlyList<MenuCommand> StaffCommands(StaffRole role)
        {
            var audiences = new List<Audience> { Audience.GlobalAdmins };
            if (role >= StaffRole.Dev) audiences.Insert(0, Audience.Devs);
            return All.Where(c => audiences.Contains(c.Audience))
                .OrderBy(c => audiences.IndexOf(c.Audience)).ThenBy(c => c.Order).ToList();
        }

        internal static string StaffPrefix(Audience audience) =>
            audience == Audience.Devs ? "(dev only) " : audience == Audience.GlobalAdmins ? "(global admin only) " : "";

        /// <summary>A staff member's own private menu (English): the private menu, then their staff commands.</summary>
        internal static List<BotCommand> BuildStaff(StaffRole role, Func<string, string> text) =>
            Build(PrivateCommands, text)
                .Concat(StaffCommands(role).Select(c => new BotCommand
                {
                    Command = c.Command,
                    Description = Truncate(StaffPrefix(c.Audience) + (c.Attribute.Description ?? c.Command), 256),
                }))
                .ToList();

        /// <summary>Language key of a command's description: "ban" is "cmdban".</summary>
        internal static string DescriptionKey(string command) => "cmd" + command;

        internal static List<BotCommand> Build(IEnumerable<string> commands, Func<string, string> text) =>
            commands.Select(c => new BotCommand { Command = c, Description = Truncate(text(DescriptionKey(c)), 256) }).ToList();

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        /// <summary>A string as plain text from one file only, no fallback; null when the file lacks it.</summary>
        internal static string Plain(XDocument doc, string key)
        {
            var value = doc?.Descendants("string").FirstOrDefault(x => x.Attribute("key")?.Value == key)
                ?.Descendants("value").FirstOrDefault()?.Value;
            return value?.Replace("\\n", "\n").Trim();
        }

        /// <summary>The languages Enforcer has files for (English included: its own code only ever duplicates the default).</summary>
        internal static readonly string[] LanguageCodes = { "en", "de", "es", "fa", "id", "it", "pt", "ru", "tr", "zh" };

        private static bool SameCommands(IReadOnlyList<BotCommand> a, IReadOnlyList<BotCommand> b) =>
            a.Count == b.Count && a.Zip(b, (x, y) => x.Command == y.Command && x.Description == y.Description).All(same => same);

        /// <summary>The staff, from <see cref="Constants"/>: devs, then the global admins who are not devs.</summary>
        internal static Dictionary<long, StaffRole> Staff()
        {
            var staff = Constants.GlobalAdmins.ToDictionary(id => id, _ => StaffRole.GlobalAdmin);
            foreach (var dev in Constants.Devs) staff[dev] = StaffRole.Dev;
            return staff;
        }

#if PREMIUM
        private const string StaffKey = "commandmenus:premium:staff";
#else
        private const string StaffKey = "commandmenus:normal:staff";
#endif

        /// <summary>What <see cref="SyncAsync"/> changed, and the descriptions English.xml lacks.</summary>
        internal sealed class SyncResult
        {
            internal List<string> Changed { get; } = new();
            internal SortedSet<string> MissingFromEnglish { get; } = new(StringComparer.Ordinal);
            internal List<string> StaffFailed { get; } = new();

            internal string Summary(string bot)
            {
                var lines = new List<string> { $"Command menus (@{bot}):" };
                lines.Add(Changed.Count == 0 ? "Already up to date" : "Updated: " + string.Join(", ", Changed));
                if (StaffFailed.Count > 0) lines.Add($"Staff menus failed: {string.Join("; ", StaffFailed.Distinct())}");
                if (MissingFromEnglish.Count > 0)
                    lines.Add($"⚠️ {MissingFromEnglish.Count} missing from English.xml, the menus show the key: {string.Join(", ", MissingFromEnglish)}");
                return string.Join("\n", lines);
            }
        }

        /// <summary>
        /// Sets the English menus (no language code: every language without its own), then one set
        /// per language code whose file translates at least one description, removing the others, then
        /// each staff member's menu. Menus already right are left alone, so a restart costs only reads.
        /// </summary>
        internal static async Task<SyncResult> SyncAsync(ITelegramBotClient api)
        {
            var result = new SyncResult();
            var englishDoc = Program.LangaugeList.FirstOrDefault(l => l.Name == "English")?.Doc;
            // A description English lacks shows as its key on purpose, so the gap is visible.
            string English(string key)
            {
                var value = Plain(englishDoc, key);
                if (value != null) return value;
                result.MissingFromEnglish.Add(key);
                return key;
            }

            foreach (var (scope, commands) in Menus)
                if (await SetIfDifferent(api, scope, null, Build(commands, English)))
                    result.Changed.Add(scope.Type.ToString());

            // Telegram takes two-letter codes only; a code two files claim goes to the first. The codes
            // of our languages are always looked at, file or not, so a menu set for one in BotFather is
            // removed rather than shown instead of English.
            var files = Program.LangaugeList
                .Where(l => l.IEFT is { Length: 2 })
                .GroupBy(l => l.IEFT).ToDictionary(g => g.Key, g => g.First().Doc);
            foreach (var code in files.Keys.Union(LanguageCodes).Distinct().OrderBy(c => c, StringComparer.Ordinal))
            {
                var doc = code == "en" ? null : files.GetValueOrDefault(code);
                string Localized(string key) => Plain(doc, key) ?? English(key);
                foreach (var (scope, commands) in Menus)
                {
                    var list = Build(commands, Localized);
                    var translated = !SameCommands(list, Build(commands, English));
                    if (translated ? await SetIfDifferent(api, scope, code, list) : await DeleteIfSet(api, scope, code))
                        result.Changed.Add($"{scope.Type}/{code}");
                }
            }

            await SyncStaffAsync(api, result, English);
            return result;
        }

        /// <summary>Each staff member's private chat gets <see cref="BuildStaff"/>; former staff lose theirs.</summary>
        private static async Task SyncStaffAsync(ITelegramBotClient api, SyncResult result, Func<string, string> english)
        {
            var staff = Staff();
            var before = (await Redis.db.SetMembersAsync(StaffKey))
                .Select(v => long.TryParse(v.ToString(), out var id) ? id : 0).Where(id => id != 0).ToList();
            foreach (var member in staff)
            {
                try
                {
                    if (await SetIfDifferent(api, BotCommandScope.Chat(member.Key), null, BuildStaff(member.Value, english)))
                        result.Changed.Add("staff");
                    await Redis.db.SetAddAsync(StaffKey, member.Key);
                }
                catch (Exception e)
                {
                    // Usually a staff member who never started this bot: there is no private chat to set it for.
                    result.StaffFailed.Add(Bot.AsApiError(e)?.Message ?? e.GetBaseException().Message);
                }
            }
            foreach (var former in before.Where(id => !staff.ContainsKey(id)))
            {
                try
                {
                    if (await DeleteIfSet(api, BotCommandScope.Chat(former))) result.Changed.Add("former staff");
                    await Redis.db.SetRemoveAsync(StaffKey, former);
                }
                catch (Exception e)
                {
                    result.StaffFailed.Add(Bot.AsApiError(e)?.Message ?? e.GetBaseException().Message);
                }
            }
        }

        private static async Task<bool> SetIfDifferent(ITelegramBotClient api, BotCommandScope scope, string code, List<BotCommand> commands)
        {
            var current = await api.GetMyCommands(scope, code) ?? Array.Empty<BotCommand>();
            if (SameCommands(current, commands)) return false;
            await api.SetMyCommands(commands, scope, code);
            return true;
        }

        private static async Task<bool> DeleteIfSet(ITelegramBotClient api, BotCommandScope scope, string code = null)
        {
            var current = await api.GetMyCommands(scope, code);
            if (current == null || current.Length == 0) return false;
            await api.DeleteMyCommands(scope, code);
            return true;
        }

        /// <summary>
        /// At startup, in the background: syncs, and tells the error chat when something changed or a
        /// description is missing. A failure is reported and the bot carries on with the old menus.
        /// </summary>
        internal static async Task SyncAndReportAsync()
        {
            try
            {
                var result = await SyncAsync(Bot.Api);
                if (result.Changed.Count > 0 || result.MissingFromEnglish.Count > 0 || result.StaffFailed.Count > 0)
                    await Bot.Api.SendMessage(Bot.ErrorChatId, result.Summary(Bot.Me?.Username));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Setting the command menus");
            }
        }
    }
}
