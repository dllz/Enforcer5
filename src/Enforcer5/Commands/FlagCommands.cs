using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Newtonsoft.Json;
using StackExchange.Redis;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "admin", InGroupOnly = true)]
        public static async Task Admin(Update update, string[] args)
        {
            if (Methods.IsGroupAdmin(update))
            {
                return;
            }
            var blocked =
                Redis.db.SetContainsAsync($"chat:{update.Message.Chat.Id}:reportblocked", update.Message.From.Id).Result;
            if (blocked)
                return; 
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (update.Message.ReplyToMessage != null)
            {
                var alreadyFlagged = Redis.db.HashExistsAsync($"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}", "reported").Result;
                if (alreadyFlagged)
                {
                    await Bot.SendReply(Methods.GetLocaleString(lang, "alreadyFlagged"), update.Message);
                    return;
                }
                await Redis.db.HashSetAsync($"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}", "reported", "true");
                if (update.Message.ReplyToMessage.From.Id == Bot.Me.Id)
                {
                    return;
                }
                if (Methods.IsGroupAdmin(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id))
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
            await SendToAdmins(mods, update.Message.Chat.Id, msgId, reporter, isReply, update.Message.Chat.Title, update.Message, repId, username, lang);
        }

        [Command(Trigger = "adminoff", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task AdminOff(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            await Redis.db.SetAddAsync($"chat:{chatId}:adminOff", userId);
            await Bot.SendReply(Methods.GetLocaleString(lang, "Off"), update);
        }
        [Command(Trigger = "adminon", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task AdminOn(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var userId = update.Message.From.Id;
            var chatId = update.Message.Chat.Id;
            if (update.Message.ReplyToMessage != null)
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            await Redis.db.SetRemoveAsync($"chat:{chatId}:adminOff", userId);
            await Bot.SendReply(Methods.GetLocaleString(lang, "On"), update);
        }

        [Command(Trigger = "solved", InGroupOnly = true, GroupAdminOnly = true)]
        public static async Task Solved(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (update.Message.ReplyToMessage != null)
            {
                var msgid = update.Message.ReplyToMessage.MessageId;
                var chatid = update.Message.Chat.Id;
                var hash = $"flagged:{chatid}:{msgid}";
                var isReported = Redis.db.HashGetAsync(hash, "Solved").Result;
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
                        solvedBy = $"{solvedBy} (@{update.Message.From.Username})";
                    var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                    await Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                    await Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                    await Redis.db.HashSetAsync(hash, "Solved", 1);
                    var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin").Result);
                    var reporter = Redis.db.HashGetAsync(hash, "Reporter").Result;
                    string text;
                    if (!reporter.HasValue)
                    {
                        text = Methods.GetLocaleString(lang, "solvedByReporter", $"{solvedBy} {update.Message.From.Id}", solvedAt,
                            update.Message.Chat.Title, reporter);
                    }
                    else
                    {
                        text = Methods.GetLocaleString(lang, "solvedBy", $"{solvedBy} {update.Message.From.Id}", solvedAt,
                            update.Message.Chat.Title);
                    }
                    for (int i = 0; i < counter; i++)
                    {
                        var json = Redis.db.HashGetAsync(hash, $"messageObject{i}").Result;
                        AdminNotification noti = new AdminNotification();
                        MemoryStream stream1 = new MemoryStream(Encoding.UTF8.GetBytes(json));
                        DataContractJsonSerializer ser = new DataContractJsonSerializer(noti.GetType());
                        noti = ser.ReadObject(stream1) as AdminNotification;
                        await Bot.Api.EditMessageTextAsync(noti.adminChatId, noti.adminMsgId,
                            $"{text}\n{Methods.GetLocaleString(lang, "reportID", noti.chatMsgId)}");
                    }
                    await Bot.Send(Methods.GetLocaleString(lang, "markSolved"), chatid);
                }
                else if (isReported.TryParse(out isReport) && isReport == 1)
                {
                    var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt").Result;
                    var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy").Result;
                    await Bot.Send(Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), chatid);
                }
            }
            else if (args.Length == 2)
            {
                if (!string.IsNullOrEmpty(args[1]))
                {
                    int msgid;
                    if (int.TryParse(args[1], out msgid))
                    {
                        var chatid = update.Message.Chat.Id;
                        var hash = $"flagged:{chatid}:{msgid}";
                        var isReported = Redis.db.HashGetAsync(hash, "Solved").Result;
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
                                solvedBy = $"{solvedBy} (@{update.Message.From.Username})";
                            var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                            await Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                            await Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                            await Redis.db.HashSetAsync(hash, "Solved", 1);
                            var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin").Result);
                            var reporter = Redis.db.HashGetAsync(hash, "Reporter").Result;
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
                                try
                                {
                                    var json = Redis.db.HashGetAsync(hash, $"messageObject{i}").Result;
                                    AdminNotification noti = new AdminNotification();
                                    MemoryStream stream1 = new MemoryStream(Encoding.UTF8.GetBytes(json));
                                    DataContractJsonSerializer ser = new DataContractJsonSerializer(noti.GetType());
                                    noti = ser.ReadObject(stream1) as AdminNotification;
                                        await Bot.Api.EditMessageTextAsync(noti.adminChatId, noti.adminMsgId,
                                            $"{text}\n{Methods.GetLocaleString(lang, "reportID", noti.chatMsgId)}");
                                }
                                catch (ApiRequestException e)
                                {

                                }
                                catch (AggregateException e)
                                {

                                }
                                catch (Exception e)
                                {

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
            }
            else
                await Bot.Send(Methods.GetLocaleString(lang, "solvedNoReply"), update.Message.Chat.Id);
        }

        [Command(Trigger = "reporton", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static async Task ReportOn(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:reportblocked";
            await Redis.db.SetRemoveAsync(hash, update.Message.ReplyToMessage.From.Id);
            await Bot.SendReply(Methods.GetLocaleString(lang, "userUnblocked"), update);
        }
        [Command(Trigger = "reportoff", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static async Task ReportOff(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:reportblocked";
            await Redis.db.SetAddAsync(hash, update.Message.ReplyToMessage.From.Id);
            await Bot.SendReply(Methods.GetLocaleString(lang, "userBlocked"), update);
        }

        private static async Task SendToAdmins(List<int> mods, long chatId, int msgId, string reporter, bool isReply, string chatTitle, Message updateMessage, int repId, string username, XDocument lang)
        {
            var sendMessageIds = new List<int>();
            var modsSentTo = new List<long>();
            var count = 0;
            var groupLink = Redis.db.HashGetAsync($"chat:{chatId}links", "link");
            foreach (var mod in mods)
            {
                var allowed = Redis.db.SetContainsAsync($"chat:{chatId}:adminOff", mod);
                if (allowed.Result)
                {
                    continue;
                }
                try
                {
                    await Bot.Api.ForwardMessageAsync(mod, chatId, msgId);
                    Message result;
                    if (!string.IsNullOrEmpty(username))
                    {
                        if (updateMessage.ReplyToMessage != null)
                        {
                            var solvedMenu = new Menu(2)
                            {
                                Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "ban"),
                                        $"banflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "kick"),
                                        $"kickflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "Warn"),
                                        $"warnflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "markSolved"),
                                        $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "goToMessage"))
                                    {
                                        Url = $"http://t.me/{username}/{repId}"
                                    }
                                }
                            };
                            result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId),
                                mod,
                                false, Key.CreateMarkupFromMenu(solvedMenu)).Result;
                        }
                        else
                        {
                            var solvedMenu = new Menu(2)
                            {
                                Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "markSolved"),
                                        $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "goToMessage"))
                                    {
                                        Url = $"http://t.me/{username}/{repId}"
                                    }
                                }
                            };
                            result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId),
                                mod,
                                false, Key.CreateMarkupFromMenu(solvedMenu)).Result;
                        }
                    }
                    else
                    {
                        if (updateMessage.ReplyToMessage != null)
                        {
                            var solvedMenu = new Menu(2)
                            {
                                Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "ban"),
                                        $"banflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "kick"),
                                        $"kickflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "Warn"),
                                        $"warnflag:{updateMessage.Chat.Id}:{updateMessage.ReplyToMessage.From.Id}"),
                                    new InlineButton(Methods.GetLocaleString(lang, "markSolved"),
                                        $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                                    groupLink.Result.HasValue
                                        ? new InlineButton(Methods.GetLocaleString(lang, "goToChat"))
                                        {
                                            Url = groupLink.Result.ToString()
                                        }
                                        : null
                                }
                            };
                            result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId),
                                mod,
                                false, Key.CreateMarkupFromMenu(solvedMenu)).Result;
                        }
                        else
                        {
                            var solvedMenu = new Menu(2)
                            {
                                Buttons = new List<InlineButton>
                                {
                                    new InlineButton(Methods.GetLocaleString(lang, "markSolved"),
                                        $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                                    groupLink.Result.HasValue
                                        ? new InlineButton(Methods.GetLocaleString(lang, "goToChat"))
                                        {
                                            Url = groupLink.Result
                                        }
                                        : null
                                }
                            };
                            result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId),
                                mod,
                                false, Key.CreateMarkupFromMenu(solvedMenu)).Result;
                        }
                    }
                    if (result != null)
                    {
                        var nme = $"flagged:{chatId}:{repId}";
                        var noti = new AdminNotification();
                        noti.hash = nme;
                        noti.chatId = chatId;
                        noti.chatMsgId = msgId;
                        noti.adminChatId = result.Chat.Id;
                        noti.adminMsgId = result.MessageId;
                        MemoryStream stream1 = new MemoryStream();
                        DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AdminNotification));
                        ser.WriteObject(stream1, noti);
                        byte[] json = stream1.ToArray();
                        var text = Encoding.UTF8.GetString(json, 0, json.Length);
                        await Redis.db.HashSetAsync(nme, $"messageObject{count}", text);
                        count++;
                    }
                }
                catch (ApiRequestException e)
                {

                }
                catch (AggregateException e)
                {

                }
                catch (Exception e)
                {
                    
                }
            }
            var hash = $"flagged:{chatId}:{repId}";
            var time = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
            var alreadyReported = Redis.db.HashGetAsync(hash, "Solved");
            var ids = sendMessageIds.ToArray();
            var chats = modsSentTo.ToArray();
            for (int i = 0; i < modsSentTo.Count; i++)
            {
                await Redis.db.HashSetAsync(hash, $"Message{i}", ids[i]);
                await Redis.db.HashSetAsync(hash, $"adminID{i}", chats[i]);
            }
            if (alreadyReported.Result.IsNullOrEmpty)
            {
               await Redis.db.HashSetAsync(hash, "Solved", 0);
               await Redis.db.HashSetAsync(hash, "Created", time);
               await Redis.db.HashSetAsync(hash, "#Admin", count);
               await Redis.db.HashSetAsync(hash, "Reporter", reporter);
               await Redis.db.HashSetAsync(hash, "repID", repId);
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

    public static partial class CallBacks
    {
        [Callback(Trigger = "banflag", GroupAdminOnly = true)]
        public static async Task BanFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var chatId = long.Parse(args[1]);
            var userId = int.Parse(args[2]);
            var res = Methods.BanUser(chatId, userId, lang);
            var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{chatId}:tempbanned", userId).Result;
            if (isAlreadyTempbanned)
            {
                var all = Redis.db.HashGetAllAsync("tempbanned").Result;
                foreach (var mem in all)
                {
                    if ($"{chatId}:{userId}".Equals(mem.Value))
                    {
                        await Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                    }
                }
                await Redis.db.SetRemoveAsync($"chat:{chatId}:tempbanned", userId);
            }
            Methods.SaveBan(userId, "ban");
            var why = Methods.GetLocaleString(lang, "inlineBan");
            Methods.AddBanList(chatId, userId, userId.ToString(), why);
            await Redis.db.HashDeleteAsync($"{call.Message.Chat.Id}:userJoin", userId);
            await Bot.Send(Methods.GetLocaleString(lang, "SuccesfulBan", userId, call.From.Id), chatId);
            await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "userBanned"));
        }

        [Callback(Trigger = "kickflag", GroupAdminOnly = true)]
        public static async Task KickFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var chatId = long.Parse(args[1]);
            var userId = int.Parse(args[2]);
            await Methods.KickUser(chatId, userId, lang);
            Methods.SaveBan(userId, "kick");

            await Bot.Send(Methods.GetLocaleString(lang, "SuccesfulKick", userId, call.From.Id), chatId);
            await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "userKicked"));
        }

        [Callback(Trigger = "warnflag", GroupAdminOnly = true)]
        public static async Task WarnFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var chatId = long.Parse(args[1]);
            var userId = int.Parse(args[2]);
            var num = Redis.db.HashIncrementAsync($"chat:{chatId}:warns", userId, 1).Result;
            var max = 3;
            int.TryParse(Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result, out max);
            if (num >= max)
            {
                var type = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result.HasValue
                    ? Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").ToString()
                    : "kick";
                if (type.Equals("ban"))
                {
                    try
                    {
                        await Bot.Api.KickChatMemberAsync(chatId, userId);
                        var name = Methods.GetNick(call.Message, args);
                        await Bot.SendReply(Methods.GetLocaleString(lang, "warnMaxBan", name), call.Message);
                    }
                    catch (AggregateException e)
                    {
                        Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", call.Message, lang);
                    }
                }
                else
                {
                    await Methods.KickUser(chatId, userId, lang);
                    var name = Methods.GetNick(call.Message, args);
                    await Bot.SendReply(Methods.GetLocaleString(lang, "warnMaxKick", name), call.Message);
                }
                await Redis.db.HashSetAsync($"chat:{chatId}:warns", userId, 0);
        }
            else
            {
                var diff = max - num;
                var text = Methods.GetLocaleString(lang, "warnFlag", userId, call.From.Id, num, max);               
                await Bot.Send(text, chatId);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, text, true);
            }
        }

        [Callback(Trigger = "solveflag", GroupAdminOnly = true)]
        public static async Task SolveFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var chatid = long.Parse(args[1]);
            var msgid = int.Parse(args[2]);
            var hash = $"flagged:{chatid}:{msgid}";
            var isReported = Redis.db.HashGetAsync(hash, "Solved").Result;
            if (!isReported.HasValue)
            {
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "reportNotFound"), true);
                return;
            }
            int isReport;
            if (isReported.TryParse(out isReport) && isReport == 0)
            {
                var solvedBy = call.From.FirstName;
                if (call.From.Username != null)
                    solvedBy = $"{solvedBy} (@{call.From.Username})";
                var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                await Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                await Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                await Redis.db.HashSetAsync(hash, "Solved", 1);
                var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin").Result);
                var reporter = Redis.db.HashGetAsync(hash, "Reporter").Result;
                var repID = Redis.db.HashGetAsync(hash, "repID").Result;
                string text;
                if (!reporter.HasValue)
                {
                    text = Methods.GetLocaleString(lang, "solvedByReporter", $"{solvedBy} {call.From.Id}", solvedAt,
                        chatid, reporter);
                }
                else
                {
                    text = Methods.GetLocaleString(lang, "solvedBy", $"{solvedBy} {call.From.Id}", solvedAt,
                        chatid);
                }
                for (int i = 0; i < counter; i++)
                {
                    var json = Redis.db.HashGetAsync(hash, $"messageObject{i}").Result;
                    AdminNotification noti = new AdminNotification();
                    MemoryStream stream1 = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(noti.GetType());
                    noti = ser.ReadObject(stream1) as AdminNotification;
                    try
                    {
                        await Bot.Api.EditMessageTextAsync(noti.adminChatId, noti.adminMsgId,
                        $"{text}\n{Methods.GetLocaleString(lang, "reportID", noti.chatMsgId)}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
                await Bot.Api.AnswerCallbackQueryAsync(call.Id,
                    Methods.GetLocaleString(lang, "markedAsSolved", chatid, repID));
                await Bot.Send(Methods.GetLocaleString(lang, "markedAsSolved", call.From.FirstName, repID), chatid);
            }
            else if (isReported.TryParse(out isReport) && isReport == 1)
            {
                var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt").Result;
                var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy").Result;
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), true);
            }
        }
    }
}
