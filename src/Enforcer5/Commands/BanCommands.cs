using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Handlers;
using Telegram.Bot.Types;
using Enforcer5.Helpers;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "kickme", InGroupOnly = true)]
        public static void Kickme(Update update, string[] args)
        {
            var res = Methods.KickUser(update.Message.Chat.Id, update.Message.From.Id);
            if (res.Result)
            {
                return;
            }
            if (res.Exception != null)
            {
                Methods.SendError(res.Exception.InnerExceptions[0], update.Message);
            }
        }

        [Command(Trigger = "kick", GroupAdminOnly = true, InGroupOnly = true)]
        public static async void Kick(Update update, string[] args)
        {
            try
            {
                var userid = Methods.GetUserId(update, args);
                var res = Methods.KickUser(update.Message.Chat.Id, userid).Result;

                Methods.SaveBan(userid, "kick");
                var lang = Methods.GetGroupLanguage(update.Message);
                object[] arguments =
                {
                        Methods.GetNick(update.Message, args, true),
                        Methods.GetNick(update.Message, args)
                    };
                await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "SuccesfulKick", arguments), update.Message);


            }
            catch (AggregateException e)
            {
                Methods.SendError(e.InnerExceptions[0], update.Message);
            }
        }
    }
}
