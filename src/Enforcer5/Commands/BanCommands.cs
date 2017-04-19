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
#pragma warning disable CS4014
#pragma warning disable CS0168
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "kickme", InGroupOnly = true)]
        public static void Kickme(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            var res = Methods.KickUser(update.Message.Chat.Id, update.Message.From.Id, lang.Doc);
            if (res)
            {
                return;
            }
        }

        [Command(Trigger = "kick", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Kick(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
                try
                {
                    try
                    {
                        var userid = Methods.GetUserId(update, args);
                        if (userid == Bot.Me.Id)
                            return;
                        var res = Methods.KickUser(update.Message.Chat.Id, userid, lang.Doc);
                        
                        if (res)
                        {
                            Methods.SaveBan(userid, "kick");
                                
                            object[] arguments =
                            {
                            Methods.GetNick(update.Message, args, userid),
                            Methods.GetNick(update.Message, args, true)
                        };
                            Bot.SendReply(Methods.GetLocaleString(lang.Doc, "SuccesfulKick", arguments), update.Message);
                        }
                    }
                    catch (Exception e)
                    {
                        Methods.SendError(e.Message, update.Message, lang.Doc);
                    }
                }
                catch (AggregateException e)
                {
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang.Doc);
                }
            
        }

        [Command(Trigger = "warn", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void  Warn(Update update, string[] args)
        {
           Warn(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id, update, targetnick:Methods.GetNick(update.Message, args, update.Message.From.Id));
        }

        public static void Warn(int warnedId, long chatId, Update update = null, string[] args = null, string targetnick = null)
        {
            try
            {
                if (Methods.IsGroupAdmin(warnedId, chatId))
                    return;
            }
            catch (Exception e)
            {

            }
            var num = Redis.db.HashIncrementAsync($"chat:{chatId}:warns", warnedId, 1).Result;
            var max = 3;
            if (warnedId == Bot.Me.Id)
                return;
            if (num < 0)
            {
                Redis.db.HashSetAsync($"chat:{chatId}:warns", warnedId, 0);
            }
            var id = warnedId;
            int.TryParse(Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result, out max);
            var lang = Methods.GetGroupLanguage(chatId);
            if (num >= max)
            {
                var type = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result.HasValue
                    ? Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result.ToString()
                    : "kick";
                if (type.Equals("ban"))
                {
                    try
                    {
                        Methods.BanUser(chatId, id, lang.Doc);
                        var name = targetnick;
                        if (update != null)
                        {
                            Bot.SendReply(Methods.GetLocaleString(lang.Doc, "warnMaxBan", name), update.Message);
                        }
                        else
                        {
                            Bot.Send(Methods.GetLocaleString(lang.Doc, "warnMaxBan", name), chatId);
                        }
                        Methods.SaveBan(id, "maxWarn");
                    }
                    catch (AggregateException e)
                    {
                        Methods.SendError(e.InnerExceptions[0], chatId, lang.Doc);
                    }
                }
                else
                {
                    Methods.KickUser(chatId, id, lang.Doc);
                    var name = targetnick;
                    if (update != null)
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), update.Message);
                    }
                    else
                    {
                        Bot.Send(Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), chatId);
                    }
                }
                Redis.db.HashSetAsync($"chat:{chatId}:warns", id, 0);
            }
            else
            {
                var diff = max - num;
                var text = Methods.GetLocaleString(lang.Doc, "warn", targetnick, num, max);
                var solvedMenu = new Menu(2)
                {
                    Buttons = new List<InlineButton>
                    {
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "resetWarn"),
                            $"resetwarns:{chatId}:{warnedId}"),
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "removeWarn"),
                            $"removewarn:{chatId}:{warnedId}"),
                    }
                };
                Bot.Send(text, chatId, customMenu: Key.CreateMarkupFromMenu(solvedMenu));
            }
        }

        [Command(Trigger = "ban", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Ban(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            try
            {
                try
                {
                    var userid = Methods.GetUserId(update, args);
                    if (userid == Bot.Me.Id)
                        return;
                    var res = Methods.BanUser(update.Message.Chat.Id, userid, lang.Doc);
                    if (res)
                    {
                        var chatId = update.Message.Chat.Id;
                        var userId = update.Message.Chat.Id;
#if normal
                        var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{chatId}:tempbanned", userId).Result;
#endif
#if premium
                        var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{chatId}:tempbannedPremium", userId).Result;
#endif
                        if (isAlreadyTempbanned)
                        {
#if normal
                            var all = Redis.db.HashGetAllAsync("tempbanned").Result;
#endif
#if premium
                            var all = Redis.db.HashGetAllAsync("tempbannedPremium").Result;
#endif
                            foreach (var mem in all)
                            {
                                if ($"{chatId}:{userId}".Equals(mem.Value))
                                {
#if normal
                                     Redis.db.HashDeleteAsync("tempbanned", mem.Name);
#endif
#if premium
                                     Redis.db.HashDeleteAsync("tempbannedPremium", mem.Name);
#endif
                                }
                            }
#if normal
                             Redis.db.SetRemoveAsync($"chat:{chatId}:tempbanned", userId);
#endif
#if premium
                             Redis.db.SetRemoveAsync($"chat:{chatId}:tempbannedPremium", userId);
#endif
                        }
                        Methods.SaveBan(userid, "ban");
                        object[] arguments =
                        {
                            Methods.GetNick(update.Message, args, userid),
                            Methods.GetNick(update.Message, args, true)
                        };
                        string why;
                        if (!string.IsNullOrEmpty(args[1]))
                        {
                            why = args[1];
                        }
                        else
                        {
                            why = $"{update.Message.ReplyToMessage.Text}";
                        }
                        Methods.AddBanList(chatId, userid, arguments[0].ToString(), why);
                         Redis.db.HashDeleteAsync($"{update.Message.Chat.Id}:userJoin", userId);
                        try
                        {
                            if (update.Message.ReplyToMessage.Type == MessageType.ServiceMessage)
                            {

                            }
                            else
                            {
                                 Bot.Api.ForwardMessageAsync(update.Message.From.Id, update.Message.Chat.Id,
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
                         Bot.SendReply(Methods.GetLocaleString(lang.Doc, "SuccesfulBan", arguments), update.Message);
                    }
                }
                catch (Exception e)
                {
                    Methods.SendError(e.Message, update.Message, lang.Doc);
                }
            }                         
            catch (AggregateException e)
            {
                Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "unban", GroupAdminOnly = true)]
        public static void UnBan(Update update, string[] args)
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
                     Bot.SendReply(Methods.GetLocaleString(lang, "userUnbanned"), update);
                }
            }

        }

        [Command(Trigger = "tempban", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void Tempban(Update update, string[] args)
        {
            var userId = update.Message.ReplyToMessage.From.Id;
            if (userId == Bot.Me.Id)
                return;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            int time;
            if (!int.TryParse(args[1], out time))
            {
                time = 60;
            }
            if (time == 0)
            {
                 Bot.SendReply(Methods.GetLocaleString(lang, "tempbanZero"), update);
            }
            else
            {
                var unbanTime = System.DateTime.UtcNow.AddHours(2).AddSeconds(time * 60).ToUnixTime();
                var hash = $"{update.Message.Chat.Id}:{userId}";
                var res = Methods.BanUser(update.Message.Chat.Id, userId, lang);
                if (res.Equals(true))
                {
                    Methods.SaveBan(userId, "tempban");
                     Redis.db.HashDeleteAsync($"chat:{update.Message.Chat.Id}:userJoin", userId);
#if normal
                     Redis.db.HashSetAsync("tempbanned", unbanTime, hash);
#endif
#if premium
                      Redis.db.HashSetAsync("tempbannedPremium", unbanTime, hash);
#endif
                    var timeBanned = TimeSpan.FromMinutes(time);
                    string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                     Bot.SendReply(
                        Methods.GetLocaleString(lang, "tempbanned", timeText, update.Message.ReplyToMessage.From.FirstName, userId),
                        update);
#if normal
                     Redis.db.SetAddAsync($"chat:{update.Message.Chat.Id}:tempbanned", userId);
#endif
#if premium
                     Redis.db.SetAddAsync($"chat:{update.Message.Chat.Id}:tempbannedPremium", userId);
#endif
                }
            }
        }          
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "resetwarns", GroupAdminOnly = true)]
        public static void ResetWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[2];
             Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:warns", userId);
             Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId);
             Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));            
        }

        [Callback(Trigger = "removewarn", GroupAdminOnly = true)]
        public static void RemoveWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var userId = args[2];
            var res = Redis.db.HashIncrementAsync($"chat:{call.Message.Chat.Id}:warns", userId, -1).Result;
            var text = "";            
                text = Methods.GetLocaleString(lang, "warnRemoved");
            if (res < 0)
            {
                 Redis.db.HashSetAsync($"chat:{call.Message.Chat.Id}:warns", userId, 0);
            }
             Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
               text);
        }
    }
}
