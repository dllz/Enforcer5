﻿using System;
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
                var ts = DateTime.UtcNow - update.Message.Date;
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
                 Redis.db.HashIncrementAsync("bot:general", "users");
                 Bot.Send(
                    "Welcome to Enforcer.\nPlease send /getCommands to get a command list.\n/help for more information", update);
                 Redis.db.HashSetAsync("bot:users", update.Message.From.Id, "xx");
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
                             Bot.SendReply(text, update);
                            return;
                        }
                    }
                }
            }           
            if (command == -1)
            {
                 Bot.SendReply(Methods.GetLocaleString(lang, "helpNoRequest"), update);
                 Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
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
                        Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
                    }
                }
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

    }
}
