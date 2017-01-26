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
                var alreadyFlagged = Redis.db.HashExistsAsync($"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}", "reported");
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

        [Command(Trigger = "solved", InGroupOnly = true, GroupAdminOnly = true)]
        public static async void Solved(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (update.Message.ReplyToMessage != null && string.IsNullOrEmpty(args[2]))
            {
                var msgid = update.Message.ReplyToMessage.MessageId;
                var chatid = update.Message.Chat.Id;
                var hash = $"flagged:{chatid}:{msgid}";
                var isReported = Redis.db.HashGetAsync(hash, "Solved");
                if (!isReported.HasValue)
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "reportNotFound"), update.Message);
                    return;
                }
                int isReport;
                if (isReported.TryParse(out isReport) && isReport == 0)
                {
                    var solvedBy = update.Message.From.FirstName;
                    if (update.Message.From.Username != null)
                        solvedBy = $"{solvedBy} (@{update.Message.From.Username}";
                    var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                    Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                    Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                    Redis.db.HashSetAsync(hash, "Solved", 1);
                    var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin"));
                    var reporter = Redis.db.HashGetAsync(hash, "Reporter");
                    var repID = Redis.db.HashGetAsync(hash, "repID");
                    string text;
                    if (!reporter.HasValue)
                    {
                        text = Methods.GetLocaleString(lang, "solvedByReporter", solvedBy, solvedAt,
                            update.Message.Chat.Title, reporter);
                    }
                    else
                    {
                        text = Methods.GetLocaleString(lang, "solvedBy", solvedBy, solvedAt,
                            update.Message.Chat.Title);
                    }
                    for (int i = 0; i < counter; i++)
                    {
                        var id = Redis.db.HashGetAsync(hash, $"adminID{i}");
                        var msgID = Redis.db.HashGetAsync(hash, $"Message{i}");
                        if (id.HasValue && msgID.HasValue)
                        {
                            await Bot.Api.EditMessageTextAsync(id, msgid,
                                $"{text}\n{Methods.GetLocaleString(lang, "reportID", repID)}");
                        }
                    }
                    await Bot.Send(Methods.GetLocaleString(lang, "markSolved"), chatid);
                }
                else if (isReported.TryParse(out isReport) && isReport == 1)
                {
                    var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt");
                    var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy");
                    await Bot.Send(Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), chatid);
                }
            }
            else if (!string.IsNullOrEmpty(args[2]))
            {
                int msgid;
                if (int.TryParse(args[2], out msgid))
                {
                    var chatid = update.Message.Chat.Id;
                    var hash = $"flagged:{chatid}:{msgid}";
                    var isReported = Redis.db.HashGetAsync(hash, "Solved");
                    if (!isReported.HasValue)
                    {
                        await Bot.SendReply(Methods.GetLocaleString(lang, "reportNotFound"), update.Message);
                        return;
                    }
                    int isReport;
                    if (isReported.TryParse(out isReport) && isReport == 0)
                    {
                        var solvedBy = update.Message.From.FirstName;
                        if (update.Message.From.Username != null)
                            solvedBy = $"{solvedBy} (@{update.Message.From.Username}";
                        var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                        Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                        Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                        Redis.db.HashSetAsync(hash, "Solved", 1);
                        var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin"));
                        var reporter = Redis.db.HashGetAsync(hash, "Reporter");
                        var repID = Redis.db.HashGetAsync(hash, "repID");
                        string text;
                        if (!reporter.HasValue)
                        {
                            text = Methods.GetLocaleString(lang, "solvedByReporter", solvedBy, solvedAt,
                                update.Message.Chat.Title, reporter);
                        }
                        else
                        {
                            text = Methods.GetLocaleString(lang, "solvedBy", solvedBy, solvedAt,
                                update.Message.Chat.Title);
                        }
                        for (int i = 0; i < counter; i++)
                        {
                            var id = Redis.db.HashGetAsync(hash, $"adminID{i}");
                            var msgID = Redis.db.HashGetAsync(hash, $"Message{i}");
                            if (id.HasValue && msgID.HasValue)
                            {
                                await Bot.Api.EditMessageTextAsync(id, msgid,
                                    $"{text}\n{Methods.GetLocaleString(lang, "reportID", repID)}");
                            }
                        }
                        await Bot.Send(Methods.GetLocaleString(lang, "markSolved"), chatid);
                    }
                    else if (isReported.TryParse(out isReport) && isReport == 1)
                    {
                        var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt");
                        var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy");
                        await Bot.Send(Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), chatid);
                    }
                }
                else
                {
                    await Bot.Send(Methods.GetLocaleString(lang, "incorrectArgument"), update.Message.Chat.Id);
                }
            }
            else
                await Bot.Send(Methods.GetLocaleString(lang, "solvedNoReply"), update.Message.Chat.Id);
        }

        private static async void SendToAdmins(List<int> mods, long chatId, int msgId, string reporter, bool isReply, string chatTitle, Message updateMessage, int repId, string username, XDocument lang)
        {
            var sendMessageIds = new List<int>();
            var modsSentTo = new List<long>();
            var count = 0;
            var groupLink = Redis.db.HashGetAsync($"chat:{chatId}links", "link");
            foreach (var mod in mods)
            {
                await Bot.Api.ForwardMessageAsync(mod, chatId, msgId);
                Task<Message> result;
                if (!string.IsNullOrEmpty(username))
                {
                    if (updateMessage.ReplyToMessage != null)
                    {
                        var buttons = new[]
                        {
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "ban"), $"banflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "kick"), $"kickflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),                            
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "Warn"), $"warnflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "markSolved"), $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "goToMessage"))
                            {
                                Url = $"http://t.me/{username}/{repId}"
                            }
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
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "goToMessage"))
                            {
                                Url = $"http://t.me/{username}/{repId}"
                            }
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
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "Warn"), $"warnflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "markSolved"), $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                            groupLink.HasValue ? 
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "goToChat"))
                            {
                                Url = groupLink
                            } : null
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
                            groupLink.HasValue ?
                            new InlineKeyboardButton(Methods.GetLocaleString(lang, "goToChat"))
                            {
                                Url = groupLink
                            } : null
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
            var time = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
            var alreadyReported = Redis.db.HashGetAsync(hash, "Solved");
            if (alreadyReported.IsNullOrEmpty)
            {
                Redis.db.HashSetAsync(hash, "Solved", 0);
                Redis.db.HashSetAsync(hash, "Created", time);
                Redis.db.HashSetAsync(hash, "#Admin", count);
                Redis.db.HashSetAsync(hash, "Solved", reporter);
                Redis.db.HashSetAsync(hash, "Solved", repId);
            }
            if (count > 0)
            {
               await Bot.Send(Methods.GetLocaleString(lang, "reported", repId), chatId);
            }
        }

        private static List<int> GetModId(long id)
        {
            var res = Bot.Api.GetChatAdministratorsAsync(id);
            return res.Result.Select(member => member.User.Id).ToList();
        }
    }
}
