using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

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

        [Command(Trigger = "help")]
        public static async Task Help(Update update, string[] args)
        {
            var command = false;
            var request = "";
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (args.Length > 0)
            {
                foreach (var mem in Bot.Commands)
                {
                    if (args[0].Contains(mem.Trigger))
                    {
                        command = true;
                        request = mem.Trigger;
                    }
                }
            }
            if (command == false)
            {
                await Bot.SendReply(Methods.GetLocaleString(lang, "helpNoRequest"), update);
            }
            else
            {
                switch (request.ToLower())
                {
                    case "kickme":
                        await Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{request}", request), update);
                        break;
                    case "kick":
                        await Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{request}", request), update);
                        break;
                    case "ban":
                        await Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{request}", request), update);
                        break;
                    case "unban":
                        await Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{request}", request), update);
                        break;
                    case "tempban":
                        await Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{request}", request), update);
                        break;
                    case "admin":
                        await Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{request}", request), update);
                        break;
                    case "adminoff":
                        await Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{request}", request), update);
                        break;
                    default:
                        await Bot.SendReply(Methods.GetLocaleString(lang, "helpOptionNotImplemented", request), update);
                        break;
                }
            }
        }

    }
}
