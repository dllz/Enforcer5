using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Handlers;
using Telegram.Bot.Types;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "kickme", InGroupOnly = true)]
        public static async Task Kickme(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            var res = Methods.KickUser(update.Message.Chat.Id, update.Message.From.Id, lang.Doc);
            if (res.Result)
            {
                return;
            }
            if (res.Exception != null)
            {

                Methods.SendError($"{res.Exception.InnerExceptions[0]}\n{res.Exception.StackTrace}", update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "kick", GroupAdminOnly = true, InGroupOnly = true, RequiresReply = true)]
        public static async Task Kick(Update update, string[] args)
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
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang.Doc);
                }
            }
            else
            {
                await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "noReply"), update.Message);
            }
        }

        [Command(Trigger = "warn", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static async Task Warn(Update update, string[] args)
        {
            if (Methods.IsGroupAdmin(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id)) 
                return;            
            var num = Redis.db.HashIncrementAsync($"chat:{update.Message.Chat.Id}:warns", update.Message.ReplyToMessage.From.Id, 1).Result;
            var max = 3;
            if(num < 0)
            {
                await Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:warns", update.Message.ReplyToMessage.From.Id, 0);
            }
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
                        Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang.Doc);
                    }
                }
                else
                {
                    await Methods.KickUser(update.Message.Chat.Id, update.Message.ReplyToMessage.From.Id, lang.Doc);
                    var name = Methods.GetNick(update.Message, args);
                    await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), update.Message);
                }
                await Redis.db.HashSetAsync($"chat:{update.Message.Chat.Id}:warns", update.Message.ReplyToMessage.From.Id, 0);            
        }
            else
            {
                var diff = max - num;                
                var text = Methods.GetLocaleString(lang.Doc, "warn", Methods.GetNick(update.Message, args), num, max);
                var solvedMenu = new Menu(2)
                {
                    Buttons = new List<InlineButton>
                    {
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "resetWarn"),
                            $"resetwarns:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.From.Id}"),
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "removeWarn"),
                            $"removewarn:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.From.Id}"),
                    }
                };
                await Bot.Send(text, update.Message.Chat.Id, customMenu: Key.CreateMarkupFromMenu(solvedMenu));
            }
        }

        [Command(Trigger = "ban", GroupAdminOnly = true, RequiresReply = true)]
        public static async Task Ban(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            try
            {
                var userid = Methods.GetUserId(update, args);
                var res = Methods.BanUser(update.Message.Chat.Id, userid, lang.Doc).Result;

                if (res)
                {
                    var chatId = update.Message.Chat.Id;
                    var userId = update.Message.Chat.Id;
                    var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{chatId}:tempbanned", userId).Result;
                    if (isAlreadyTempbanned)
                    {
                        var all = Redis.db.HashGetAllAsync("tempbanned").Result;
                        foreach (var mem in all)
                        {
                            if ($"{chatId}:{userId}".Equals(mem.Value))
                            {
                                await Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                            }
                        }
                        await Redis.db.SetRemoveAsync($"chat:{chatId}:tempbanned", userId);
                    }
                    Methods.SaveBan(userid, "ban");
                    object[] arguments =
                    {
                        Methods.GetNick(update.Message, args),
                        Methods.GetNick(update.Message, args, true)
                    };
                    string why;
                    if (!string.IsNullOrEmpty(args[1]))
                    {
                        why = args[1];
                    }
                    else
                    {
                        why = $"\"update.Message.ReplyToMessage.Text\"";
                    }
                    Methods.AddBanList(chatId, userid, arguments[0].ToString(), why);
                    await Redis.db.HashDeleteAsync($"{update.Message.Chat.Id}:userJoin", userId);
                    try
                    {
                        if (update.Message.ReplyToMessage.Type == MessageType.ServiceMessage)
                        {
                            
                        }
                        else
                        {
                            await Bot.Api.ForwardMessageAsync(update.Message.From.Id, update.Message.Chat.Id,
                            update.Message.ReplyToMessage.MessageId, disableNotification: true);
                        }                        
                    }
                    catch (ApiRequestException e)
                    {

                    }
                    catch (AggregateException e)
                    {

                    }
                    catch (Exception e)
                    {

                    }
                    await Bot.SendReply(Methods.GetLocaleString(lang.Doc, "SuccesfulBan", arguments), update.Message);
                }
            }
            catch (AggregateException e)
            {
                Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "unban", GroupAdminOnly = true)]
        public static async Task UnBan(Update update, string[] args)
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

        [Command(Trigger = "tempban", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static async Task Tempban(Update update, string[] args)
        {
            var userId = update.Message.ReplyToMessage.From.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            int time;
            if (!int.TryParse(args[1], out time))
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "incorrectArgument"), update);
                return;
            }
            if (time == 0)
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "tempbanZero"), update);
            }
            var unbanTime = System.DateTime.UtcNow.AddHours(2).AddSeconds(time * 60).ToUnixTime();
            var hash = $"{update.Message.Chat.Id}:{userId}";
            var res = Methods.BanUser(update.Message.Chat.Id, userId, lang);
            if (res.Result.Equals(true))
            {
                Methods.SaveBan(userId, "tempban");
                await Redis.db.HashDeleteAsync($"chat:{update.Message.Chat.Id}:userJoin", userId);
                await Redis.db.HashSetAsync("tempbanned", unbanTime, hash);
                var timeBanned = TimeSpan.FromMinutes(time);
                string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                await Bot.SendReply(
                    Methods.GetLocaleString(lang, "tempbanned", timeText, update.Message.ReplyToMessage.From.FirstName, userId),
                    update);
                await Redis.db.SetAddAsync($"chat:{update.Message.Chat.Id}:tempbanned", userId);
            }
        }          
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "resetwarns", GroupAdminOnly = true)]
        public static async Task ResetWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[2];
            await Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:warns", userId);
            await Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId);
            await Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));            
        }

        [Callback(Trigger = "removewarn", GroupAdminOnly = true)]
        public static async Task RemoveWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[2];
            var res = Redis.db.HashIncrementAsync($"chat:{call.Message.Chat.Id}:warns", userId, -1).Result;
            var text = "";            
                text = Methods.GetLocaleString(lang, "warnRemoved");
            if (res < 0)
            {
                await Redis.db.HashSetAsync($"chat:{call.Message.Chat.Id}:warns", userId, 0);
            }
            await Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
               text);
        }
    }
}