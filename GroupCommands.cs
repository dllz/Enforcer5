using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

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
                await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                await Bot.SendReply(text, update);
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
                    var result = Bot.SendReply(input, update);
                    Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:rules", input);
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
                await Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "about", InGroupOnly = true)]
        public static async void About(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var text = Methods.GetAbout(chatId, lang);
            if (Methods.SendInPm(update.Message, "About"))
            {
                await Bot.Send(text, update.Message.From.Id);
                await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                await Bot.SendReply(text, update);
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
                    var result = Bot.SendReply(input, update);
                    Redis.db.StringSetAsync($"chat:{update.Message.Chat.Id}:about", input);
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
                await Bot.SendReply(Methods.GetLocaleString(lang, "NoInput"), update);
            }
        }

        [Command(Trigger = "adminlist", InGroupOnly = true)]
        public static async void AdminList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var text = Methods.GetAdminList(update.Message, lang);
            if (Methods.SendInPm(update.Message, "Modlist"))
            {
                await Bot.Send(text, update.Message.From.Id);
                await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            }
            else
            {
                await Bot.SendReply(text, update);
            }
        }

        [Command(Trigger = "s", InGroupOnly = true, RequiresReply = true)]
        public static async void SaveMessage(Update update, string[] args)
        {
            var msgID = update.Message.ReplyToMessage.MessageId;
            var saveTo = update.Message.From.Id;
            var chat = update.Message.Chat.Id;
            try
            {
                await Bot.Api.ForwardMessageAsync(saveTo, chat, msgID, disableNotification: true);
            }
            catch (AggregateException e)
            {
                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                Methods.SendError(e.InnerExceptions[0], update.Message, lang);
            }
        }

        [Command(Trigger = "id")]
        public static async void GetId(Update update, string[] args)
        {
            long id;
            string user = "";
            string name;
            if (update.Message.ReplyToMessage != null)
            {
                if (update.Message.ReplyToMessage.ForwardFrom != null)
                {
                    id = update.Message.ReplyToMessage.ForwardFrom.Id;
                    name = update.Message.ReplyToMessage.ForwardFrom.FirstName;
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.ForwardFrom.LastName))
                        name = $"{name} {update.Message.ReplyToMessage.ForwardFrom.LastName}";
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.ForwardFrom.Username))
                        user = update.Message.ReplyToMessage.ForwardFrom.Username;
                }
                else
                {
                    id = update.Message.ReplyToMessage.From.Id;
                    name = update.Message.ReplyToMessage.From.FirstName;
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.From.LastName))
                        name = $"{name} {update.Message.ReplyToMessage.From.LastName}";
                    if (!string.IsNullOrEmpty(update.Message.ReplyToMessage.From.Username))
                        user = update.Message.ReplyToMessage.From.Username;
                }
            }
            else
            {
                id = update.Message.Chat.Id;
                name = update.Message.Chat.Title;
                if (!string.IsNullOrEmpty(update.Message.Chat.Username))
                    user = update.Message.Chat.Username;
            }
            if (!string.IsNullOrEmpty(user))
            {
                try
                {
                    await Bot.SendReply($"{id}\n{name}\n@{user}", update.Message);
                }
                catch (AggregateException e)
                {
                    var lang = Methods.GetGroupLanguage(update.Message).Doc;
                    Methods.SendError(e.InnerExceptions[0], update.Message, lang);
                }
            }
            else
            {
                try
                {
                    await Bot.SendReply($"{id}\n{name}", update.Message);
                }
                catch (AggregateException e)
                {
                    var lang = Methods.GetGroupLanguage(update.Message).Doc;
                    Methods.SendError(e.InnerExceptions[0], update.Message, lang);
                }
            }
        }

        [Command(Trigger = "support")]
        public static async void Support(Update update, string[] args)
        {
            int msgToReplyTo;
            if (update.Message.ReplyToMessage != null)
            {
                msgToReplyTo = update.Message.ReplyToMessage.MessageId;
            }
            else
            {
                msgToReplyTo = update.Message.MessageId;
            }
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            await Bot.SendReply(Methods.GetLocaleString(lang, "Support"), update.Message.Chat.Id, msgToReplyTo);
        }

        [Command(Trigger = "user", InGroupOnly = true, GroupAdminOnly = true)]
        public static async void User(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = Methods.GetUserId(update, args);
                var buttons = new[]
                {
                    new InlineKeyboardButton(Methods.GetLocaleString(lang, "removeWarn"), $"userbutton:remwarns:{userid}"),
                    new InlineKeyboardButton(Methods.GetLocaleString(lang, "ban"), $"userbutton:banuser:{userid}"), 
                    new InlineKeyboardButton(Methods.GetLocaleString(lang, "Warn"), $"userbutton:warnuser:{userid}"), 
                };
                var keyboard = new InlineKeyboardMarkup(buttons.ToArray());
                var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
                await Bot.SendReply(text, update, keyboard);
            }
            catch (Exception e)
            {
                if (e.Message.Equals("UnableToResolveId"))
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "UnableToGetID"), update);
                }
                else
                {
                    Methods.SendError(e.InnerException, update.Message, lang);
                }
            }
            

        }

        [Command(Trigger = "me")]
        public static async void Me(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            try
            {
                var userid = update.Message.From.Id;
                var text = Methods.GetUserInfo(userid, update.Message.Chat.Id, update.Message.Chat.Title, lang);
                await Bot.Send(text, update.Message.From.Id);
                if (update.Message.Chat.Type != ChatType.Private)
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
                }
            }
            catch (AggregateException e)
            {
                Methods.SendError(e.InnerExceptions[0], update.Message, lang);
            }
        }
    }
}
