using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
namespace Enforcer5.Handlers
{

    internal static class UpdateHandler
    {
        public static void UpdateReceived(object sender, UpdateEventArgs e)
        {
            new Task(() => { HandleUpdate(e.Update); }).Start();
        }

        //        private static void AddCount(int id, string command)
        //        {
        //            try
        //            {
        //                if (!UserMessages.ContainsKey(id))
        //                    UserMessages.Add(id, new SpamDetector { Messages = new HashSet<UserMessage>() });
        //                UserMessages[id].Messages.Add(new UserMessage(command));
        //            }
        //            catch
        //            {
        //                // ignored
        //            }
        //        }

        //        internal static void BanMonitor()
        //        {
        //            while (true)
        //            {
        //                try
        //                {
        //                    //first load up the ban list
        //                    using (var db = new WWContext())
        //                    {
        //                        foreach (var id in SpamBanList)
        //                        {
        //                            var p = db.Players.FirstOrDefault(x => x.TelegramId == id);
        //                            var name = p?.Name;
        //                            var count = p?.TempBanCount ?? 0;
        //                            count++;
        //                            if (p != null)
        //                                p.TempBanCount = count; //update the count

        //                            var expireTime = DateTime.Now;
        //                            switch (count)
        //                            {
        //                                case 1:
        //                                    expireTime = expireTime.AddHours(12);
        //                                    break;
        //                                case 2:
        //                                    expireTime = expireTime.AddDays(1);
        //                                    break;
        //                                case 3:
        //                                    expireTime = expireTime.AddDays(3);
        //                                    break;
        //                                default: //perm ban
        //                                    expireTime = (DateTime)SqlDateTime.MaxValue;
        //                                    break;

        //                            }
        //                            db.GlobalBans.Add(new GlobalBan
        //                            {
        //                                BannedBy = "Moderator",
        //                                Expires = expireTime,
        //                                TelegramId = id,
        //                                Reason = "Spam / Flood",
        //                                BanDate = DateTime.Now,
        //                                Name = name
        //                            });
        //                        }
        //                        SpamBanList.Clear();
        //                        db.SaveChanges();

        //                        //now refresh the list
        //                        var list = db.GlobalBans.ToList();
        //#if RELEASE2
        //                        for (var i = list.Count - 1; i >= 0; i--)
        //                        {
        //                            if (list[i].Expires > DateTime.Now) continue;
        //                            db.GlobalBans.Remove(db.GlobalBans.Find(list[i].Id));
        //                            list.RemoveAt(i);
        //                        }
        //                        db.SaveChanges();
        //#endif

        //                        BanList = list;
        //                    }
        //                }
        //                catch
        //                {
        //                    // ignored
        //                }

        //                //refresh every 20 minutes
        //                Thread.Sleep(TimeSpan.FromMinutes(1));
        //            }
        //        }

        //        internal static void SpamDetection()
        //        {
        //            while (true)
        //            {
        //                try
        //                {
        //                    var temp = UserMessages.ToDictionary(entry => entry.Key, entry => entry.Value);
        //                    //clone the dictionary
        //                    foreach (var key in temp.Keys.ToList())
        //                    {
        //                        try
        //                        {
        //                            //drop older messages (1 minute)
        //                            temp[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddMinutes(-1));

        //                            //comment this out - if we remove it, it doesn't keep the warns
        //                            //if (temp[key].Messages.Count == 0)
        //                            //{
        //                            //    temp.Remove(key);
        //                            //    continue;
        //                            //}
        //                            //now count, notify if limit hit
        //                            if (temp[key].Messages.Count() >= 20) // 20 in a minute
        //                            {
        //                                temp[key].Warns++;
        //                                if (temp[key].Warns < 2 && temp[key].Messages.Count < 40)
        //                                {
        //                                    Send($"Please do not spam me. Next time is automated ban.", key);
        //                                    //Send($"User {key} has been warned for spamming: {temp[key].Warns}\n{temp[key].Messages.GroupBy(x => x.Command).Aggregate("", (a, b) => a + "\n" + b.Count() + " " + b.Key)}",
        //                                    //    Para);
        //                                    continue;
        //                                }
        //                                if ((temp[key].Warns >= 3 || temp[key].Messages.Count >= 40) & !temp[key].NotifiedAdmin)
        //                                {
        //                                    //Send(
        //                                    //    $"User {key} has been banned for spamming: {temp[key].Warns}\n{temp[key].Messages.GroupBy(x => x.Command).Aggregate("", (a, b) => a + "\n" + b.Count() + " " + b.Key)}",
        //                                    //    Para);
        //                                    temp[key].NotifiedAdmin = true;
        //                                    //ban
        //                                    SpamBanList.Add(key);
        //                                    var count = 0;
        //                                    using (var db = new WWContext())
        //                                    {
        //                                        count = db.Players.FirstOrDefault(x => x.TelegramId == key).TempBanCount ?? 0;
        //                                    }
        //                                    var unban = "";
        //                                    switch (count)
        //                                    {
        //                                        case 0:
        //                                            unban = "12 hours";
        //                                            break;
        //                                        case 1:
        //                                            unban = "24 hours";
        //                                            break;
        //                                        case 2:
        //                                            unban = "3 days";
        //                                            break;
        //                                        default:
        //                                            unban =
        //                                                "Permanent. You have reached the max limit of temp bans for spamming.";
        //                                            break;
        //                                    }
        //                                    Send("You have been banned for spamming.  Your ban period is: " + unban,
        //                                        key);
        //                                }

        //                                temp[key].Messages.Clear();
        //                            }
        //                        }
        //                        catch (Exception e)
        //                        {
        //                            //Console.WriteLine(e.Message);
        //                        }
        //                    }
        //                    UserMessages = temp;
        //                }
        //                catch (Exception e)
        //                {
        //                    //Console.WriteLine(e.Message);
        //                }
        //                Thread.Sleep(2000);
        //            }
        //        }


        private static void Log(Update update, string text, Models.Commands command = null)
        {
            if (text.Equals("text"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write(command.Method.GetMethodInfo().Name);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.Chat.Title} {update.Message.Chat.Id}]");
            }else if (text.Equals("callback"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write(command.Method.GetMethodInfo().Name);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.CallbackQuery.From.FirstName} {update.CallbackQuery.From.Id}]");
            }else if (text.Equals("chatMember"))
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"[{System.DateTime.UtcNow.AddHours(2):hh:mm:ss dd-MM-yyyy}] ");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($" {update.Message.From.FirstName} -> [{update.Message.NewChatMember.FirstName} {update.Message.NewChatMember.Id}]");
            }      
        }

        internal static void HandleUpdate(Update update)
        {
            {
                CollectStats(update.Message);
                Bot.MessagesProcessed++;               
                //ignore previous messages

                //if (update.Message?.Chat.Type != ChatType.Private && update.Message?.Chat.Id != -1001077134233)
                //    Bot.Api.LeaveChatAsync(update.Message.Chat.Id);
                if ((update.Message?.Date ?? DateTime.MinValue) < Bot.StartTime.AddSeconds(-10))
                    return; //toss it
                //Console.WriteLine("Checking Global Ban");
                if (Methods.IsRekt(update))
                {
                    return;
                }
                
                //Settings.Main.LogText += update?.Message?.Text + Environment.NewLine;             
                try
                {    
                    //Console.WriteLine("Checking Message");                    
                    switch (update.Message.Type)
                    {
                        case MessageType.UnknownMessage:
                            break;
                        case MessageType.TextMessage:

                            if (update.Message.Text.StartsWith("/"))
                            {
                                var args = GetParameters(update.Message.Text);
                                args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                                //check for the command
                                Console.WriteLine("Looking for command");
                                var command = Bot.Commands.FirstOrDefault(
                                        x =>
                                            String.Equals(x.Trigger, args[0],
                                                StringComparison.CurrentCultureIgnoreCase));
                                if (command != null)
                                {
                                    Log(update, "text", command);
                                    
                                    //check that we should run the command
                                    if (command.DevOnly & !Constants.Devs.Contains(update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    if (command.GroupAdminOnly & !Methods.IsGroupAdmin(update) & !Methods.IsGlobalAdmin(update.Message.From.Id))
                                    {
                                        Bot.SendReply(Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message).Doc, "userNotAdmin"), update.Message);
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
                                    Bot.CommandsReceived++;                                      
                                    command.Method.Invoke(update, args);
                                }
                            }
                            else if (update.Message.Text.StartsWith("@admin"))
                            {
                                var args = GetParameters(update.Message.Text);
                                args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                                //check for the command
                                Console.WriteLine("Looking for command");
                                var command = Bot.Commands.FirstOrDefault(
                                        x =>
                                            String.Equals(x.Trigger, args[0],
                                                StringComparison.CurrentCultureIgnoreCase));
                                if (command != null)
                                {
                                    Log(update, "text", command);
                                        
                                    //check that we should run the command
                                    if (command.DevOnly & !Constants.Devs.Contains(update.Message.From.Id))
                                    {
                                        return;
                                    }
                                    if (command.GroupAdminOnly & !Methods.IsGroupAdmin(update) & !Methods.IsGlobalAdmin(update.Message.From.Id))
                                    {
                                        Bot.SendReply(Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message).Doc, "userNotAdmin"), update.Message);
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
                                    Bot.CommandsReceived++;
                                    command.Method.Invoke(update, args);
                                }
                            }
                            break;                
                        case MessageType.PhotoMessage:
                            break;
                        case MessageType.AudioMessage:
                            break;
                        case MessageType.VideoMessage:
                            break;
                        case MessageType.VoiceMessage:
                            break;
                        case MessageType.DocumentMessage:
                            break;
                        case MessageType.StickerMessage:
                            break;
                        case MessageType.LocationMessage:
                            break;
                        case MessageType.ContactMessage:
                            break;
                        case MessageType.ServiceMessage:
                            if (update.Message.NewChatMember != null)
                            {
                                Log(update, "chatMember");
                                if (update.Message.NewChatMember.Id == Bot.Me.Id)
                                {
                                    Service.BotAdded(update.Message);
                                }
                                else
                                {
                                    Service.Welcome(update.Message);
                                }
                            }                          
                            break;
                        case MessageType.VenueMessage:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                catch (Exception ex)
                {
                    try
                    {
                        Bot.Send($"Please contact @werewolfsupport, an error occured:\n{ex.Message}\n\n{ex.StackTrace}", update);
                    }
                    catch (Exception e)
                    {
                        //fuckit
                    }
                    try
                    {
                        Bot.Send($"@falconza shit happened\n{ex.Message}\n\n{ex.StackTrace}", -1001094155678);
                    }
                    catch (Exception e)
                    {
                        try
                        {
                            Bot.Send($"@falconza shit happened\n{ex.Message}\n\n{ex.StackTrace}", 125311351);
                        }
                        catch (Exception exception)
                        {
                            //fuckit
                        }
                    }
                }
            }
        }

        private static void CollectStats(Message updateMessage)
        {
            try
            {
                Console.WriteLine("Collecting Stats");
                Redis.db.HashIncrementAsync("bot:general", "messages");
                if (updateMessage?.From?.Username != null)
                {
                    Redis.db.HashSetAsync("bot:usernames", $"@{updateMessage.From.Username.ToLower()}", updateMessage.From.Id);
                    Redis.db.HashSetAsync($"bot:usernames:{updateMessage.Chat.Id}", $"@{updateMessage.From.Username.ToLower()}", updateMessage.From.Id);
                }
                if (updateMessage?.ForwardFrom?.Username != null)
                {
                    Redis.db.HashSetAsync("bot:usernames", $"@{updateMessage.ForwardFrom.Username.ToLower()}", updateMessage.ForwardFrom.Id);
                    Redis.db.HashSetAsync($"bot:usernames:{updateMessage.Chat.Id}", $"@{updateMessage.ForwardFrom.Username.ToLower()}", updateMessage.ForwardFrom.Id);
                }
                if (updateMessage?.Chat.Type != ChatType.Private)
                {
                    if (updateMessage?.From != null)
                    {
                        Redis.db.HashIncrementAsync($"chat:{updateMessage.From.Id}", "msgs");
                        Redis.db.HashSetAsync($"chat:{updateMessage.Chat.Id}:userlast", updateMessage.From.Id, System.DateTime.Now.Ticks);
                        Redis.db.StringSetAsync($"chat:{updateMessage.Chat.Id}:chatlast", DateTime.Now.Ticks);
                    }
                }
            }
            catch (Exception e)
            {
                Bot.Send($"@falconza shit happened\n{e.Message}\n\n{e.StackTrace}", -1001094155678);
            }

        }
           
        private static string[] GetParameters(string input)
        {
            return input.Contains(" ") ? new[] { input.Substring(1, input.IndexOf(" ")).Trim(), input.Substring(input.IndexOf(" ") + 1) } : new[] { input.Substring(1).Trim(), null };
        }
        private static string[] GetCallbackParameters(string input)
        {
            return input.Contains(":") ? new[] { input.Substring(1, input.IndexOf(":")).Trim(), input.Substring(input.IndexOf(":") + 1) } : new[] { input.Substring(1).Trim(), null };
        }


        internal static Task<Message> Send(string message, long id, bool clearKeyboard = false,
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

        public static void HandleCallback(CallbackQuery update)
        {
                var callback = update.Data;
                if (!string.IsNullOrEmpty(callback))
                {
                    var args = GetCallbackParameters(update.Data);
                    args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                    //check for the command
                    Console.WriteLine("Looking for command");
                    var callbacks = Bot.CallBacks.FirstOrDefault(
                            x =>
                                String.Equals(x.Trigger, args[0],
                                    StringComparison.CurrentCultureIgnoreCase));
                    if (callbacks != null)
                    {
                        //Log(update, "callback", command);

                        if (callbacks.DevOnly & !Constants.Devs.Contains(update.From.Id))
                        {
                            return;
                        }
                        //if (command.GroupAdminOnly & !Methods.IsGroupAdmin(update.Message..Id) & !Methods.IsGlobalAdmin(update.From.Id))
                        //{
                        //    Bot.Send(Methods.GetLocaleString(Methods.GetGroupLanguage(update.From.Id).Doc, "userNotAdmin"), update.From.Id);
                        //    return;
                        //}
                        if (callbacks.InGroupOnly & update.Message.Chat.Type == ChatType.Private)
                        {
                            return;
                        }
                        Bot.CommandsReceived++;
                        callbacks.Method.Invoke(update, args);
                    }
                }
        }
    }
}

