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

        [Command(Trigger = "Start")]
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
            
    }
}
