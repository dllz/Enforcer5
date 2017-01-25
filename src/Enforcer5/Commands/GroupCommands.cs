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
        [Command(Trigger = "rules", InGroupOnly = true)]
        public static async void Rules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var text = Methods.GetRules(update.Message.Chat.Id, lang);
            if (Methods.SendInPm(update.Message, "Rules"))
            {
                await Bot.Send(text, update.Message.From.Id);
            }
            else
            {
                await Bot.Send(text, update);
            }
        }

        [Command(Trigger = "setrules", InGroupOnly = true, GroupAdminOnly = true)]
        public static async void SetRules(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.Send(input, update);
                    Redis.db.StringSet($"chat:{update.Message.Chat.Id}:rules", input);
                    await Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.Result.MessageId,
                        Methods.GetLocaleString(lang, "RulesSet"));
                }
                catch (AggregateException e)
                {
                    Methods.SendError(e.InnerExceptions[0], update.Message, lang);
                }
            }
            else
            {
                await Bot.Send(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "about", InGroupOnly = true)]
        public static async void About(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var text = Methods.GetAbout(chatId, lang);
            if (Methods.SendInPm(update.Message, "Rules"))
            {
                await Bot.Send(text, update.Message.From.Id);
            }
            else
            {
                await Bot.Send(text, update);
            }
        }


        [Command(Trigger = "setabout", InGroupOnly = true, GroupAdminOnly = true)]
        public static async void SetAbout(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!string.IsNullOrWhiteSpace(args[1]))
            {
                var input = args[1];
                try
                {
                    var result = Bot.Send(input, update);
                    Redis.db.StringSet($"chat:{update.Message.Chat.Id}:about", input);
                    await Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.Result.MessageId,
                        Methods.GetLocaleString(lang, "AboutSet"));
                }
                catch (AggregateException e)
                {
                    Methods.SendError(e.InnerExceptions[0], update.Message, lang);
                }
            }
            else
            {
                await Bot.Send(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }
    }
}
