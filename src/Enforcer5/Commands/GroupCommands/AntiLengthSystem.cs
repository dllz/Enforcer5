using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "maxnl", InGroupOnly = true, GroupAdminOnly = true)]
        public static void MaxNameLength(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            long chars;
            if (Int64.TryParse(args[1], out chars))
            {
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:antinamelengthsettings", "maxlength", chars);
                Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
        }
        [Command(Trigger = "maxtl", InGroupOnly = true, GroupAdminOnly = true)]
        public static void MaxTextLength(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            long chars;
            if (Int64.TryParse(args[1], out chars))
            {
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:antitextlengthsettings", "maxlength", chars);
                Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
        }
        [Command(Trigger = "maxl", InGroupOnly = true, GroupAdminOnly = true)]
        public static void MaxLines(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            long chars;
            if (Int64.TryParse(args[1], out chars))
            {
                Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:antitextlengthsettings", "maxlines", chars);
                Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                return;
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
        }

    }
}
