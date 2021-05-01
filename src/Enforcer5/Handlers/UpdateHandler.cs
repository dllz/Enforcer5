using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.InputMessageContents;
using Telegram.Bot.Types.ReplyMarkups;

#pragma warning disable CS4014 // Because this call is not ed, execution of the current method continues before the call is completed
namespace Enforcer5.Handlers
{

    internal static class UpdateHandler
    {

        internal static Dictionary<long, SpamDetector> UserMessages = new Dictionary<long, SpamDetector>();
       // internal static Dictionary<long, SpamDetector> BlockReplies = new Dictionary<long, SpamDetector>();
        public static void UpdateReceived(object sender, UpdateEventArgs e)
        {
#if premium
            Redis.db.StringSetAsync("bot:last_Premium_update", Bot.Api.MessageOffset);
#endif
#if normal
            Redis.db.StringSetAsync("bot:last_update", Bot.Api.MessageOffset);
#endif
           
            if (e.Update.Message == null) return;
            if ((e.Update.Message?.Date.ToUniversalTime() ?? DateTime.MinValue) < Bot.StartTime.AddMinutes(-2))
                return; //toss it
            new Task(() => { HandleUpdate(e.Update); }).Start();
        }
        private static void Log(Update update, string text, Models.Commands command = null)
        {
            if (text.Equals("text"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Red;
                if (command != null) Console.Write(command.Method.GetMethodInfo().Name);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{(DateTime.Now - update.Message.Date):mm\\:ss\\.ff}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.Chat.Title} {update.Message.Chat.Id}]");                
                Botan.log(update.Message, command.Trigger);
            }
            else if (text.Equals("chatMember"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{(DateTime.Now - update.Message.Date):mm\\:ss\\.ff}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.NewChatMember.FirstName} {update.Message.NewChatMember.Id}]");
                Botan.log(update.Message, "welcome");
            }
            else if (text.Equals("extra"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{(DateTime.Now - update.Message.Date):mm\\:ss\\.ff}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.Chat.Title} {update.Message.Chat.Id}]");
                Botan.log(update.Message, "extra");
            }
            
        }
        private static void Log(CallbackQuery update, Models.CallBacks command = null)
        {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            //Console.Write($"{(DateTime.Now - update.Message.Date):mm\\:ss\\.ff}");
            Console.ForegroundColor = ConsoleColor.Red;
            if (command != null)
            {
                Console.Write(command.Method.GetMethodInfo().Name);
                Botan.log(update, command.Method.GetMethodInfo().Name);
            }
               
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.From.FirstName} {update.From.Id}]");
            
        }

        private static void Log(InlineQuery update, string text, Models.Queries command = null)
        {
            if (command != null)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                if (command.Method != null)
                {
                    Console.Write(command.Method.GetMethodInfo().Name);
                    Botan.log(update, command.Method.GetMethodInfo().Name);
                }
                else
                {
                    Botan.log(update, "Query");
                }
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($" Query:  {text}");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($" [{update.From.FirstName} {update.From.Id}]");
        }

        private static async void HandleUpdate(Update update)
        {
            {
#if premium
                if (update.Message.Chat.Type != ChatType.Private)
                {
                    var allowed = Redis.db.SetContainsAsync("premiumBot", update.Message.Chat.Id).Result;
                    if (!allowed)
                    {       
                        Bot.Send(
                            "Hi there, this bot is no longer active, please use @enforcerbot instead of this bot and remove this bot from your group to stop the spam.\nIt has the same features and more.\nRemember to subscribe to our channel @greywolfdev for updates for @enforcerbot and more",
                            update);
                        Bot.Api.LeaveChatAsync(update.Message.Chat.Id);
                        return;
                    }
                }                
#endif  
               
                //return;
                var bannedGroup = Redis.db.SetContainsAsync("bot:bannedGroups", update.Message.Chat.Id).Result;
                var bannedUser = Redis.db.SetContainsAsync("bot:bannedGroups", update.Message.From.Id).Result;
                if (bannedGroup || bannedUser)
                {
                    if (update.Message.Chat.Type != ChatType.Private && bannedGroup)
                    {
                        Bot.Api.LeaveChatAsync(update.Message.Chat.Id);                      
                    }
                    return;
                } 

                new Task(() => { CollectStats(update.Message); }).Start();                
                Bot.MessagesProcessed++;
                new Task(() => { Methods.IsRekt(update); }).Start();
                //ignore previous messages
                //if (update.Message?.Chat.Type != ChatType.Private && update.Message?.Chat.Id != -1001108140050)
                //{
                //    Bot.Send("please use @enforcerbot", update);
                //    Bot.Api.LeaveChatAsync(update.Message.Chat.Id);
                //    Console.WriteLine("LEaving chat");
                //    return;
                //}
                try
                {
                   // Console.WriteLine("Checking Message");                    
                    if (update.Message == null)
                    {
                        return;
                    }
                    if (update.Message.Chat.Type != ChatType.Private)
                    {
                        new Task(() => { OnMessage.AntiFlood(update); }).Start();
                    }
                    
                    switch (update.Message.Type)
                    {
                        case MessageType.UnknownMessage:
                            break;
                        case MessageType.TextMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.ArabDetection(update); }).Start();
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                                
                            if (update.Message.Text.StartsWith("/"))
                            {
                                var args = GetParameters(update.Message.Text);
                                args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                                //check for the command
                                //Console.WriteLine("Looking for command");
                                var command = Bot.Commands.FirstOrDefault(
                                    x =>
                                        String.Equals(x.Trigger, args[0],
                                            StringComparison.CurrentCultureIgnoreCase));
                                if (command != null)
                                {                                  
                                    
                                   
                                    var blocked = Redis.db.StringGetAsync($"spammers{update.Message.From.Id}").Result;
                                    if (blocked.HasValue)
                                    {
                                        return;
                                    }
                                    if (command.DevOnly && !Constants.Devs.Contains(update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    if (command.GroupAdminOnly && !Methods.IsGroupAdmin(update) &
                                        !Methods.IsGlobalAdmin(update.Message.From.Id) & !Constants.Devs.Contains(update.Message.From.Id))
                                    {
                                        Bot.SendReply(
                                            Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message, true).Doc,
                                                "userNotAdmin"), update.Message);
                                        return;
                                    }
                                    if (Constants.Devs.Contains(update.Message.From.Id) & (command.GroupAdminOnly | command.DevOnly))
                                    {
                                        Service.LogDevCommand(update, update.Message.Text);
                                    }

                                    if (command.InGroupOnly && update.Message.Chat.Type == ChatType.Private)
                                    {
                                        return;
                                    }
                                    if (command.RequiresReply && update.Message.ReplyToMessage == null)
                                    {
                                        var lang = Methods.GetGroupLanguage(update.Message, true);
                                        Bot.SendReply(Methods.GetLocaleString(lang.Doc, "noReply"), update);
                                        return;
                                    }
                                    if (command.UploadAdmin && !Methods.IsLangAdmin(update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    if (command.GlobalAdminOnly && !Methods.IsGlobalAdmin(update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    Bot.CommandsReceived++;
                                     command.Method.Invoke(update, args);
                                    new Task(() => { Log(update, "text", command); }).Start();
                                }
                            }
                            else if (update.Message.Text.StartsWith("#"))
                            {
                                string[] args = new string[1];
                                args[0] = update.Message.Text;
                                if (update.Message.Chat.Type == ChatType.Private)
                                {
                                    return;
                                }
                                var blocked = Redis.db.StringGetAsync($"spammers{update.Message.From.Id}").Result;
                                if (blocked.HasValue)
                                {
                                    return; ;
                                }
                                new Task(() => { Log(update, "extra"); }).Start();
                                 Task.Run(() => Commands.SendExtra(update, args));
                            }
                            else if (update.Message.Text.StartsWith("@admin") | update.Message.Text.StartsWith("@pingall"))
                            {
                                var args = GetParameters(update.Message.Text);
                                args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                                //check for the command
                                //Console.WriteLine("Looking for command");
                                var command = Bot.Commands.FirstOrDefault(
                                    x =>
                                        String.Equals(x.Trigger, args[0],
                                            StringComparison.CurrentCultureIgnoreCase));
                                if (command != null)
                                {
                                    new Task(() => { Log(update, "text", command); }).Start();
                                    AddCount(update.Message.From.Id, update.Message.Text);
                                    //check that we should run the command
                                    var blocked = Redis.db.StringGetAsync($"spammers{update.Message.From.Id}").Result;
                                    if (blocked.HasValue)
                                    {
                                        return; ;
                                    }
                                    if (command.DevOnly & !Constants.Devs.Contains((long)update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    if (command.GroupAdminOnly & !Methods.IsGroupAdmin(update) &
                                        !Methods.IsGlobalAdmin(update.Message.From.Id))
                                    {
                                        Bot.SendReply(
                                            Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message,true).Doc,
                                                "userNotAdmin"), update.Message);
                                        return;
                                    }
                                    if (command.InGroupOnly & update.Message.Chat.Type == ChatType.Private)
                                    {
                                        return;
                                    }
                                    if (command.RequiresReply & update.Message.ReplyToMessage == null)
                                    {
                                        var lang = Methods.GetGroupLanguage(update.Message,true);
                                        Bot.SendReply(Methods.GetLocaleString(lang.Doc, "noReply"), update);
                                        return;
                                    }
                                    if (command.GlobalAdminOnly & !Methods.IsGlobalAdmin(update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    Bot.CommandsReceived++;
                                     command.Method.Invoke(update, args);
                                }
                            }
                            break;
                        case MessageType.PhotoMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                                Commands.IsNSFWImage(update.Message.Chat.Id, update.Message);
                            }
                            break;
                        case MessageType.AudioMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.VideoMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                                Commands.IsNSFWVideo(update.Message.Chat.Id, update.Message);
                            }
                            break;
                        case MessageType.VoiceMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.DocumentMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                Commands.IsNSFWGif(update.Message.Chat.Id, update.Message);
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();

                            }
                            break;
                        case MessageType.StickerMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                Commands.IsNSFWStickers(update.Message.Chat.Id, update.Message);
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.LocationMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.ContactMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.ServiceMessage:
                            if (update.Message.NewChatMembers != null && update.Message.NewChatMembers.Length > 0)
                            {
                                try
                                {
                                    var blocked = Redis.db.StringGetAsync($"spammers{update.Message.NewChatMember.Id}").Result;
                                    if (blocked.HasValue)
                                    {
                                        return; ;
                                    }
                                    new Task(() => { Log(update, "chatMember"); }).Start();
                                    var isBanned = Redis.db.StringGetAsync($"chat:{update.Message.Chat.Id}:tempbanned:{update.Message.NewChatMember}").Result;
                                    if (isBanned.HasValue)
                                    {
#if normal
                                        Redis.db.HashDeleteAsync("tempbanned", isBanned.ToString());
#endif
#if premium
                Redis.db.HashDeleteAsync("tempbannedPremium", isBanned.ToString());
#endif
                                    }
                                    new Task(() => { OnMessage.ArabJoinDetection(update); }).Start();
                                    if (update.Message.NewChatMember.Id == Bot.Me.Id)
                                    {
                                         Service.BotAdded(update.Message);
                                    }
                                    else
                                    {
                                        Service.Welcome(update.Message);
                                        var hash = $"chat:{update.Message.Chat.Id}:settings";
                                        var muteOnJoin = Redis.db.HashGet(hash, "MuteOnJoin");
                                        var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
                                        if (muteOnJoin == "no")
                                        {
                                            Methods.MuteUser(update.Message.Chat.Id, update.Message.NewChatMember.Id, lang, true);
                                        }
                                        // Service.ResetUser(update.Message);
                                        
                                    }
#if premium
                                     if ((update.Message.Chat.Id == -1001060486754 | update.Message.Chat.Id ==-1001030085238) && update.Message.NewChatMembers.Length > 1)
                                    {
                                        for (int i = 0; i < update.Message.NewChatMembers.Length; i++)
                                        {
                                            try
                                            {
                                                bool res = Commands.Tempban(update.Message.NewChatMembers[i].Id, update.Message.Chat.Id, 60, message: $"User: {update.Message.NewChatMembers[i].Id} has been tempbanned for an hour as they were added by {update.Message.From.Id}");
                                                Thread.Sleep(2000);
                                            }
                                            catch (Exception e)
                                            {
                                                Console.WriteLine(e.Message);
                                            }
                                        }
                                        try
                                        {
                                            bool res = Commands.Tempban(update.Message.From.Id, update.Message.Chat.Id, 120, message: $"User: {update.Message.From.Id} has been tempbanned for 2 hours as they added to many members");
                                        }
                                        catch (Exception e)
                                        {
                                            Console.WriteLine(e.Message);
                                        }                                            
                                    }
#endif

                                }
                                catch (ApiRequestException e)
                                {
                                    Console.WriteLine(e);
                                }
                                catch (AggregateException e)
                                {
                                    Console.WriteLine(e);
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e);
                                }
                            }
                            break;
                        case MessageType.VenueMessage:
                            if (update.Message.Chat.Type != ChatType.Private)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.GameMessage:
                            break;
                        default:
                            return;
                            break;
                    }
                }
                catch (AggregateException e)
                {
                    Console.WriteLine(e);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);                    
                }
            }
        }

        private static void AddCount(long id, string command)
        {
            try
            {
                if (!UserMessages.ContainsKey(id))
                    UserMessages.Add(id, new SpamDetector { Messages = new HashSet<UserMessage>() });
                UserMessages[id].Messages.Add(new UserMessage(command));
            }
            catch (Exception e)
            {
                // ignored
            }
        }

        private static void AddCount(int id, Message m)
        {
            try
            {
                if (!UserMessages.ContainsKey(id))
                    UserMessages.Add(id, new SpamDetector { Messages = new HashSet<UserMessage>() });
               


            }
            catch
            {
                // ignored

            }
        }

        private static async void CollectStats(Message updateMessage)
        {
            try
            {
                //Console.WriteLine("Collecting Stats");
                Redis.db.HashIncrementAsync("bot:general", "messages");
                Redis.db.HashSetAsync($"user:{updateMessage.From.Id}", "name", updateMessage.From.FirstName);            
                if (updateMessage?.From?.Username != null)
                {
                    Redis.db.HashSetAsync("bot:usernames", $"@{updateMessage.From.Username.ToLower()}", updateMessage.From.Id);                                   
                    Redis.db.HashSetAsync($"user:{updateMessage.From.Id}", "username", $"@{updateMessage.From.Username.ToLower()}");
                }
                if (updateMessage?.ForwardFrom?.Username != null)
                {
                    Redis.db.HashSetAsync("bot:usernames", $"@{updateMessage.ForwardFrom.Username.ToLower()}", updateMessage.ForwardFrom.Id);                    
                    Redis.db.HashSetAsync($"user:{updateMessage.ForwardFrom.Id}", "username", $"@{updateMessage.ForwardFrom.Username.ToLower()}");
                }
                if (updateMessage?.Chat.Type != ChatType.Private)
                {
                    Redis.db.HashSetAsync($"chat:{updateMessage.Chat.Id}:details", "name", updateMessage.Chat.Title);
                    if (updateMessage?.From != null)
                    {
                        Redis.db.HashIncrementAsync($"chat:{updateMessage.From.Id}", "msgs");
                        Redis.db.HashIncrementAsync($"{updateMessage.Chat.Id}:users:{updateMessage.From.Id}", "msgs");
                        Redis.db.HashSetAsync($"chat:{updateMessage.Chat.Id}:userlast", updateMessage.From.Id, System.DateTime.Now.Ticks);
                        Redis.db.StringSetAsync($"chat:{updateMessage.Chat.Id}:chatlast", DateTime.Now.Ticks);
                    }                  
                    var updated = Redis.db.SetContainsAsync("lenghtUpdate3",updateMessage.Chat.Id).Result;
                    if (!updated)
                    {
                        Service.NewSettings(updateMessage.Chat.Id);
                        Redis.db.SetAddAsync("lenghtUpdate3", updateMessage.Chat.Id);
                    }
                    updated = Redis.db.SetContainsAsync("lenghtUpdate", updateMessage.Chat.Id).Result;
                    if (!updated)
                    {
                        Service.NewSetting2(updateMessage.Chat.Id);
                        Redis.db.SetAddAsync("lenghtUpdate", updateMessage.Chat.Id);
                    }
                    updated = Redis.db.SetContainsAsync("dbUpdate:lenghtUpdate", updateMessage.Chat.Id).Result;
                    if (!updated)
                    {
                        Service.NewSetting3(updateMessage.Chat.Id);
                        Redis.db.SetAddAsync("dbUpdate:lenghtUpdate", updateMessage.Chat.Id);
                    }
                    updated = Redis.db.SetContainsAsync("dbUpdate:lenghtUpdat4", $"{updateMessage.Chat.Id}:{updateMessage.From.Id}").Result;
                    if (!updated)
                    {
                        Service.removeWarn0(updateMessage.Chat.Id, updateMessage.From.Id);
                        Redis.db.SetAddAsync("dbUpdate:lenghtUpdat4", $"{updateMessage.Chat.Id}:{updateMessage.From.Id}");
                    }
                }
            }
            catch (Exception e)
            {

                Bot.Send($"shit happened\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }

        }
           
        private static string[] GetParameters(string input)
        {
            if (input.Length == 0)
            {
                return new string[] {"", null};
            }
            return input.Contains(" ") ? new[] { input.Substring(1, input.IndexOf(" ")).Trim(), input.Substring(input.IndexOf(" ") + 1) } : new[] { input.Substring(1).Trim(), null };
        }
        private static string[] GetInlineParameters(string input)
        {
            if (input.Length == 0)
            {
                return new string[] { "", null };
            }
            return input.Contains(" ") ? new[] { input.Substring(0, input.IndexOf(" ")).Trim(), input.Substring(input.IndexOf(" ") + 1) } : new[] { input, null };
        }
        private static string[] GetCallbackParameters(string input)
        {
            return input.Split(':');
        }

         internal static void SpamDetection()
        {
            while (true)
            {
                try
                {
                    var temp = UserMessages.ToDictionary(entry => entry.Key, entry => entry.Value);
                    //var quickRemove = BlockReplies.ToDictionary(entry => entry.Key, entry => entry.Value);
                    //clone the dictionary
                    foreach (var key in temp.Keys.ToList())
                    {
                        try
                        {
                            //drop older messages (1 minute)
/*                            temp[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddMinutes(-1));
#if normal
                            quickRemove[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddSeconds(-10));
#endif
#if premium
                            quickRemove[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddSeconds(-4));
#endif*/
                            //comment this out - if we remove it, it doesn't keep the warns
                            //if (temp[key].Messages.Count == 0)
                            //{
                            //    temp.Remove(key);
                            //    continue;
                            //}
                            //now count, notify if limit hit
#if normal
                            if (temp[key].Messages.Count() < 5)
                            {
                                temp[key].NotifiedAdmin = false;
                            }
#endif
#if premium
                            if (temp[key].Messages.Count() < 10)
                            {
                                temp[key].NotifiedAdmin = false;
                            }
#endif
#if normal
                            if (temp[key].Messages.Count() >= 5) // 20 in a minute
                            {
#endif
#if premium
                            if (temp[key].Messages.Count() >= 10) // 20 in a minute
                            {
#endif
#if normal
                                if (temp[key].Messages.Count < 10)
                                {
#endif
#if premium
                                if (temp[key].Messages.Count < 15)
                                {
#endif
                                    if (temp[key].NotifiedAdmin == false)
                                    {
                                        try
                                        {
                                            Bot.Send($"Please do not spam me. Next time is automated ban.", key);
                                        }
                                        catch (Exception e)
                                        {
                                            Console.WriteLine(e);
                                            
                                        }
                                        temp[key].NotifiedAdmin = true;
                                    }
                                    //Send($"User {key} has been warned for spamming: {temp[key].Warns}\n{temp[key].Messages.GroupBy(x => x.Command).Aggregate("", (a, b) => a + "\n" + b.Count() + " " + b.Key)}",
                                    //    Para);                                    
                                    continue;
                                }
                                var number = 11;
#if premium
                                number = 15;
#endif
                        if ((temp[key].Warns >= 3 || temp[key].Messages.Count > number))
                                {
                                    Redis.db.StringSetAsync($"spammers{key}", key, TimeSpan.FromMinutes(10));
                                    Console.ForegroundColor = ConsoleColor.Green;
                                    Console.WriteLine($"{key} - Banned for 10 minutes");
                                    temp[key].Warns = 1;
                                    temp[key].NotifiedAdmin = false;
                                    try
                                    {
                                        Bot.Send("You have been banned for 10 minutes due to spam", long.Parse(key.ToString()));
                                        Thread.Sleep(10000);
                                        Bot.Send(
                                            $"{long.Parse(key.ToString())}, {Methods.GetName(long.Parse(key.ToString()))}, {Methods.GetUsername(long.Parse(key.ToString()))} has been spam banned for 10 minutes.",
                                            -1001076212715);
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);
                                      
                                    }
                                }

                                temp[key].Messages.Clear();
                            }
                        }
                        catch (Exception e)
                        {
                            //Console.WriteLine(e.Message);
                        }
                    }
                    UserMessages = temp;
                }
                catch (Exception e)
                {
                    //Console.WriteLine(e.Message);
                }
                Thread.Sleep(1000);
            }
        }
        internal static Message Send(string message, long id,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html)
        {
            return Bot.Send(message, id, customMenu, parseMode);
        }

        public static void InlineQueryReceived(object sender, InlineQueryEventArgs e)
        {
            new Task(() => { HandleInlineQuery(e.InlineQuery); }).Start();
        }
        
        internal static void HandleInlineQuery(InlineQuery q)
        {
            try
            {
                var query = q.Query;
                var com = GetInlineParameters(query);
                var results = new List<InlineQueryResultArticle>();
                var userLang = Methods.GetGroupLanguage(q.From).Doc;
                
                Models.Queries matchedTrigger = null;
                if(!string.IsNullOrEmpty(com[0]))
                {
                    try
                    {
                        matchedTrigger = Bot.Queries.First(x => x.Trigger.Contains(com[0]));
                    }
                    catch (Exception e)
                    {
                        //nothing happens                        
                    }                   
                }
                if (matchedTrigger == null)
                    matchedTrigger = new Models.Queries()
                    {
                        Trigger = ""
                    };
               var optionDictionary = new Dictionary<string, string>();
                new Task(() => { Log(q, query, matchedTrigger); }).Start();
                if (string.IsNullOrEmpty(matchedTrigger.Trigger) && !string.IsNullOrEmpty(com[0]))
                {
                    var helpArticles = InlineMethods.GetHelpArticles(com[0], userLang);
                    foreach (var help in helpArticles)
                    {
                        var des = new string(help.details.Take(50).ToArray());
                        results.Add(new InlineQueryResultArticle
                        {
                            Description = $"{des}...",
                            Title = $"{help.name}",
                            Id = help.name,
                            InputMessageContent = new InputTextMessageContent
                            {
                                DisableWebPagePreview = true,
                                MessageText = help.details,
                                ParseMode = ParseMode.Html
                            }
                        });
                    }
                }
                else
                {

                    if (!string.IsNullOrEmpty(matchedTrigger.Trigger))
                    {
                        results = matchedTrigger.Method.Invoke(q.From, com, userLang);
                    }
                    else
                    {
                        var choices = Bot.Queries.Where(x => x.Trigger.ToLower().Contains(query.ToLower()));
                        foreach (var choice in choices)
                        {
                            results.Add(new InlineQueryResultArticle()
                            {
                                Description = Methods.GetLocaleString(userLang, $"{choice.Trigger}Description"),
                                Title = $"{choice.Title}",
                                Id = choice.Trigger,
                                InputMessageContent = new InputTextMessageContent()
                                {
                                    DisableWebPagePreview = true,
                                    MessageText = Methods.GetLocaleString(userLang, "typeMore"),
                                    ParseMode = ParseMode.Default
                                }
                            });
                        }
                    }
                }
                var menu = results.Take(50).Cast<InlineQueryResult>().ToArray();
                var res = Bot.Api.AnswerInlineQueryAsync(q.Id, menu, 0).Result;

            }
            catch (Exception e)
            {
                Console.WriteLine($"{e.Message}\n{e.StackTrace}");
            }
        }

        public static void CallbackHandler(object sender, CallbackQueryEventArgs e)
        {
            new Task(() => { HandleCallback(e.CallbackQuery); }).Start();
            
        }

        public static async void HandleCallback(CallbackQuery update)
        {
            var callback = update.Data;
            if (!string.IsNullOrEmpty(callback))
            {

                try
                {
                    //if ((update.Message?.Date ?? DateTime.MinValue) < Bot.StartTime.AddMinutes(-20))
                    //    return; //toss it
                    var args = GetCallbackParameters(update.Data);
                    args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                    //check for the command
                    // Console.WriteLine("Looking for command");
                    var callbacks = Bot.CallBacks.FirstOrDefault(
                        x =>
                            String.Equals(x.Trigger, args[0],
                                StringComparison.CurrentCultureIgnoreCase));
                    if (callbacks != null)
                    {                                               
                        var blocked = Redis.db.StringGetAsync($"spammers{update.From.Id}").Result;
                        if (blocked.HasValue)
                        {
                            return;
                            ;
                        }
                        if (callbacks.DevOnly & !Constants.Devs.Contains(update.From.Id))
                        {
                            return;
                        }
                        if (callbacks.UploadAdmin & !Methods.IsLangAdmin(update.From.Id))
                        {
                            return;
                        }
                        if (args.Length >= 2)
                        {
                            if (!string.IsNullOrEmpty(args[1]))
                            {
                                if (callbacks.GroupAdminOnly &
                                    !Methods.IsGroupAdmin(update.From.Id, long.Parse(args[1])))
                                {
                                    
                                    return;
                                }
                                if (callbacks.GroupAdminOnly)
                                {
                                    Service.LogCommand(long.Parse(args[1]), update.From.Id, update.From.FirstName, update.Message.Chat.Title, callbacks.Trigger, isCallback: true);
                                }
                            }
                        }
                        if (callbacks.InGroupOnly & update.Message.Chat.Type == ChatType.Private)
                        {
                            return;
                        }
                        Bot.CommandsReceived++;
                        new Task(() => { Log(update, callbacks); }).Start();
                        try
                        {
                            callbacks.Method.Invoke(update, args);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"{e.Message}\n{e.StackTrace}");
                        }                         
                    }
                }
                catch (ApiRequestException e)
                {
                    Console.WriteLine(e);
                }
                catch (AggregateException e)
                {
                    Console.WriteLine(e);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }
    }
}



