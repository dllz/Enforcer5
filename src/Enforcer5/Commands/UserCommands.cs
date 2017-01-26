using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "ping")]
        public static void Ping(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message);
            try
            {                
                var ts = DateTime.UtcNow - update.Message.Date;
                var send = DateTime.UtcNow;
                var message = Methods.GetLocaleString(lang.Doc, "PingInfo", $"{ts:mm\\:ss\\.ff}",
                    $"\n{Program.MessagePxPerSecond.ToString("F0")} MAX IN | {Program.MessageTxPerSecond.ToString("F0")} MAX OUT");
                var result = Bot.Send(message, update.Message.Chat.Id).Result;
                ts = DateTime.UtcNow - send;
                message += "\n" + Methods.GetLocaleString(lang.Doc, "Ping2", $"{ts:mm\\:ss\\.ff}");
                Bot.Api.EditMessageTextAsync(update.Message.Chat.Id, result.MessageId, message);
            }
            catch (Exception e)
            {
                Methods.SendError(e.InnerException, update.Message, lang.Doc);
            }
        }
    }
}
