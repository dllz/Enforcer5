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
#pragma warning disable CS4014
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "admin", InGroupOnly = true)]
        public static void Admin(Update update, string[] args)
        {
            if (Methods.IsGroupAdmin(update))
            {
                return;
            }
            var blocked =
                Redis.db.SetContainsAsync($"chat:{update.Message.Chat.Id}:reportblocked", update.Message.From.Id).Result;
            if (blocked)
                return;
            var ReportOn = Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}:settings", "Flagged").Result;
            if (ReportOn.Equals("yes"))
                return;
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (update.Message.ReplyToMessage != null)
            {
                var alreadyFlagged = Redis.db.HashExistsAsync($"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}", "reported").Result;
                if (alreadyFlagged)
                {
                     Bot.SendReply(Methods.GetLocaleString(lang, "alreadyFlagged"), update.Message);
                    return;
                }
                 Redis.db.HashSetAsync($"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}", "reported", "true");
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
             SendToAdmins(mods, update.Message.Chat.Id, msgId, reporter, isReply, update.Message.Chat.Title, update.Message, repId, username, lang);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "adminoff", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AdminOff(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var userId = Methods.GetUserId(update, args);
            var chatId = update.Message.Chat.Id;
             Redis.db.SetAddAsync($"chat:{chatId}:adminOff", userId);
             Bot.SendReply(Methods.GetLocaleString(lang, "off"), update);
        }
        [Command(Trigger = "adminon", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AdminOn(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            var userId = Methods.GetUserId(update, args);
            var chatId = update.Message.Chat.Id;
             Redis.db.SetRemoveAsync($"chat:{chatId}:adminOff", userId);
             Bot.SendReply(Methods.GetLocaleString(lang, "on"), update);
        }

        [Command(Trigger = "solved", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Solved(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            if (update.Message.ReplyToMessage != null)
            {
                var msgid = update.Message.ReplyToMessage.MessageId;
                var chatid = update.Message.Chat.Id;
                var hash = $"flagged:{chatid}:{msgid}";
                var isReported = Redis.db.HashGetAsync(hash, "Solved").Result;
                if (!isReported.HasValue)
                {
                     Bot.SendReply(Methods.GetLocaleString(lang, "reportNotFound"), update.Message);
                    return;
                }
                int isReport;
                if (isReported.TryParse(out isReport) && isReport == 0)
                {
                    var solvedBy = update.Message.From.FirstName;
                    if (update.Message.From.Username != null)
                        solvedBy = $"{solvedBy} (@{update.Message.From.Username})";
                    var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                     Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                     Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                     Redis.db.HashSetAsync(hash, "Solved", 1);
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
                         Bot.Api.EditMessageTextAsync(noti.adminChatId, noti.adminMsgId,
                            $"{text}\n{Methods.GetLocaleString(lang, "reportID", noti.reportId)}");
                    }
                     Bot.Send(Methods.GetLocaleString(lang, "markSolved"), chatid);
                    Service.LogCommand(update, update.Message.Text);
                }
                else if (isReported.TryParse(out isReport) && isReport == 1)
                {
                    var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt").Result;
                    var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy").Result;
                     Bot.Send(Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), chatid);
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
                             Bot.SendReply(Methods.GetLocaleString(lang, "reportNotFound"), update.Message);
                            return;
                        }
                        int isReport;
                        if (isReported.TryParse(out isReport) && isReport == 0)
                        {
                            var solvedBy = update.Message.From.FirstName;
                            if (update.Message.From.Username != null)
                                solvedBy = $"{solvedBy} (@{update.Message.From.Username})";
                            var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                             Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                             Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                             Redis.db.HashSetAsync(hash, "Solved", 1);
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
                                         Bot.Api.EditMessageTextAsync(noti.adminChatId, noti.adminMsgId,
                                            $"{text}\n{Methods.GetLocaleString(lang, "reportID", noti.reportId)}");
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
                             Bot.Send(Methods.GetLocaleString(lang, "markSolved"), chatid);
                            Service.LogCommand(update, update.Message.Text);
                        }
                        else if (isReported.TryParse(out isReport) && isReport == 1)
                        {
                            var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt");
                            var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy");
                             Bot.Send(Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), chatid);
                        }
                    }
                    else
                    {
                         Bot.Send(Methods.GetLocaleString(lang, "incorrectArgument"), update.Message.Chat.Id);
                    }
                }
            }
            else
                 Bot.Send(Methods.GetLocaleString(lang, "solvedNoReply"), update.Message.Chat.Id);
        }

        [Command(Trigger = "reporton", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void ReportOn(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:reportblocked";
            var userId = Methods.GetUserId(update, args);
             Redis.db.SetRemoveAsync(hash, userId);
             Bot.SendReply(Methods.GetLocaleString(lang, "userUnblocked"), update);
            Service.LogCommand(update, update.Message.Text);
        }
        [Command(Trigger = "reportoff", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void ReportOff(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:reportblocked";
            var userId = Methods.GetUserId(update, args);
             Redis.db.SetAddAsync(hash, userId);
             Bot.SendReply(Methods.GetLocaleString(lang, "userBlocked"), update);
            Service.LogCommand(update, update.Message.Text);
        }

        private static void SendToAdmins(List<ChatMember> mods, long chatId, int msgId, string reporter, bool isReply, string chatTitle, Message updateMessage, int repId, string username, XDocument groupLang)
        {
            var sendMessageIds = new List<int>();
            var modsSentTo = new List<long>();
            var count = 0;
            var groupLink = Redis.db.HashGetAsync($"chat:{chatId}links", "link");
            foreach (var user in mods)
            {
                if (user != null)
                {
                    var lang = Methods.GetGroupLanguage(user.User).Doc;
                    var mod = user.User.Id;
                    var allowed = Redis.db.SetContainsAsync($"chat:{chatId}:adminOff", mod);
                    if (allowed.Result)
                    {
                        continue;
                    }
                    try
                    {
                        var resulted = Bot.Api.ForwardMessageAsync(mod, chatId, msgId).Result;
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
                                        },
                                        new InlineButton(Methods.GetLocaleString(lang, "delete"),
                                            $"delflag:{updateMessage.Chat.Id}:{msgId}")
                                    }
                                };
                                result = Bot.Send(Methods.GetLocaleString(lang, "reportAdminReply", reporter, chatTitle, repId, updateMessage.Text),
                                    mod,
                                    Key.CreateMarkupFromMenu(solvedMenu));
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
                                    mod
                                    , Key.CreateMarkupFromMenu(solvedMenu));
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
                                        new InlineButton(Methods.GetLocaleString(lang, "delete"),$"delflag:{updateMessage.Chat.Id}:{msgId}"),
                                        groupLink.Result.HasValue && !groupLink.Result.ToString().ToLower().Equals("no")
                                            ? new InlineButton(Methods.GetLocaleString(lang, "goToChat"))
                                            {
                                                Url = groupLink.Result.ToString()
                                            }
                                            : null
                                    }

                                };
                                result = Bot.Send(Methods.GetLocaleString(lang, "reportAdminReply", reporter, chatTitle, repId, updateMessage.Text),
                                    mod,
                                    Key.CreateMarkupFromMenu(solvedMenu));
                            }
                            else
                            {
                                var solvedMenu = new Menu(2)
                                {
                                    Buttons = new List<InlineButton>
                                    {
                                        new InlineButton(Methods.GetLocaleString(lang, "markSolved"),
                                            $"solveflag:{updateMessage.Chat.Id}:{repId}"),
                                        groupLink.Result.HasValue && !groupLink.Result.ToString().ToLower().Equals("no")
                                            ? new InlineButton(Methods.GetLocaleString(lang, "goToChat"))
                                            {
                                                Url = groupLink.Result
                                            }
                                            : null
                                    }
                                };
                                result = Bot.Send(Methods.GetLocaleString(lang, "reportAdmin", reporter, chatTitle, repId),
                                    mod,
                                    Key.CreateMarkupFromMenu(solvedMenu));
                            }
                        }
                        if (result != null)
                        {
                            var nme = $"flagged:{chatId}:{repId}";
                            var noti = new AdminNotification();
                            noti.hash = nme;
                            noti.chatId = chatId;
                            noti.chatMsgId = msgId;
                            noti.reportId = repId;
                            noti.adminChatId = result.Chat.Id;
                            noti.adminMsgId = result.MessageId;
                            MemoryStream stream1 = new MemoryStream();
                            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(AdminNotification));
                            ser.WriteObject(stream1, noti);
                            byte[] json = stream1.ToArray();
                            var text = Encoding.UTF8.GetString(json, 0, json.Length);
                            Redis.db.HashSetAsync(nme, $"messageObject{count}", text);
                            count++;
                        }
                    }
                    catch (ApiRequestException e)
                    {
                        Console.WriteLine(e.Message + e.StackTrace);
                    }
                    catch (AggregateException e)
                    {
                        Console.WriteLine(e.Message + e.StackTrace);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message + e.StackTrace);
                    }
                }
                
            }
            var hash = $"flagged:{chatId}:{repId}";
            var time = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
            var alreadyReported = Redis.db.HashGetAsync(hash, "Solved");
            var ids = sendMessageIds.ToArray();
            var chats = modsSentTo.ToArray();
            for (int i = 0; i < modsSentTo.Count; i++)
            {
                 Redis.db.HashSetAsync(hash, $"Message{i}", ids[i]);
                 Redis.db.HashSetAsync(hash, $"adminID{i}", chats[i]);
            }
            if (alreadyReported.Result.IsNullOrEmpty)
            {
                Redis.db.HashSetAsync(hash, "Solved", 0);
                Redis.db.HashSetAsync(hash, "Created", time);
                Redis.db.HashSetAsync(hash, "#Admin", count);
                Redis.db.HashSetAsync(hash, "Reporter", reporter);
                Redis.db.HashSetAsync(hash, "repID", repId);
            }
            if (count > 0)
            {
                 Bot.Send(Methods.GetLocaleString(groupLang, "reported", repId), chatId);
            }
        }

        private static List<ChatMember> GetModId(long id)
        {
            var res = Bot.Api.GetChatAdministratorsAsync(id).Result;
            return res.ToList();
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "banflag", GroupAdminOnly = true)]
        public static void BanFlag(CallbackQuery call, string[] args)
        {
            var grouplang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userLang = Methods.GetGroupLanguage(call.Message, false).Doc;
            var chatId = long.Parse(args[1]);
            var userId = int.Parse(args[2]);
            var res = Methods.BanUser(chatId, userId, grouplang);
            var isAlreadyTempbanned = Redis.db.SetContainsAsync($"chat:{chatId}:tempbanned", userId).Result;
            if (isAlreadyTempbanned)
            {
                var all = Redis.db.HashGetAllAsync("tempbanned").Result;
                foreach (var mem in all)
                {
                    if ($"{chatId}:{userId}".Equals(mem.Value))
                    {
                         Redis.db.HashDeleteAsync("tempbanned", mem.Name);
                    }
                }
                 Redis.db.SetRemoveAsync($"chat:{chatId}:tempbanned", userId);
            }
            if (res)
            {
                Methods.SaveBan(userId, "ban");
                var why = Methods.GetLocaleString(grouplang, "inlineBan");
                Methods.AddBanList(chatId, userId, userId.ToString(), why);
                Redis.db.HashDeleteAsync($"{call.Message.Chat.Id}:userJoin", userId);
                Bot.Send(Methods.GetLocaleString(grouplang, "SuccesfulBan", userId, call.From.Id), chatId);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(userLang, "userBanned"));
            }
        }

        [Callback(Trigger = "kickflag", GroupAdminOnly = true)]
        public static void KickFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userlang = Methods.GetGroupLanguage(call.Message, false).Doc;
            var chatId = long.Parse(args[1]);
            var userId = int.Parse(args[2]);
             var res = Methods.KickUser(chatId, userId, lang);
            if (res)
            {
                Methods.SaveBan(userId, "kick");

                Bot.Send(Methods.GetLocaleString(lang, "SuccesfulKick", userId, call.From.Id), chatId);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(userlang, "userKicked"));
            }
        }

        [Callback(Trigger = "warnflag", GroupAdminOnly = true)]
        public static void WarnFlag(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var userId = int.Parse(args[2]);
            var callId = call.Id;
            var callFromId = call.From.Id;
            var nick = Redis.db.HashGetAsync($"user:{userId}", "name").Result + $" ({userId})";
            Commands.Warn(userId, chatId, targetnick: nick, callbackid: callId, callbackfromid: callFromId);
        }

        [Callback(Trigger = "solveflag", GroupAdminOnly = true)]
        public static void SolveFlag(CallbackQuery call, string[] args)
        {            
            var chatid = long.Parse(args[1]);
            var msgid = int.Parse(args[2]);
            var lang = Methods.GetGroupLanguage(chatid).Doc;
            var hash = $"flagged:{chatid}:{msgid}";
            var isReported = Redis.db.HashGetAsync(hash, "Solved").Result;
            if (!isReported.HasValue)
            {
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "reportNotFound"), true);
                return;
            }
            int isReport;
            if (isReported.TryParse(out isReport) && isReport == 0)
            {
                var solvedBy = call.From.FirstName;
                if (call.From.Username != null)
                    solvedBy = $"{solvedBy} (@{call.From.Username})";
                var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                 Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                 Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                 Redis.db.HashSetAsync(hash, "Solved", 1);
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
                         Bot.Api.EditMessageTextAsync(noti.adminChatId, noti.adminMsgId,
                        $"{text}\n{Methods.GetLocaleString(lang, "reportID", noti.reportId)}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
                 Bot.Api.AnswerCallbackQueryAsync(call.Id,
                    Methods.GetLocaleString(lang, "markedAsSolved", chatid, repID));
                 Bot.Send(Methods.GetLocaleString(lang, "markedAsSolved", call.From.FirstName, repID), chatid);
            }
            else if (isReported.TryParse(out isReport) && isReport == 1)
            {
                var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt").Result;
                var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy").Result;
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), true);
            }
        }

        [Callback(Trigger = "delflag", GroupAdminOnly = true)]
        public static void DeleteFlag(CallbackQuery call, string[] args)
        {
            var chatid = long.Parse(args[1]);
            var msgid = int.Parse(args[2]);
            Bot.DeleteMessage(chatid, msgid);
            var lang = Methods.GetGroupLanguage(chatid).Doc;
            Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "messageDeleted"));
        }
    }
}
