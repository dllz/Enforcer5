using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Telegram.Bot.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    public static class OnMessage
    {
        public static async Task OnChatMessage(Update update)
        {            
            var msgType = Methods.GetMediaType(update.Message);
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (!isIgnored(chatId, msgType))
            {
                var msgs = Redis.db.StringGetAsync($"spam:{chatId}:{update.Message.From.Id}").Result;
                var num = msgs.IsInteger ? int.Parse(msgs) : 0;
                if (num == 0) num = 1;
                var maxSpam = 8;
                if (update.Message.Chat.Type == ChatType.Private) maxSpam = 12;
                var floodSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:flood").Result;
                var maxMsgs = floodSettings.Where(e => e.Name.Equals("MaxFlood")).FirstOrDefault();
                var maxTime = new TimeSpan(0,0,0,5);               
                if ((DateTime.Now.ToUnixTime() - update.Message.Date.ToUnixTime()) < 30)
                {       
                    await Redis.db.StringSetAsync($"spam:{chatId}:{update.Message.From.Id}", num + 1, maxTime);   
                }
                if (num == int.Parse(maxMsgs.Value) + 1)
                {
                    var action = floodSettings.Where(e => e.Name.Equals("ActionFlood")).FirstOrDefault();
                    var name = update.Message.From.FirstName;
                    if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                    try
                    {
                        if (action.Value.Equals("ban"))
                        {
                            await Methods.BanUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "flood");
                            Methods.AddBanList(chatId, update.Message.From.Id, update.Message.From.FirstName,
                                Methods.GetLocaleString(lang, "bannedForFlood", ""));
                        }
                        else
                        {
                            await Methods.KickUser(chatId, update.Message.From.Id, lang);
                            Methods.SaveBan(update.Message.From.Id, "flood");
                        }
                        if (msgs == (int.Parse(maxMsgs.Value) + 1) || msgs == int.Parse(maxMsgs.Value) + 5)
                        {
                            await Bot.Send(
                                Methods.GetLocaleString(lang, "bannedForFlood", $"{update.Message.From.FirstName}, {update.Message.From.Id}"),
                                update);
                        }
                    }
                    catch (Exception e)
                    {

                    }
                }
            }
        }

        public static bool isIgnored(long chatId, string msgType)
        {
            var status = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", msgType).Result;
            return status.Equals("yes");
        }
    }
}
