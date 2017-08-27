using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
#pragma warning disable CS4014
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "ping")]
        public static void Ping(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, false);
            try
            {                
                var ts = DateTime.Now - update.Message.Date;
                var send = DateTime.UtcNow;
                var message = Methods.GetLocaleString(lang.Doc, "PingInfo", $"{ts:mm\\:ss\\.ff}\n{Program.MessagePxPerSecond} MAX IN | {Program.MessageTxPerSecond} MAX OUT");
                var result = Bot.Send(message, update.Message.Chat.Id);
                var second  = DateTime.UtcNow - send;
                message += "\n" + Methods.GetLocaleString(lang.Doc, "Ping2", $"{second:mm\\:ss\\.ff}");
                 Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId, message);
            }
            catch (Exception e)
            {
                Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "getCommands")]
        public static void GetCommands(Update update, string[] args)
        {
            var comList = string.Join("\n", Bot.Commands.Select(e => e.Trigger));
             Bot.SendReply(comList, update);
        }

        [Command(Trigger = "start")]
        public static void BotStarted(Update update, string[] args)
        {
            if (update.Message.Chat.Type == ChatType.Private)
            {
                if (!String.IsNullOrEmpty(args[1]))
                {
                    if (args[1].Split('#')[0].Equals("pingme"))
                    {
                        var group = args[1].Split('#')[1];
                        long longGroup;
                        if (long.TryParse(group, out longGroup))
                        {
                            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
                            Redis.db.SetAddAsync($"chat:{longGroup}:tagall2", update.Message.From.Id);
                            Bot.SendReply(Methods.GetLocaleString(lang, "registerfortagall"), update);
                        }
                    }   
                }
                else
                {
                    Redis.db.HashIncrementAsync("bot:general", "users");
                    Bot.Send(
                        "Welcome to Enforcer.\nPlease send /getCommands to get a command list.\n/help for more information", update);
                    Redis.db.HashSetAsync("bot:users", update.Message.From.Id, "xx");
                }
            }
        }

        [Command(Trigger = "helplist")]
        public static void HelpList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
             Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(Methods.GetGroupLanguage(-1001076212715).Doc)), update);
        }

        [Command(Trigger = "help")]
        public static void Help(Update update, string[] args)
        {
            var command = -1;
            var request = "";
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            var sendToPm = Methods.SendInPm(update.Message, "Help");
            if (args.Length > 1)
            {
                if (!string.IsNullOrEmpty(args[1]))
                {
                    command = 0;
                    foreach (var mem in Bot.Commands)
                    {
                        if (args[1].ToLower().Equals(mem.Trigger.ToLower()))
                        {
                            request = mem.Trigger.ToLower();
                            command = 1;
                            string text;
                            try
                            {
                                text = Methods.GetLocaleString(lang, $"hcommand{request}", request);                                
                            }
                            catch (Exception e)
                            {
                                try
                                {
                                    lang = Methods.GetGroupLanguage(-1001076212715).Doc;
                                    text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                                }
                                catch (Exception ep)
                                {
                                    text = Methods.GetLocaleString(lang, "helpOptionNotImplemented", request);
                                }
                            }
                            if (sendToPm)
                                Bot.SendToPm(text, update);
                            else
                                Bot.SendReply(text, update);
                            return;
                        }
                    }
                }
            }           
            if (command == -1)
            {
                if (sendToPm)
                {
                    Bot.SendToPm(Methods.GetLocaleString(lang, "helpNoRequest"), update);
                    Bot.SendToPm(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "helpNoRequest"), update);
                    Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
                }
                 
            }
            else if (command == 0)
            {
                request = args[1].ToLower();
                string text;
                try
                {
                    text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                }

                catch (Exception e)
                {
                    try
                    {
                        lang = Methods.GetGroupLanguage(-1001076212715).Doc;
                        text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                    }
                    catch (Exception ep)
                    {

                        text = Methods.GetLocaleString(lang, "helpNoRequest");
                        if(sendToPm)
                            Bot.SendToPm(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
                        else
                            Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
                    }
                }
                if (sendToPm)
                    Bot.SendToPm(text, update);
                else
                 Bot.SendReply(text, update);                
            }           
        }

        [Command(Trigger = "donate")]
        public static void Donate(Update update, string[] args)
        {
            System.Xml.Linq.XDocument lang;
            try
            {
                lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            }
            catch (NullReferenceException e)
            {
                try
                {
                    lang = Methods.GetGroupLanguage(-1001076212715).Doc;
                }
                catch (NullReferenceException exception)
                {
                    Console.WriteLine(exception);
                    return;
                }
            }
            var startMe = new Menu(1)
            {
                Buttons = new List<InlineButton>
                {
                    new InlineButton(Methods.GetLocaleString(lang, "donateWord"), url:$"https://paypal.me/stubbornrobot")
                }
            };
            Bot.SendReply(Methods.GetLocaleString(lang, "donate", "paypal.me/stubbornrobot or Bitcoin: 13QvBKfAattcSxSsW274fbgnKU5ASpnK3A"), update, keyboard:Key.CreateMarkupFromMenu(startMe));
        }

        [Command(Trigger = "unping", InGroupOnly = true, GroupAdminOnly = true)]
        public static void UntagOverride(Update update, string[] args)
        {
            long chatId = update.Message.Chat.Id;
            long userId = Methods.GetUserId(update, args);
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            Redis.db.SetRemoveAsync($"chat:{chatId}:tagall2", userId);
            Bot.SendReply(Methods.GetLocaleString(lang, "unregisterfortagall"), update);
        }      

        [Command(Trigger = "pingme", InGroupOnly = true)]
        public static void TagMe(Update update, string[] args)
        {
            long chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            var button = new Menu(1)
            {
                Buttons = new List<InlineButton>(1)
                {
#if premium
                    new InlineButton("Register", url:$"t.me/enforcedbot?start=pingme#{chatId}")
#endif
#if normal
                    new InlineButton("Register", url:$"t.me/enforcerbot?start=pingme#{chatId}")
#endif
                }
            };
            Bot.SendReply(Methods.GetLocaleString(lang, "registerfortagallclickme"), update, Key.CreateMarkupFromMenu(button));
        }

        [Command(Trigger = "pingall", InGroupOnly = true)]
        public static void TagAll(Update update, string[] args)
        {
            long chatId = update.Message.Chat.Id;            
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            long userId = update.Message.From.Id;
            var spamcheck = Redis.db.StringGetAsync($"chat:{ chatId}:spamList").Result;
            if(spamcheck.HasValue)
                return;
            var set = Redis.db.SetScan($"chat:{chatId}:tagall2").ToList();
            
            string list = "";
            long num = 0;
            foreach (var mem in set)
            {
                if (mem.HasValue && long.TryParse(mem.ToString(), out num))
                {
                    list = $"{list} <a href=\"tg:///user?id={num}\">{Methods.GetName(num)}</a>";
                }
            }
            if(set.Count > 0)
                Bot.SendReply(Methods.GetLocaleString(lang, "tagallregistered", list), update);
            else
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "tagallregisterednoone"), update);
            }
            Redis.db.StringSetAsync($"chat:{chatId}:spamList", "true", TimeSpan.FromMinutes(10));
        }

        [Command(Trigger = "unpingme", InGroupOnly = true)]
        public static void unTagMe(Update update, string[] args)
        {
            long chatId = update.Message.Chat.Id;
            long userId = update.Message.From.Id;
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            Redis.db.SetRemoveAsync($"chat:{chatId}:tagall", userId);
            Bot.SendReply(Methods.GetLocaleString(lang, "unregisterfortagall"), update);
        }
    }
}
