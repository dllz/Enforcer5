using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Handlers;
using Telegram.Bot.Types;
using Enforcer5.Helpers;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "kickme", InGroupOnly = true)]
        public static void Kickme(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            var res = Methods.KickUser(update.Message.Chat.Id, update.Message.From.Id, lang.Doc);
            if (res.Result)
            {
                return;
            }
            if (res.Exception != null)
            {

                Methods.SendError(res.Exception.InnerExceptions[0], update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "kick", GroupAdminOnly = true, InGroupOnly = true, RequiresReply = true)]
        public static async void Kick(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            if (update.Message.ReplyToMessage != null)
            {
                try
                {
                    var userid = Methods.GetUserId(update, args);
                    var res = Methods.KickUser(update.Message.Chat.Id, userid, lang.Doc).Result;

                    if (res)
                    {
                        Methods.SaveBan(userid, "kick");

                        object[] arguments =
                        {
                            Methods.GetNick(update.Message, args),
                            Methods.GetNick(update.Message, args, true)
                        };
                        await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "SuccesfulKick", arguments), update.Message);
                    }
                }
                catch (AggregateException e)
                {
                    Methods.SendError(e.InnerExceptions[0], update.Message, lang.Doc);
                }
            }
            else
            {
                await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "noReply"), update.Message);
            }
        }

        [Command(Trigger = "warn", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static async void Warn(Update update, string[] args)
        {
            var num = Redis.db.HashIncrementAsync($"chat:{update.Message.Chat.Id}:warns", update.Message.From.Id, 1).Result;
            var max = 3;
            int.TryParse(Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}:warnsettings", "max").Result, out max);
            var lang = Methods.GetGroupLanguage(update.Message);
            if (num >= max)
            {
                var type = Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}:warnsettings", "type").Result.HasValue
                    ? Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}:warnsettings", "type").ToString()
                    : "kick";
                if (type.Equals("ban"))
                {
                    try
                    {
                        await Bot.Api.KickChatMemberAsync(update.Message.Chat.Id, update.Message.ReplyToMessage.From.Id);
                        var name = Methods.GetNick(update.Message, args);
                        await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "warnMaxBan", name), update.Message);
                    }
                    catch (AggregateException e)
                    {
                        Methods.SendError(e.InnerExceptions[0], update.Message, lang.Doc);
                    }
                }
                else
                {
                    await Methods.KickUser(update.Message.Chat.Id, update.Message.ReplyToMessage.From.Id, lang.Doc);
                    var name = Methods.GetNick(update.Message, args);
                    await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), update.Message);
                }
            }
            else
            {
                var diff = max - num;
                var text = Methods.GetLocaleString(lang.Doc, "warn", Methods.GetNick(update.Message, args), num, max);
                var baseMenu = new List<InlineKeyboardButton>();
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang.Doc, "resetWarn"), $"resetwarns:{update.Message.ReplyToMessage.From.Id}"));
                baseMenu.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang.Doc, "removeWarn"), $"removewarn:{update.Message.ReplyToMessage.From.Id}"));
                var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
                await Bot.Send(text, update.Message.Chat.Id, customMenu: menu);
            }
        }

        [Command(Trigger = "ban", GroupAdminOnly = true, RequiresReply = true)]
        public static async void Ban(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            try
            {
                var userid = Methods.GetUserId(update, args);
                var res = Methods.BanUser(update.Message.Chat.Id, userid, lang.Doc).Result;

                if (res)
                {
                    Methods.SaveBan(userid, "ban");
                    object[] arguments =
                    {
                        Methods.GetNick(update.Message, args),
                        Methods.GetNick(update.Message, args, true)
                    };
                    await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "SuccesfulBan", arguments), update.Message);
                }
            }
            catch (AggregateException e)
            {
                Methods.SendError(e.InnerExceptions[0], update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "unban", GroupAdminOnly = true)]
        public static async void UnBan(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var userId = Methods.GetUserId(update, args);
            var status = Bot.Api.GetChatMemberAsync(chatId, userId).Result.Status;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (status == ChatMemberStatus.Kicked)
            {
                var res = Methods.UnbanUser(chatId, userId, lang);
                if (res)
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "userUnbanned"), update);
                }
            }

        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "resetwarns", GroupAdminOnly = true)]
        public static void ResetWarns(CallbackQuery call, string[] args)
        {

        }
    }
}