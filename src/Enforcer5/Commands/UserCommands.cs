using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
#pragma warning disable CS4014
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "ping")]
        public static async Task Ping(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            try
            {                
                var ts = DateTime.UtcNow - update.Message.Date;
                var send = DateTime.UtcNow;
                var message = Methods.GetLocaleString(lang.Doc, "PingInfo", $"{ts:mm\\:ss\\.ff}\n{Program.MessagePxPerSecond} MAX IN | {Program.MessageTxPerSecond} MAX OUT");
                var result = Bot.Send(message, update.Message.Chat.Id).Result;
                var second  = DateTime.UtcNow - send;
                message += "\n" + Methods.GetLocaleString(lang.Doc, "Ping2", $"{second:mm\\:ss\\.ff}");
                await Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId, message);
            }
            catch (Exception e)
            {
                Methods.SendError($"{e.Message}\n{e.StackTrace}", update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "getCommands")]
        public static async Task GetCommands(Update update, string[] args)
        {
            var comList = string.Join("\n", Bot.Commands.Select(e => e.Trigger));
            await Bot.SendReply(comList, update);
        }

        [Command(Trigger = "start")]
        public static async Task BotStarted(Update update, string[] args)
        {
            if (update.Message.Chat.Type == ChatType.Private)
            {
                await Redis.db.HashIncrementAsync("bot:general", "users");
                await Bot.Send(
                    "Welcome to Enforcer.\nPlease send /getCommands to get a command list.\n/help for more information", update);
                await Redis.db.HashSetAsync("bot:users", update.Message.From.Id, "xx");
            }
        }

        [Command(Trigger = "helplist")]
        public static async Task HelpList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            await Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
        }

        [Command(Trigger = "help")]
        public static async Task Help(Update update, string[] args)
        {
            var command = -1;
            var request = "";
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (args.Length > 1)
            {
                if (!string.IsNullOrEmpty(args[1]))
                {
                    command = 0;
                    foreach (var mem in Bot.Commands)
                    {
                        if (args[1].Equals(mem.Trigger))
                        {
                            request = mem.Trigger;
                            command = 1;
                            string text;
                            try
                            {
                                text = Methods.GetLocaleString(lang, $"hcommand{request}", request);                                
                            }
                            catch (Exception e)
                            {
                                text = Methods.GetLocaleString(lang, "helpOptionNotImplemented", request);
                            }
                            await Bot.SendReply(text, update);
                            return;
                        }
                    }
                }
            }           
            if (command == -1)
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "helpNoRequest"), update);
                await Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
            }
            else if (command == 0)
            {
                request = args[1];
                string text;
                try
                {
                    text = Methods.GetLocaleString(lang, $"hcommand{request}", request);
                }
                catch (Exception e)
                {
                    text = Methods.GetLocaleString(lang, "helpNoRequest");
                    await Bot.SendReply(Methods.GetLocaleString(lang, "gethelplist", Methods.GetHelpList(lang)), update);
                }
                await Bot.SendReply(text, update);                
            }           
        }

    }
}
