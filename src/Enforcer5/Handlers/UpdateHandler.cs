using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

#pragma warning disable CS4014 // Because this call is not ed, execution of the current method continues before the call is completed
namespace Enforcer5.Handlers
{

    internal static class UpdateHandler
    {

        internal static Dictionary<int, SpamDetector> UserMessages = new Dictionary<int, SpamDetector>();
        public static void UpdateReceived(object sender, UpdateEventArgs e)
        {
            if (e.Update.Message == null) return;
            new Task(() => { HandleUpdate(e.Update); }).Start();
#if premium
            Redis.db.StringSetAsync("bot:last_Premium_update", Bot.Api.MessageOffset);
#endif
#if normal
            Redis.db.StringSetAsync("bot:last_update", Bot.Api.MessageOffset);
#endif
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
                Console.Write($"{(DateTime.UtcNow - update.Message.Date):mm\\:ss\\.ff}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.Chat.Title} {update.Message.Chat.Id}]");
            }else if (text.Equals("chatMember"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{(DateTime.UtcNow - update.Message.Date):mm\\:ss\\.ff}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.NewChatMember.FirstName} {update.Message.NewChatMember.Id}]");
            }else if (text.Equals("extra"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{(DateTime.UtcNow - update.Message.Date):mm\\:ss\\.ff}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.Chat.Title} {update.Message.Chat.Id}]");
            }
            Botan.log(update.Message, text);
        }
        private static void Log(CallbackQuery update, Models.CallBacks command = null)
        {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            //Console.Write($"{(DateTime.UtcNow - update.Message.Date):mm\\:ss\\.ff}");
            Console.ForegroundColor = ConsoleColor.Red;
                if (command != null) Console.Write(command.Method.GetMethodInfo().Name);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.From.FirstName} {update.From.Id}]");      
        }

        private static async void HandleUpdate(Update update)
        {
            {
#if premium
                if (update.Message.Chat.Type != ChatType.Private)
                {
                    var allowed = Redis.db.SetContainsAsync("premiumBot", update.Message.Chat.Id).Result;
                    if (!allowed)
                         Bot.Api.LeaveChatAsync(update.Message.Chat.Id);
                }                
#endif             
                new Task(() => { CollectStats(update.Message); }).Start();                
                Bot.MessagesProcessed++;
                new Task(() => { Methods.IsRekt(update); }).Start();
                //ignore previous messages
                //if (update.Message?.Chat.Type != ChatType.Private && update.Message?.Chat.Id != -1001076212715)
                //{
                //    Bot.Api.LeaveChatAsync(update.Message.Chat.Id);
                //    Console.WriteLine("LEaving chat");
                //    return;
                //}


                if ((update.Message?.Date ?? DateTime.MinValue) < Bot.StartTime.AddSeconds(-10) && Bot.testing == false)
                    return; //toss it
                //Console.WriteLine("Checking Global Ban");
                if (update.Message?.Date.ToUniversalTime() < System.DateTime.UtcNow.AddSeconds(-30) && Bot.testing == false)
                   return;
                //Settings.Main.LogText += update?.Message?.Text + Environment.NewLine;  
                           
                try
                {
                    //Console.WriteLine("Checking Message");                    
                    if (update.Message == null) return;
                    if (update.Message.Chat.Type == ChatType.Supergroup)
                    {
                        new Task(() => { OnMessage.AntiFlood(update); }).Start();
                    }
                    //new Task(() => { OnMessage.CheckMedia(update); }).Start();
                    switch (update.Message.Type)
                    {
                        case MessageType.UnknownMessage:
                            break;
                        case MessageType.TextMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.ArabDetection(update); }).Start();
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
                                    new Task(() => { Log(update, "text", command); }).Start();
                                    AddCount(update.Message.From.Id, update.Message.Text);
                                    //check that we should run the command
                                    var blocked = Redis.db.StringGetAsync($"spammers{update.Message.From.Id}").Result;
                                    if (blocked.HasValue)
                                    {
                                        return;;
                                    }
                                    if (command.DevOnly & !Constants.Devs.Contains(update.Message.From.Id))
                                    {                                        
                                        return;
                                    }
                                    if (command.GroupAdminOnly & !Methods.IsGroupAdmin(update) &
                                        !Methods.IsGlobalAdmin(update.Message.From.Id) & !Constants.Devs.Contains(update.Message.From.Id))
                                    {
                                        Bot.SendReply(
                                            Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message).Doc,
                                                "userNotAdmin"), update.Message);
                                        return;
                                    }
                                    if (Constants.Devs.Contains(update.Message.From.Id) & (command.GroupAdminOnly | command.DevOnly))
                                    {
                                        Service.LogDevCommand(update, update.Message.Text);
                                    }
                                    if (command.InGroupOnly & update.Message.Chat.Type == ChatType.Private)
                                    {
                                        return;
                                    }
                                    if (command.RequiresReply & update.Message.ReplyToMessage == null)
                                    {
                                        var lang = Methods.GetGroupLanguage(update.Message);
                                        Bot.SendReply(Methods.GetLocaleString(lang.Doc, "noReply"), update);
                                        return;
                                    }
                                    if (command.UploadAdmin & !Methods.IsLangAdmin(update.Message.From.Id))
                                    {
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
                            else if (update.Message.Text.StartsWith("@admin"))
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
                                    if (command.DevOnly & !Constants.Devs.Contains(update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    if (command.GroupAdminOnly & !Methods.IsGroupAdmin(update) &
                                        !Methods.IsGlobalAdmin(update.Message.From.Id))
                                    {
                                        Bot.SendReply(
                                            Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message).Doc,
                                                "userNotAdmin"), update.Message);
                                        return;
                                    }
                                    if (command.InGroupOnly & update.Message.Chat.Type == ChatType.Private)
                                    {
                                        return;
                                    }
                                    if (command.RequiresReply & update.Message.ReplyToMessage == null)
                                    {
                                        var lang = Methods.GetGroupLanguage(update.Message);
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
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                Commands.IsNSFWImage(update.Message.Chat.Id, update.Message);
                            }
                            break;
                        case MessageType.AudioMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.VideoMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.VoiceMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.DocumentMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.StickerMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.LocationMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.ContactMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        case MessageType.ServiceMessage:
                            if (update.Message.NewChatMember != null)
                            {
                                try
                                {
                                    var blocked = Redis.db.StringGetAsync($"spammers{update.Message.NewChatMember.Id}").Result;
                                    if (blocked.HasValue)
                                    {
                                        return; ;
                                    }
                                    new Task(() => { Log(update, "chatMember"); }).Start();
                                    if (update.Message.NewChatMember.Id == Bot.Me.Id)
                                    {
                                         Service.BotAdded(update.Message);
                                    }
                                    else
                                    {
                                         Service.Welcome(update.Message);
                                        // Service.ResetUser(update.Message);
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
                            break;
                        case MessageType.VenueMessage:
                            if (update.Message.Chat.Type == ChatType.Supergroup)
                            {
                                new Task(() => { OnMessage.CheckMedia(update); }).Start();
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                catch (ApiRequestException e)
                {
                    Console.WriteLine(e);
                    try
                    {                        
                        if (e.ErrorCode == 112)
                        {
                            if (update.Message != null && update.Message.Chat.Title != null)
                            {
                                var lang = Methods.GetGroupLanguage(update.Message).Doc;
                                Bot.SendReply(
                                    Methods.GetLocaleString(lang, "markdownBroken"), update);
                            }
                            else
                            {
                                Bot.SendReply("The markdown in this text is broken", update);
                            }
                        }
                        else if (e.ErrorCode == 403)
                        {
                            var lang = Methods.GetGroupLanguage(update.Message).Doc;
                            var startMe = new Menu(1)
                            {
                                Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "StartMe"),
                                        url: $"https://t.me/{Bot.Me.Username}")
                                }
                            };
                            Bot.SendReply(Methods.GetLocaleString(lang, "botNotStarted"), update, Key.CreateMarkupFromMenu(startMe));
                        }
                        else
                        {
                            Bot.SendReply($"{e.ErrorCode}\n{e.Message}, 1231", update);
                            Bot.Send($"1\n{e.ErrorCode}\n\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
                        }                        
                    }
                    catch (ApiRequestException ex)
                    {
                        //fuckit  
                        Console.WriteLine(ex);
                    }
                    catch (Exception ex)
                    {
                        //fuckit
                        Console.WriteLine(ex);
                    }
                }
                catch (AggregateException e)
                {
                    Console.WriteLine(e);
                    Bot.Send($"{e.InnerExceptions[0]}\n{e.StackTrace}", update);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    try
                    {
                        if (ex.Message.Equals("UnableToResolveUsername"))
                        {
                            Bot.Send($"an error occured:\n{ex.Message}", update);
                        }
                        else
                        {
                            Bot.Send($"Please contact @werewolfsupport, an error occured:\n{ex.Message}", update);
                        }
                    }
                    catch (Exception e)
                    {
                        //fuckit
                        Console.WriteLine(e);
                    }
                    try
                    {
                         Bot.Send($"@falconza shit happened\n{ex.Message}\n\n{ex.StackTrace}", -1001076212715);
                    }
                    catch (Exception et)
                    {
                        Console.WriteLine(et);
                        try
                        {
                             Bot.Send($"@falconza shit happened\n{ex.Message}\n\n{ex.StackTrace}", Constants.Devs[0]);
                        }
                        catch (Exception e)
                        {
                            //fuckit
                            Console.WriteLine(e);
                        }
                    }
                }
            }
        }

        private static void AddCount(int id, string command)
        {
            try
            {
                if (!UserMessages.ContainsKey(id))
                    UserMessages.Add(id, new SpamDetector { Messages = new HashSet<UserMessage>() });
                UserMessages[id].Messages.Add(new UserMessage(command));
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
                    Redis.db.HashSetAsync($"bot:usernames:{updateMessage.Chat.Id}", $"@{updateMessage.From.Username.ToLower()}", updateMessage.From.Id);                    
                    Redis.db.HashSetAsync($"user:{updateMessage.From.Id}", "username", $"@{updateMessage.From.Username.ToLower()}");
                }
                if (updateMessage?.ForwardFrom?.Username != null)
                {
                    Redis.db.HashSetAsync("bot:usernames", $"@{updateMessage.ForwardFrom.Username.ToLower()}", updateMessage.ForwardFrom.Id);
                    Redis.db.HashSetAsync($"bot:usernames:{updateMessage.Chat.Id}", $"@{updateMessage.ForwardFrom.Username.ToLower()}", updateMessage.ForwardFrom.Id);
                    Redis.db.HashSetAsync($"user:{updateMessage.From.Id}", "username", $"@{updateMessage.ForwardFrom.Username.ToLower()}");
                }
                if (updateMessage?.Chat.Type != ChatType.Private)
                {
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
                }
            }
            catch (Exception e)
            {

                Bot.Send($"@falconza shit happened\n{e.Message}\n\n{e.StackTrace}", -1001076212715);
            }

        }
           
        private static string[] GetParameters(string input)
        {
            return input.Contains(" ") ? new[] { input.Substring(1, input.IndexOf(" ")).Trim(), input.Substring(input.IndexOf(" ") + 1) } : new[] { input.Substring(1).Trim(), null };
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
                    //clone the dictionary
                    foreach (var key in temp.Keys.ToList())
                    {
                        try
                        {
                            //drop older messages (1 minute)
                            temp[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddMinutes(-1));

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
        internal static Message Send(string message, long id, bool clearKeyboard = false,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html)
        {
            return Bot.Send(message, id, clearKeyboard, customMenu, parseMode);
        }

        public static void InlineQueryReceived(object sender, InlineQueryEventArgs e)
        {
            new Task(() => { HandleInlineQuery(e.InlineQuery); }).Start();
        }
        
        internal static void HandleInlineQuery(InlineQuery q)
        {

            //var commands = new InlineCommand[]
            //{
            //    new StatsInlineCommand(q.From),
            //};
            
            //List<InlineCommand> choices;
            //if (String.IsNullOrWhiteSpace(q.Query))
            //{
            //    //show all commands available
            //    choices = commands.ToList();
            //}
            //else
            //{
            //    //let's figure out what they wanted
            //    var com = q.Query;
            //    choices = commands.Where(command => command.Command.StartsWith(com) || Commands.ComputeLevenshtein(com, command.Command) < 3).ToList();
            //}

            //Bot.Api.AnswerInlineQuery(q.Id, choices.Select(c => new InlineQueryResultArticle()
            //{
            //    Description = c.Description,
            //    Id = c.Command,
            //    Title = c.Command,
            //    InputMessageContent = new InputTextMessageContent
            //    {
            //        DisableWebPagePreview = true,
            //        MessageText = c.Content,
            //        ParseMode = ParseMode.Html
            //    }
            //}).Cast<InlineQueryResult>().ToArray(), 0, true);
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
                        AddCount(update.Message.From.Id, update.Message.Text);
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
                                    !Methods.IsGroupAdmin(update.From.Id, long.Parse(args[1])) &
                                    !Methods.IsGlobalAdmin(update.From.Id))
                                {
                                    Bot.Send(
                                        Methods.GetLocaleString(Methods.GetGroupLanguage(update.From.Id).Doc,
                                            "userNotAdmin"), update.From.Id);
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
                         callbacks.Method.Invoke(update, args);
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



