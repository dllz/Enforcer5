using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public class FlagCommands
    {
        [Command(Trigger = "admin", InGroupOnly = true)]
        public static async void Admin(Update update, string[] args)
        {
            if (Methods.IsGroupAdmin(update))
            {
                return;
            }
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (update.Message.ReplyToMessage != null)
            {
                var alreadyFlagged = Redis.db.HashExists($"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}", "reported");
                if (alreadyFlagged)
                {                  
                    await Bot.SendReply(Methods.GetLocaleString(lang, "alreadyFlagged"), update.Message);
                    return;
                }
                if (update.Message.ReplyToMessage.From.Id == Bot.Me.Id)
                {
                    return;
                }                               
            }
            var mods = GetModId(update.Message.Chat.Id);
            var repId = update.Message.MessageId;
            var msgId = update.Message.MessageId;
            var isReply = false;
            if (update.Message.ReplyToMessage != null)
            {
                isReply = true;
                msgId = update.Message.ReplyToMessage.MessageId;
            }
            var reporter = update.Message.From.FirstName;
            var username = update.Message.Chat.Username;
            if (update.Message.From.Username != null)
            {
                reporter = $"{reporter} (@{update.Message.From.Username}";
            }
            SendToAdmins(mods, update.Message.Chat.Id, msgId, reporter, isReply, update.Message.Chat.Title,update.Message, repId, username, lang);
        }

        private static void SendToAdmins(List<int> mods, long chatId, int msgId, string reporter, bool isReply, string chatTitle, Message updateMessage, int repId, string username, XDocument lang)
        {
            var sendMessageIds = new List<int>();
            var modsSentTo = new List<long>();
            var count = 0;
            foreach (var mod in mods)
            {
                Bot.Api.ForwardMessageAsync(mod, chatId, msgId);
                Task<Message> result;
                if (!string.IsNullOrEmpty(username))
                {
                    if (updateMessage.ReplyToMessage != null)
                    {
                        var buttons = new[]
                        {
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "ban"), $"banflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "kick"), $"kickflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),                            
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "warn"), $"warnflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "markSolved"), $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                        };
                        var menu = new InlineKeyboardMarkup(buttons.ToArray());
                        result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId), mod,
                            false, menu);
                    }
                    else
                    {
                        var buttons = new[]
                        {
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "markSolved"), $"solveflag:{updateMessage.Chat.Id}:{repId}"), 
                        };
                        var menu = new InlineKeyboardMarkup(buttons.ToArray());
                        result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId), mod,
                            false, menu);
                    }
                }
                else
                {
                    if (updateMessage.ReplyToMessage != null)
                    {
                        var buttons = new[]
                        {
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "ban"), $"banflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "kick"), $"kickflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "warn"), $"warnflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "markSolved"), $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                        };
                        var menu = new InlineKeyboardMarkup(buttons.ToArray());
                        result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId), mod,
                            false, menu);
                    }
                    else
                    {
                        var buttons = new[]
                        {
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "markSolved"), $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                        };
                        var menu = new InlineKeyboardMarkup(buttons.ToArray());
                        result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId), mod,
                            false, menu);
                    }
                }
                if (result.Result != null)
                {
                    sendMessageIds.Add(result.Result.MessageId);
                    modsSentTo.Add(result.Result.Chat.Id);
                    count++;
                }
            }
            var hash = $"flagged:{chatId}:{msgId}";
            var time = System.DateTime.UtcNow.ToString("hh:mm:ss dd:mm:yyyy");
            var alreadyReported = Redis.db.HashGet(hash, "Solved");
            if (alreadyReported.IsNullOrEmpty)
            {
                Redis.db.HashSet(hash, "Solved", 0);
                Redis.db.HashSet(hash, "Created", time);
                Redis.db.HashSet(hash, "#Admin", count);
                Redis.db.HashSet(hash, "Solved", reporter);
                Redis.db.HashSet(hash, "Solved", repId);
            }
            if (count > 0)
            {
               Bot.Send(Methods.GetLocaleString(lang, "reported", repId), chatId);
            }
        }

        private static List<int> GetModId(long id)
        {
            var res = Bot.Api.GetChatAdministratorsAsync(id);
            return res.Result.Select(member => member.User.Id).ToList();
        }
    }
}
