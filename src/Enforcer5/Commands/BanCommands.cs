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
using System.Net.Http;
using Newtonsoft.Json;

#pragma warning disable CS4014
#pragma warning disable CS0168
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "kickme", InGroupOnly = true)]
        public static void Kickme(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true);
            var res = Methods.KickUser(update.Message.Chat.Id, update.Message.From.Id, lang.Doc);
            if (res)
            {
                return;
            }
        }

        [Command(Trigger = "kick", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Kick(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true);
                try
                {
                    try
                    {
                        var userid = Methods.GetUserId(update, args);
                        if (userid == Bot.Me.Id || userid == update.Message.From.Id)
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
                            Service.LogCommand(update, update.Message.Text);
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

       [Command(Trigger = "warn", InGroupOnly = true, GroupAdminOnly = true)]
        public static void  Warn(Update update, string[] args)
        {
           if (update.Message.ReplyToMessage != null)
           {
                Warn(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id, update, targetnick:Methods.GetNick(update.Message, args, update.Message.From.Id));
               Service.LogCommand(update, update.Message.Text);
            }
           else 
           {
               try
               {
                   var warnedid = Methods.GetUserId(update, args);
                   var chatid = update.Message.Chat.Id;
                   
                   Warn(warnedid, chatid, update, targetnick:$"{Redis.db.HashGetAsync($"user:{warnedid}", "name").Result} ({warnedid})");
                    Service.LogCommand(update, update.Message.Text);
               }
               catch (Exception e)
               {
                   var lang = Methods.GetGroupLanguage(update.Message,true);
                   Methods.SendError(e.Message, update.Message, lang.Doc);
               }
           }
        }

        public static void Warn(long warnedId, long chatId, Update update = null, string[] args = null, string targetnick = null, string callbackid = "", long callbackfromid = 0)
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
                var name = "";
                switch(type)
                {
                    case "ban":
                        try
                        {
                            var res = Methods.BanUser(chatId, id, lang.Doc);

                            if (res)
                            {
                                name = targetnick;
                                if (update != null)
                                {
                                    Bot.SendReply(Methods.GetLocaleString(lang.Doc, "warnMaxBan", name), update.Message);
                                }
                                else if (!string.IsNullOrEmpty(callbackid))
                                {
                                    var bantext = Methods.GetLocaleString(lang.Doc, "warnMaxBan", name);
                                    Bot.Api.AnswerCallbackQueryAsync(callbackid, bantext, true);
                                    Bot.Send(bantext, chatId);
                                }
                                else //How should it be possible that there is neither an update nor a callback query?
                                {
                                    Bot.Send(Methods.GetLocaleString(lang.Doc, "warnMaxBan", name), chatId);
                                }
                                Methods.SaveBan(id, "maxWarn");
                            }
                                
                        }
                        catch (AggregateException e)
                        {
                            Methods.SendError(e.InnerExceptions[0], chatId, lang.Doc);
                        }
                        break;
                        
                    case "kick":
                        Methods.KickUser(chatId, id, lang.Doc);
                        name = targetnick;
                        if (update != null)
                        {
                            Bot.SendReply(Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), update.Message);
                        }
                        else if (!string.IsNullOrEmpty(callbackid))
                        {
                            var kicktext = Methods.GetLocaleString(lang.Doc, "warnMaxKick", name);
                            Bot.Api.AnswerCallbackQueryAsync(callbackid, kicktext, true);
                            Bot.Send(kicktext, chatId);
                        }
                        else //How should it be possible that there is neither an update nor a callback query?
                        {
                            Bot.Send(Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), chatId);
                        }
                        break;
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
                if (update != null)
                {
                    Bot.SendReply(text, update, Key.CreateMarkupFromMenu(solvedMenu));
                }
                else if (!string.IsNullOrEmpty(callbackid))
                {
                    text = Methods.GetLocaleString(lang.Doc, "warnFlag", warnedId, callbackfromid, num, max);
                    Bot.Api.AnswerCallbackQueryAsync(callbackid, text, true);
                    Bot.Send(text, chatId);
                }
                else //How should it be possible that there is neither an update nor a callback query?
                {
                    Bot.Send(text, chatId, customMenu: Key.CreateMarkupFromMenu(solvedMenu));
                }
            }
        }

        [Command(Trigger = "ban", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Ban(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true);
            try
            {
                try
                {
                    var userid = Methods.GetUserId(update, args);
                    if (userid == Bot.Me.Id || userid == update.Message.From.Id)
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
                        Service.LogCommand(update, update.Message.Text);
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
            var status = Bot.Api.GetChatMemberAsync(chatId, Convert.ToInt32(userId)).Result.Status;
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            if (status == ChatMemberStatus.Kicked)
            {
                var res = Methods.UnbanUser(chatId, userId, lang);
                if (res)
                {
                     Bot.SendReply(Methods.GetLocaleString(lang, "userUnbanned"), update);
                    Service.LogCommand(update, update.Message.Text);
                }
            }

        }

        public static void Tempban(long userId, long chatId, long time,
            string nick = null, Update update = null, string message = null)
        {           
            var lang = Methods.GetGroupLanguage(chatId).Doc;
                var dataUnbanTime = System.DateTime.UtcNow.AddHours(2).AddSeconds(time * 60);
            var unbanTime = dataUnbanTime.ToUnixTime();
                var hash = $"{chatId}:{userId}";
                var res = Methods.BanUser(chatId, userId, lang);
                var isBanned = Redis.db.StringGetAsync($"chat:{chatId}:tempbanned:{userId}").Result;
            if (isBanned.HasValue)
            {
#if normal
                Redis.db.HashDeleteAsync("tempbanned", unbanTime);
#endif
#if premium
                Redis.db.HashDeleteAsync("tempbannedPremium", unbanTime);
#endif
            }
            if (res.Equals(true))
                {
                    Methods.SaveBan(userId, "tempban");
                    Redis.db.HashDeleteAsync($"chat:{chatId}:userJoin", userId);

#if normal
                var entry = Redis.db.HashGetAsync("tempbanned", unbanTime).Result;
                    while (entry.HasValue)
                    {
                            dataUnbanTime.AddSeconds(1);
                        unbanTime = dataUnbanTime.ToUnixTime();
                        entry = Redis.db.HashGetAsync("tempbanned", unbanTime).Result;
                }
                    Redis.db.HashSetAsync("tempbanned", unbanTime, hash);
#endif
#if premium
                var entry = Redis.db.HashGetAsync("tempbannedPremium", unbanTime).Result;
                    while (entry.HasValue)
                    {
                            dataUnbanTime.AddSeconds(1);
                        unbanTime = dataUnbanTime.ToUnixTime();
                        entry = Redis.db.HashGetAsync("tempbannedPremium", unbanTime).Result;
                }
                      Redis.db.HashSetAsync("tempbannedPremium", unbanTime, hash);
#endif
                var timeBanned = TimeSpan.FromMinutes(time);
                    string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                    if (message == null)
                    {
                        message = Methods.GetLocaleString(lang, "tempbanned", timeText, nick, userId);
                    }
                    if (update != null)
                    {
                        Bot.SendReply(message
                            ,
                            update);
                    }
                    else
                    {
                        Bot.Send(message, chatId);
                    }

                Redis.db.StringSetAsync($"chat:{chatId}:tempbanned:{userId}", unbanTime, TimeSpan.FromMinutes(time));
#if normal
                Redis.db.SetAddAsync($"chat:{chatId}:tempbanned", userId);                    
#endif
#if premium
                     Redis.db.SetAddAsync($"chat:{chatId}:tempbannedPremium", userId);
#endif
            }
            }        

        [Command(Trigger = "tempban", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Tempban(Update update, string[] args)
        {

            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            long userId = 0, time;
            string length = "";
            if (update.Message.ReplyToMessage != null) // by reply
            {
                userId = update.Message.ReplyToMessage.From.Id; // user id is id of replied message

                if (args[1] != null)
                {
                    length = args[1].Contains(' ') // length is the first argument after the command
                        ? args[1].Split(' ')[0]    // so admins can add a reason for the tempban after the time
                        : args[1];
                }

            }
            else if (args[1] != null)
            {
                if (args[1].Contains(' ')) // not by reply but contains a space so we might have userid and time
                {
                    var user = args[1].Split(' ')[0]; // either username or ID
                    length = args[1].Split(' ')[1]; // Length of the ban, or the first word of the reason, if no time is specified. Parsing will fail then and time set to 60.

                    if (user.StartsWith("@")) userId = Methods.ResolveIdFromusername(user);
                    else if (!long.TryParse(user, out userId)) // If the first argument after command is neither a username nor an ID, it is incorrect.
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "incorrectArgument"), update);
                        return;
                    }
                    if (userId == Bot.Me.Id) return;
                }
                else // not by reply neither we have both ID and time, but if we have ID, standard time is 60 minutes
                {
                    var user = args[1];
                    length = Methods.GetGroupTempbanTime(update.Message.Chat.Id).ToString(); // Length is 60 since there is definitely no length specified.

                    if (user.StartsWith("@")) userId = Methods.ResolveIdFromusername(user);
                    else if (!long.TryParse(user, out userId)) // If the specified argument after the command is neither a username nor an ID, it is incorrect.
                    {
                        Bot.SendReply(Methods.GetLocaleString(lang, "incorrectArgument"), update);
                        return;
                    }
                }
            }

            if (!long.TryParse(length, out time)) // Convert our length string into an int, or into 60, if there was no length specified
            {
                time = Methods.GetGroupTempbanTime(update.Message.Chat.Id);
            }
            if (time == 0)
            {
                time = Methods.GetGroupTempbanTime(update.Message.Chat.Id);
            }
            if (userId != 0)
            {
                Tempban(userId, update.Message.Chat.Id, time, Methods.GetNick(update.Message, args, userId));
                Service.LogCommand(update, update.Message.Text);
            
            }
        }

        [Command(Trigger = "delmsg", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void DeleteMessageInGroup(Update update, string[] args)
        {
            Bot.DeleteMessage(update.Message.Chat.Id, update.Message.ReplyToMessage.MessageId);
            Service.LogCommand(update, update.Message.Text);
        }

       
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "resetwarns", GroupAdminOnly = true)]
        public static void ResetWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userId = args[2];
             Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:warns", userId);
             Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:mediawarn", userId);
             Bot.Api.EditMessageTextAsync(call.Message.Chat.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));            
        }

        [Callback(Trigger = "removewarn", GroupAdminOnly = true)]
        public static void RemoveWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
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
