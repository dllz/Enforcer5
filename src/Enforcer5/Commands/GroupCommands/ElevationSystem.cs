using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "elevate", InGroupOnly = true, Menu = Attributes.CommandMenu.GroupAdmins)]
        public static void ElevateUser(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                if (userid == Bot.Me.Id)
                    return;
                if (update.Message.From.Id == userid)
                    return;
                var chat = update.Message.Chat.Id;
                var role = Bot.Api.GetChatMember(chat, update.Message.From.Id);
                var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id.ToString()).Result;
                var upriv = Redis.db.SetContainsAsync($"chat:{chat}:deauth", update.Message.From.Id).Result;
                var blocked = Redis.db.StringGetAsync($"chat:{chat}:blockList:{update.Message.From.Id}").Result;
                var alreadyElevated = Redis.db.SetContainsAsync($"chat:{chat}:mod", userid).Result;
                if (alreadyElevated)
                    return;
                if ((role.Result.Status == ChatMemberStatus.Creator || priv))
                {
                    Redis.db.SetAddAsync($"chat:{chat}:mod", userid);
                    Redis.db.StringSetAsync($"chat:{chat}:adminses:{userid}", "true");
                    if (blocked.HasValue)
                        Redis.db.SetRemoveAsync($"chat:{chat}:blockList", userid);
                    Bot.SendReply(Methods.GetLocaleString(lang, "evlavated", Ids.Named(userid, Redis.db.HashGetAsync($"user:{userid}", "name").Result), Ids.Named(update.Message.From.Id, update.Message.From.FirstName)), update);
                    Service.LogCommand(update, update.Message.Text);
                }
                else if (!upriv & blocked.HasValue == false & Methods.IsGroupAdmin(update))
                {
                    Redis.db.StringSetAsync($"chat:{chat}:adminses:{userid}", "true", TimeSpan.FromMinutes(30));
                    Redis.db.SetAddAsync($"chat:{chat}:modlog",
                        $"{Redis.db.HashGetAsync($"user:{userid}", "name").Result.ToString()} ({userid}) by {update.Message.From.FirstName} ({update.Message.From.Id}) at {System.DateTime.UtcNow} UTC");
                    Redis.db.StringSetAsync($"chat:{chat}:blockList:{userid}", userid, TimeSpan.FromMinutes(31));
                    Bot.SendReply(Methods.GetLocaleString(lang, "evlavated", Ids.Named(userid, Redis.db.HashGetAsync($"user:{userid}", "name").Result), Ids.Named(update.Message.From.Id, update.Message.From.FirstName)), update);
                    Service.LogCommand(update, update.Message.Text);
                }


            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    // An unknown @username is the admin's typo, not a bug.
                    if (e.Message != "UnableToResolveUsername")
                        ErrorReporter.Report(e, "Elevating user");
                    Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang);
                }
            }
        }

        [Command(Trigger = "deelevate", InGroupOnly = true, Menu = Attributes.CommandMenu.GroupAdmins)]
        public static void deElavateUser(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                if (userid == Bot.Me.Id)
                    return;
                var chat = update.Message.Chat.Id;
                var role = Bot.Api.GetChatMember(chat, update.Message.From.Id);
                var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
                if (role.Result.Status == ChatMemberStatus.Creator || priv)
                {
                    // Removing the elevation; a "false" here used to hide a real admin as well.
                    Redis.db.KeyDeleteAsync($"chat:{chat}:adminses:{userid}").Wait();
                    Redis.db.SetRemoveAsync($"chat:{chat}:mod", userid);
                    Bot.SendReply(Methods.GetLocaleString(lang, "devlavated", Ids.Named(userid, Redis.db.HashGetAsync($"user:{userid}", "name").Result), Ids.Named(update.Message.From.Id, update.Message.From.FirstName)), update);
                    Service.LogCommand(update, update.Message.Text);
                }
            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    // An unknown @username is the admin's typo, not a bug.
                    if (e.Message != "UnableToResolveUsername")
                        ErrorReporter.Report(e, "De-elevating user");
                    Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang);
                }
            }
        }

        [Command(Trigger = "elevatelog", GroupAdminOnly = true, InGroupOnly = true)]
        public static void ElevateLog(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            var log = Redis.db.SetMembersAsync($"chat:{update.Message.Chat.Id}:modlog").Result.Select(e => e.ToString()).ToList();
            // Stored as "Name (id) by Name (id) at …": escaped for HTML, the names mentions, the ids copyable.
            var shown = log.Select(ShownModLog);
            Bot.SendReply($"{Methods.GetLocaleString(lang, "prevMods")} {string.Join("\n", shown)}", update);
        }

        private static readonly System.Text.RegularExpressions.Regex ModLogLine = new(
            @"^(?<who>.*) \((?<whoId>-?\d+)\) by (?<by>.*) \((?<byId>-?\d+)\)(?<rest> at .*)$",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        /// <summary>A stored /elevatelog line as HTML: the names mentions, the ids copyable.</summary>
        internal static string ShownModLog(string line)
        {
            var m = ModLogLine.Match(line);
            if (m.Success && long.TryParse(m.Groups["whoId"].Value, out var who) && long.TryParse(m.Groups["byId"].Value, out var by))
                return $"{Ids.NamedHtml(who, m.Groups["who"].Value)} by {Ids.NamedHtml(by, m.Groups["by"].Value)}{m.Groups["rest"].Value.FormatHTML()}";
            return System.Text.RegularExpressions.Regex.Replace(line.FormatHTML(), @"\((-?\d+)\)", g => $"({Ids.Mark(g.Groups[1].Value)})");
        }

        [Command(Trigger = "auth", InGroupOnly = true, Menu = Attributes.CommandMenu.GroupAdmins)]
        public static void AuthUser(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            if (userid == Bot.Me.Id)
                return;
            var role = Bot.Api.GetChatMember(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if ((role.Result.Status == ChatMemberStatus.Creator || priv) || update.Message.From.Id == Constants.Devs[0])
            {
                var lang = Methods.GetGroupLanguage(update.Message,true).Doc;

                Redis.db.SetAddAsync($"chat:{chat}:auth", userid);
                Bot.SendReply(Methods.GetLocaleString(lang, "auth", Ids.Mark(userid), Ids.Mark(update.Message.From.Id)), update);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "blockelevate", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Blockelevate(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            if (userid == Bot.Me.Id)
                return;
            var role = Bot.Api.GetChatMember(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator | priv)
            {
                var lang = Methods.GetGroupLanguage(update.Message,true).Doc;

                Redis.db.SetAddAsync($"chat:{chat}:deauth", userid);
                Bot.SendReply(Methods.GetLocaleString(lang, "elevateBlocked", Ids.Mark(userid), Ids.Mark(update.Message.From.Id)), update);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "unblockelevate", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Unblockelevate(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            if (userid == Bot.Me.Id)
                return;
            var role = Bot.Api.GetChatMember(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator | priv)
            {
                var lang = Methods.GetGroupLanguage(update.Message,true).Doc;

                Redis.db.SetRemoveAsync($"chat:{chat}:deauth", userid);
                Bot.SendReply(Methods.GetLocaleString(lang, "elevateUnblocked", Ids.Mark(userid), Ids.Mark(update.Message.From.Id)), update);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "deauth", InGroupOnly = true, Menu = Attributes.CommandMenu.GroupAdmins)]
        public static void deAuthUser(Update update, string[] args)
        {
            var chat = update.Message.Chat.Id;
            var userid = Methods.GetUserId(update, args);
            if (userid == Bot.Me.Id)
                return;
            var role = Bot.Api.GetChatMember(chat, update.Message.From.Id);
            var priv = Redis.db.SetContainsAsync($"chat:{chat}:auth", update.Message.From.Id).Result;
            if (role.Result.Status == ChatMemberStatus.Creator || priv)
            {
                var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
                Redis.db.SetRemoveAsync($"chat:{chat}:auth", userid);
                Bot.SendReply(Methods.GetLocaleString(lang, "dauth", Ids.Mark(userid), Ids.Mark(update.Message.From.Id)), update);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "resetSettings", InGroupOnly = true, GroupAdminOnly = true)]
        public static void resetSettings(Update update, string[] args)
        {
            Service.LogCommand(update, update.Message.Text);
            Service.GenerateSettings(update.Message.Chat.Id);
            Bot.SendReply("Settings have been reset", update);
        }
    }
}
