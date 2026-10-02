    using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Handlers;
using Telegram.Bot.Types;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Net.Http;
using Newtonsoft.Json;
using Telegram.Bot;

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

        [Command(Trigger = "prewarn", InGroupOnly = true, GroupAdminOnly = true)]
        public static void PreWarn(Update update, string[] args)
        {
            if (update.Message.ReplyToMessage != null)
            {
                Warn(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id, update, targetnick: Methods.GetNick(update.Message, args, update.Message.From.Id), preWarn: true);
                Service.LogCommand(update, update.Message.Text);
            }
            else
            {
                try
                {
                    var warnedid = Methods.GetUserId(update, args);
                    var chatid = update.Message.Chat.Id;

                    Warn(warnedid, chatid, update, targetnick: $"{Redis.db.HashGetAsync($"user:{warnedid}", "name").Result} ({warnedid})", preWarn: true);
                    Service.LogCommand(update, update.Message.Text);
                }
                catch (Exception e)
                {
                    var lang = Methods.GetGroupLanguage(update.Message, true);
                    Methods.SendError(e.Message, update.Message, lang.Doc);
                }
            }
        }

        public static void Warn(long warnedId, long chatId, Update update = null, string[] args = null, string targetnick = null, string callbackid = "", long callbackfromid = 0, bool preWarn = false)
        {
            try
            {
                if (Methods.IsGroupAdmin(warnedId, chatId))
                    return;
            }
            catch (Exception e)
            {

            }
            var lang = Methods.GetGroupLanguage(chatId);
            var preText = "";
            long preWarns = 0;
            if (preWarn)
            {
                preWarns = Redis.db.HashIncrementAsync($"chat:{chatId}:prewarns", warnedId, 1).Result;
                var text = Methods.GetLocaleString(lang.Doc, "prewarn", targetnick, preWarns);
                var solvedMenu = new Menu(2)
                {
                    Buttons = new List<InlineButton>
                    {
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "resetPreWarn"),
                            $"resetPrewarns:{chatId}:{warnedId}"),
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "removePreWarn"),
                            $"removePrewarn:{chatId}:{warnedId}"),
                    }
                };
                Bot.SendReply(text, update, Key.CreateMarkupFromMenu(solvedMenu));
                return;
            } else
            {
                long.TryParse(Redis.db.HashGetAsync($"chat:{chatId}:prewarns", warnedId).Result.ToString(), out preWarns);
                if(preWarns > 0)
                {
                    preText = Methods.GetLocaleString(lang.Doc, "preWarnConverted", targetnick, preWarns) + "\n";
                    Redis.db.HashSetAsync($"chat:{chatId}:prewarns", warnedId, 0);
                }
            }
            var num = Redis.db.HashIncrementAsync($"chat:{chatId}:warns", warnedId, 1 + preWarns).Result;
            Redis.db.HashIncrementAsync($"chat:{chatId}:totalWarns", warnedId, 1 + preWarns);
            var max = 3;
            if (warnedId == Bot.Me.Id)
                return;
            if (num < 0)
            {
                Redis.db.HashSetAsync($"chat:{chatId}:warns", warnedId, 0);
            }
            var id = warnedId;
            int.TryParse(Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result.ToString(), out max);
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
                                    Bot.SendReply($"{preText}{Methods.GetLocaleString(lang.Doc, "warnMaxBan", name)}", update.Message);
                                }
                                else if (!string.IsNullOrEmpty(callbackid))
                                {
                                    var bantext = Methods.GetLocaleString(lang.Doc, "warnMaxBan", name);
                                    Bot.Api.AnswerCallbackQuery(callbackid, preText + bantext, true);
                                    Bot.Send(bantext, chatId);
                                }
                                else //How should it be possible that there is neither an update nor a callback query?
                                {
                                    Bot.Send(preText + Methods.GetLocaleString(lang.Doc, "warnMaxBan", name), chatId);
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
                            Bot.SendReply(preText + Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), update.Message);
                        }
                        else if (!string.IsNullOrEmpty(callbackid))
                        {
                            var kicktext = Methods.GetLocaleString(lang.Doc, "warnMaxKick", name);
                            Bot.Api.AnswerCallbackQuery(callbackid, preText + kicktext, true);
                            Bot.Send(kicktext, chatId);
                        }
                        else //How should it be possible that there is neither an update nor a callback query?
                        {
                            Bot.Send(preText + Methods.GetLocaleString(lang.Doc, "warnMaxKick", name), chatId);
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
                    Bot.SendReply(preText + text, update, Key.CreateMarkupFromMenu(solvedMenu));
                }
                else if (!string.IsNullOrEmpty(callbackid) && !string.IsNullOrEmpty(targetnick))
                {
                    var nick = Redis.db.HashGetAsync($"user:{callbackfromid}", "name").Result;
                    text = Methods.GetLocaleString(lang.Doc, "warnFlag", targetnick, $"{nick} ({callbackfromid})", num, max);
                    Bot.Api.AnswerCallbackQuery(callbackid, preText + text, true);
                    Bot.Send(text, chatId);
                }
                else if (!string.IsNullOrEmpty(callbackid))
                {
                    text = Methods.GetLocaleString(lang.Doc, "warnFlag", warnedId, callbackfromid, num, max);
                    Bot.Api.AnswerCallbackQuery(callbackid, preText + text, true);
                    Bot.Send(text, chatId);
                }
                else //How should it be possible that there is neither an update nor a callback query?
                {
                    Bot.Send(preText + text, chatId, customMenu: Key.CreateMarkupFromMenu(solvedMenu));
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
                    // A post made as a channel carries a shared placeholder sender, so the user
                    // path below cannot handle it: ban the channel instead.
                    switch (ChannelPosts.FromModerationCommand(update.Message, args.Length > 1 ? args[1] : null,
                                update.Message.Chat.Id, out var channelId, out var channelTitle))
                    {
                        case ChannelTargetKind.Group:
                            Bot.SendReply(Methods.GetLocaleString(lang.Doc, "cannotbanadmin"), update);
                            return;
                        case ChannelTargetKind.Channel:
                            BanChannel(update, channelId, channelTitle, lang.Doc);
                            return;
                    }

                    var userid = Methods.GetUserId(update, args);
                    if (userid == Bot.Me.Id || userid == update.Message.From.Id)
                        return;

                    var isChatMember = Bot.Api.GetChatMember(update.Message.Chat.Id, userid).Result;
                    
                    var res = Methods.BanUser(update.Message.Chat.Id, userid, lang.Doc);
                    if (res)
                    {
                        var chatId = update.Message.Chat.Id;
                        // Was the chat id, so the tempban cleanup and ban stats below never matched.
                        var userId = userid;
#if NORMAL
                        var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{chatId}:tempbanned", userId).Result;
#endif
#if PREMIUM
                        var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{chatId}:tempbannedPremium", userId).Result;
#endif
                        if (isAlreadyTempbanned)
                        {
#if NORMAL
                            var all = Redis.db.HashGetAllAsync("tempbanned").Result;
#endif
#if PREMIUM
                            var all = Redis.db.HashGetAllAsync("tempbannedPremium").Result;
#endif
                            foreach (var mem in all)
                            {
                                // Values are "{chat}:{user}:{name}:{group}"; an exact match never hit,
                                // so the tempban timer later unbanned someone who had been /banned.
                                if (ChannelPosts.IsTempbanEntryFor(mem.Value, chatId, userId))
                                {
#if NORMAL
                                     Redis.db.HashDeleteAsync("tempbanned", mem.Name);
#endif
#if PREMIUM
                                     Redis.db.HashDeleteAsync("tempbannedPremium", mem.Name);
#endif
                                }
                            }
#if NORMAL
                             Redis.db.SetRemoveAsync($"chat:{chatId}:tempbanned", userId);
#endif
#if PREMIUM
                             Redis.db.SetRemoveAsync($"chat:{chatId}:tempbannedPremium", userId);
#endif
                        }
                        if (isChatMember.Status == ChatMemberStatus.Member)
                        {
                            Methods.SaveBan(userId, "ban");
                        }
                        if (userid == 321720895 | userid == 9375804)
                        {
                            Redis.db.SetAdd("bot:lookaround",
                                $"{userid}:\n{update.Message.Chat.Id} {update.Message.Chat.Title} {update.Message.From.Id} {update.Message.From.FirstName}");
                        }
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
                            // Service messages (joins, title changes) cannot be forwarded.
                            if (!update.Message.ReplyToMessage.IsServiceMessage)
                            {
                                Bot.Api.ForwardMessage(update.Message.From.Id, update.Message.Chat.Id,
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
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;

            switch (ChannelPosts.FromModerationCommand(update.Message, args.Length > 1 ? args[1] : null,
                        chatId, out var channelId, out var channelTitle))
            {
                case ChannelTargetKind.Group:
                    return; // anonymous admins are never banned
                case ChannelTargetKind.Channel:
                    UnbanChannel(update, channelId, channelTitle, lang);
                    return;
            }

            // Every step used to fail silently: an unknown username or a failed lookup threw on the
            // pool, and a user who was not banned got no answer at all.
            long userId;
            ChatMemberStatus status;
            try
            {
                userId = Methods.GetUserId(update, args);
                status = Bot.Api.GetChatMember(chatId, userId).GetAwaiter().GetResult().Status;
            }
            catch (Exception e)
            {
                Methods.SendError(Bot.AsApiError(e)?.Message ?? e.Message, update.Message, lang);
                return;
            }

            if (status != ChatMemberStatus.Kicked)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "userNotBanned", Methods.GetNick(update.Message, args, userId)), update);
                return;
            }

            var isBanned = Redis.db.StringGetAsync($"chat:{chatId}:tempbanned:{userId}").Result;
            if (isBanned.HasValue)
            {
#if NORMAL
                Redis.db.HashDeleteAsync("tempbanned", isBanned.ToString());
#endif
#if PREMIUM
                Redis.db.HashDeleteAsync("tempbannedPremium", isBanned.ToString());
#endif
            }
            var res = Methods.UnbanUser(chatId, userId, lang);
            if (res)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "userUnbanned"), update);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        public static bool Tempban(long userId, long chatId, double time,
            string nick = null, Update update = null, string message = null)
        {           
            var lang = Methods.GetGroupLanguage(chatId).Doc;
                // Must be pure UTC: this value is both stored as the expiry key and handed to
                // Telegram as untilDate, which Telegram always interprets as UTC.
                var dataUnbanTime = System.DateTime.UtcNow.AddSeconds(time * 60);
            var unbanTime = dataUnbanTime.ToUnixTime();
                var hash = $"{chatId}:{userId}:{Redis.db.HashGetAsync($"user:{userId}", "name").Result}:{Redis.db.HashGetAsync($"chat:{chatId}:details", "name").Result}";
            var res = Methods.TempBanUser(chatId, userId, dataUnbanTime, lang);
                var isBanned = Redis.db.StringGetAsync($"chat:{chatId}:tempbanned:{userId}").Result;
            if (isBanned.HasValue)
            {
#if NORMAL
                Redis.db.HashDeleteAsync("tempbanned", isBanned.ToString());
#endif
#if PREMIUM
                Redis.db.HashDeleteAsync("tempbannedPremium", isBanned.ToString());
#endif
            }
            if (res.Equals(true))
                {
                    Methods.SaveBan(userId, "tempban");
                    Redis.db.HashDeleteAsync($"chat:{chatId}:userJoin", userId);

#if NORMAL
                var entry = Redis.db.HashGetAsync("tempbanned", unbanTime).Result;
                    while (entry.HasValue)
                    {
                            dataUnbanTime.AddSeconds(1);
                        unbanTime = dataUnbanTime.ToUnixTime();
                        entry = Redis.db.HashGetAsync("tempbanned", unbanTime).Result;
                }
                    Redis.db.HashSetAsync("tempbanned", unbanTime, hash);
#endif
#if PREMIUM
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
                        Bot.SendReply(message,update);
                    }
                    else
                    {
                        Bot.Send(message, chatId);
                    } 

                Redis.db.StringSetAsync($"chat:{chatId}:tempbanned:{userId}", unbanTime, TimeSpan.FromMinutes(time));
#if NORMAL
                Redis.db.SetAddAsync($"chat:{chatId}:tempbanned", userId);                    
#endif
#if PREMIUM
                     Redis.db.SetAddAsync($"chat:{chatId}:tempbannedPremium", userId);
#endif
                    return true;
                }
            return false;
        }        

        [Command(Trigger = "tempban", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Tempban(Update update, string[] args)
        {

            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            long userId = 0, time;
            string length = "";
            string units = "";
            if (update.Message.ReplyToMessage != null) // by reply
            {
                userId = update.Message.ReplyToMessage.From.Id; // user id is id of replied message
                if (userId == Bot.Me.Id) return;

                if (args[1] != null)
                {
                    length = args[1].Split(' ')[0];    
                     
                    try
                    {
                        units = args[1].Split(' ')[1];
                    }
                    catch (Exception e)
                    {
                        units = "min";
                    }
                }

            }
            else if (args[1] != null)
            {
                if (args[1].Contains(' ')) // not by reply but contains a space so we might have userid and time
                {
                    var user = args[1].Split(' ')[0]; // either username or ID
                    length = args[1].Split(' ')[1]; // Length of the ban, or the first word of the reason, if no time is specified. Parsing will fail then and time set to 60.
                    try
                    {
                        units = args[1].Split(' ')[2];
                    }
                    catch (Exception e)
                    {
                        units = "min";
                    }

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
                    if (userId == Bot.Me.Id) return;
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
            double calculatedTime = 0;
            switch (units)
            {
                case "min":
                case "mins":
                case "minutes":
                case "minute":
                    calculatedTime = TimeSpan.FromMinutes(time).TotalMinutes;
                    break;
                case "hour":
                case "hours":
                    calculatedTime = TimeSpan.FromHours(time).TotalMinutes;
                    break;
                case "days":
                case "day":
                    calculatedTime = TimeSpan.FromDays(time).TotalMinutes;
                    break;
                default:
                    calculatedTime = TimeSpan.FromMinutes(time).TotalMinutes;
                    break;
            }
            if (userId != 0)
            {
                Tempban(userId, update.Message.Chat.Id, calculatedTime, Methods.GetNick(update.Message, args, userId));
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "delmsg", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void DeleteMessageInGroup(Update update, string[] args)
        {
            Bot.DeleteMessage(update.Message.Chat.Id, update.Message.ReplyToMessage.MessageId);
            Service.LogCommand(update, update.Message.Text);
        }

        /// <summary>
        /// Who a mute command is about, or 0 when there is nobody to act on. A post made as a
        /// channel, or by an anonymous admin, carries a placeholder sender that must not be muted
        /// in its place, so those get an explanation instead. Like /ban, the bot and the admin
        /// themselves are skipped silently: without a reply or an argument, GetUserId returns the
        /// admin.
        /// </summary>
        private static long MuteTarget(Update update, string[] args, System.Xml.Linq.XDocument lang)
        {
            switch (ChannelPosts.FromModerationCommand(update.Message, args.Length > 1 ? args[1] : null,
                        update.Message.Chat.Id, out _, out _))
            {
                case ChannelTargetKind.Group:
                    Bot.SendReply(Methods.GetLocaleString(lang, "cannotmuteadmin"), update);
                    return 0;
                case ChannelTargetKind.Channel:
                    Bot.SendReply(Methods.GetLocaleString(lang, "cannotMuteChannel"), update);
                    return 0;
            }

            var userId = Methods.GetUserId(update, args);
            if (userId == Bot.Me.Id || userId == update.Message.From.Id) return 0;
            return userId;
        }

        [Command(Trigger = "mute", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Mute(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var userId = MuteTarget(update, args, lang);
                if (userId == 0) return;

                // On failure MuteUser has already told the chat why.
                if (!Methods.MuteUser(chatId, userId, lang)) return;

                object[] arguments =
                {
                    Methods.GetNick(update.Message, args, userId),
                    Methods.GetNick(update.Message, args, true)
                };
                Service.LogCommand(update, update.Message.Text);
                Bot.SendReply(Methods.GetLocaleString(lang, "muted", arguments), update);
            }
            catch (Exception e)
            {
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "tempmute", GroupAdminOnly = true, InGroupOnly = true)]
        public static void TempMute(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var userId = MuteTarget(update, args, lang);
                if (userId == 0) return;

                TimeSpan duration;
                switch (Duration.TryParse(Duration.AfterTarget(update.Message, args.Length > 1 ? args[1] : null), out duration))
                {
                    case DurationParseResult.Ok:
                        break;
                    case DurationParseResult.Empty:
                        duration = TimeSpan.FromMinutes(Methods.GetGroupTempMuteTime(chatId));
                        break;
                    default:
                        Bot.SendReply(Methods.GetLocaleString(lang, "invalidDuration"), update);
                        return;
                }

                // Pure UTC: Telegram reads untilDate as UTC.
                var until = DateTime.UtcNow.Add(duration);
                if (!Methods.TempMuteUser(chatId, userId, until, lang)) return;

                Service.LogCommand(update, update.Message.Text);
                Bot.SendReply(Methods.GetLocaleString(lang, "temmpmuted", Duration.ToDisplay(duration),
                    Methods.GetNick(update.Message, args, userId), userId), update);
            }
            catch (Exception e)
            {
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "unmute", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Unmute(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var userId = MuteTarget(update, args, lang);
                if (userId == 0) return;

                if (!Methods.UnmuteUser(chatId, userId, lang)) return;

                object[] arguments =
                {
                    Methods.GetNick(update.Message, args, userId),
                    Methods.GetNick(update.Message, args, true)
                };
                Bot.SendReply(Methods.GetLocaleString(lang, "unmuted", arguments), update);
                Service.LogCommand(update, update.Message.Text);
            }
            catch (Exception e)
            {
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "mutelist", GroupAdminOnly = true, InGroupOnly = true)]
        public static void MuteList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var muted = Repositories.Mutes.GetMutedAsync(chatId).GetAwaiter().GetResult();
                var now = DateTime.UtcNow;
                var tempMuted = Repositories.Mutes.GetTempMutedAsync(chatId, now).GetAwaiter().GetResult();

                var mutedLines = StillMuted(chatId, muted.Select(id => (id, "")));
                var tempLines = StillMuted(chatId, tempMuted
                    .OrderBy(t => t.UntilUtc)
                    .Select(t => (t.UserId, $" - {Duration.ToDisplay(t.UntilUtc - now)}")));

                if (mutedLines.Count == 0 && tempLines.Count == 0)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "noMutedUsers"), update);
                    return;
                }
                Bot.SendReply(Methods.GetLocaleString(lang, "muteList",
                    mutedLines.Count == 0 ? "-" : string.Join("\n", mutedLines),
                    tempLines.Count == 0 ? "-" : string.Join("\n", tempLines)), update);
            }
            catch (Exception e)
            {
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "tempbanlist", GroupAdminOnly = true, InGroupOnly = true)]
        public static void TempbanList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var now = DateTime.UtcNow;
                var tempbans = Repositories.Tempbans.GetActiveAsync(update.Message.Chat.Id, now).GetAwaiter().GetResult();
                if (tempbans.Count == 0)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "noTempbans"), update);
                    return;
                }
                // Tempbanned users are not in the chat, so names come from the bot's own records.
                var lines = tempbans.Select(t =>
                    $"{DescribeUser(t.UserId, null)} - {Duration.ToDisplay(t.UntilUtc - now)}");
                Bot.SendReply(Methods.GetLocaleString(lang, "tempbanList", string.Join("\n", lines)), update);
            }
            catch (Exception e)
            {
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        /// <summary>
        /// One line per user that Telegram still shows as muted, each followed by its suffix. Users
        /// it shows as free (unmuted by hand in Telegram, banned, or gone) are dropped from the
        /// bot's records. If Telegram cannot be asked, the user is listed and kept.
        /// </summary>
        internal static List<string> StillMuted(long chatId, IEnumerable<(long UserId, string Suffix)> users)
        {
            var lines = new List<string>();
            foreach (var (userId, suffix) in users)
            {
                User user = null;
                try
                {
                    var member = Bot.Api.GetChatMember(chatId, userId).Result;
                    user = member.User;
                    if (!(member is ChatMemberRestricted restricted) || restricted.CanSendMessages)
                    {
                        Repositories.Mutes.ClearAsync(chatId, userId).GetAwaiter().GetResult();
                        continue;
                    }
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Checking the mute of {userId} in {chatId} failed: {Bot.AsApiError(e)?.Message ?? e.Message}");
                }
                lines.Add(DescribeUser(userId, user) + suffix);
            }
            return lines;
        }

        /// <summary>"Name @username (id)", HTML-escaped. Falls back to the stored name, then the id alone.</summary>
        internal static string DescribeUser(long userId, User user)
        {
            string name;
            if (user != null)
            {
                name = user.FirstName.FormatHTML();
                if (!string.IsNullOrEmpty(user.Username)) name += $" @{user.Username}";
            }
            else
            {
                name = Methods.GetName(userId);
            }
            return string.IsNullOrWhiteSpace(name) ? $"{userId}" : $"{name} ({userId})";
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
             Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
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
             Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
               text);
        }

        [Callback(Trigger = "resetPrewarns", GroupAdminOnly = true)]
        public static void ResetPreWarns(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message, true).Doc;
            var userId = args[2];
            Redis.db.HashDeleteAsync($"chat:{call.Message.Chat.Id}:prewarns", userId);

            Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
               Methods.GetLocaleString(lang, "warnsReset", call.From.FirstName));
        }

        [Callback(Trigger = "removePrewarn", GroupAdminOnly = true)]
        public static void RemovePreWarn(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message, true).Doc;
            var userId = args[2];
            var res = Redis.db.HashIncrementAsync($"chat:{call.Message.Chat.Id}:prewarns", userId, -1).Result;
            var text = "";
            text = Methods.GetLocaleString(lang, "warnRemoved");
            if (res < 0)
            {
                Redis.db.HashSetAsync($"chat:{call.Message.Chat.Id}:prewarns", userId, 0);
            }
            Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId,
              text);
        }
    }
}
