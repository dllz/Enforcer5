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
                // A limit that is not positive is the default, as it always read.
                int? limit = chars > 0 && chars <= int.MaxValue ? (int)chars : null;
                if (Detection.DetectionSettings.TryUpdate(update.Message.Chat.Id, p => p with { NameLength = p.NameLength with { MaxCharacters = limit } }) != null)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                    return;
                }
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
                // A limit that is not positive is the default, as it always read.
                int? limit = chars > 0 && chars <= int.MaxValue ? (int)chars : null;
                if (Detection.DetectionSettings.TryUpdate(update.Message.Chat.Id, p => p with { TextLength = p.TextLength with { MaxCharacters = limit } }) != null)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                    return;
                }
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
                // A limit that is not positive is the default, as it always read.
                int? limit = chars > 0 && chars <= int.MaxValue ? (int)chars : null;
                if (Detection.DetectionSettings.TryUpdate(update.Message.Chat.Id, p => p with { TextLength = p.TextLength with { MaxLines = limit } }) != null)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "done"), update);
                    return;
                }
            }
            Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
        }

    }
}
