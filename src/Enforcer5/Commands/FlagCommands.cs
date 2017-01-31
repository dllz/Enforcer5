﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
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
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            if (update.Message.ReplyToMessage != null)
            {
                var alreadyFlagged = Redis.db.HashExistsAsync($"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}", "reported").Result;
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
            SendToAdmins(mods, update.Message.Chat.Id, msgId, reporter, isReply, update.Message.Chat.Title, update.Message, repId, username, lang);
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
                        solvedBy = $"{solvedBy} (@{update.Message.From.Username}";
                    var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                    await Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                    await Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                    await Redis.db.HashSetAsync(hash, "Solved", 1);
                    var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin").Result);
                    var reporter = Redis.db.HashGetAsync(hash, "Reporter").Result;
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
                        var id = Redis.db.HashGetAsync(hash, $"adminID{i}").Result;
                        var msgID = Redis.db.HashGetAsync(hash, $"Message{i}").Result;
                        if (id.HasValue && msgID.HasValue)
                        {
                            try
                            {
                                await Bot.Api.EditMessageTextAsync(id, msgid,
                                $"{text}\n{Methods.GetLocaleString(lang, "reportID", repID)}");
                            }
                            catch (ApiRequestException e)
                            {
                                
                            }
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
                                solvedBy = $"{solvedBy} (@{update.Message.From.Username}";
                            var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                            await Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                            await Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                            await Redis.db.HashSetAsync(hash, "Solved", 1);
                            var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin").Result);
                            var reporter = Redis.db.HashGetAsync(hash, "Reporter").Result;
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
                                try
                                {
                                    var id = Redis.db.HashGetAsync(hash, $"adminID{i}").Result;
                                    var msgID = Redis.db.HashGetAsync(hash, $"Message{i}").Result;
                                    if (id.HasValue && msgID.HasValue)
                                    {
                                        await Bot.Api.EditMessageTextAsync(id, msgid,
                                            $"{text}\n{Methods.GetLocaleString(lang, "reportID", repID)}");
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

        private static async Task SendToAdmins(List<int> mods, long chatId, int msgId, string reporter, bool isReply, string chatTitle, Message updateMessage, int repId, string username, XDocument lang)
        {
            var sendMessageIds = new List<int>();
            var modsSentTo = new List<long>();
            var count = 0;
            var groupLink = Redis.db.HashGetAsync($"chat:{chatId}links", "link");
            foreach (var mod in mods)
            {
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
                        var nme = $"flagged:{chatId}:{msgId}";
                        await Redis.db.HashSetAsync(nme, $"Message{count}", result.MessageId);
                        await Redis.db.HashSetAsync(nme, $"adminID{count}", result.Chat.Id);
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
            var hash = $"flagged:{chatId}:{msgId}";
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
            var chatId = int.Parse(args[1]);
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
            await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "userBanned"));
        }

        [Callback(Trigger = "kickflag", GroupAdminOnly = true)]
        public static async Task KickFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var chatId = int.Parse(args[1]);
            var userId = int.Parse(args[2]);
            await Methods.KickUser(chatId, userId, lang);
            Methods.SaveBan(userId, "kick");

            object[] arguments =
            {
                            Methods.GetNick(call.Message, args),
                            Methods.GetNick(call.Message, args, true)
                        };
            await Bot.SendReply(Methods.GetLocaleString(lang, "SuccesfulKick", arguments), call.Message);
            await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "userKicked"));
        }

        [Callback(Trigger = "warnflag", GroupAdminOnly = true)]
        public static async Task WarnFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message).Doc;
            var chatId = int.Parse(args[1]);
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
            var chatid = int.Parse(args[1]);
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
                    solvedBy = $"{solvedBy} (@{call.From.Username}";
                var solvedAt = System.DateTime.UtcNow.ToString("hh:mm:ss dd-MM-yyyy");
                await Redis.db.HashSetAsync(hash, "SolvedAt", solvedAt);
                await Redis.db.HashSetAsync(hash, "solvedBy", solvedBy);
                await Redis.db.HashSetAsync(hash, "Solved", 1);
                var counter = int.Parse(Redis.db.HashGetAsync(hash, "#Admin").Result);
                var reporter = Redis.db.HashGetAsync(hash, "Reporter").Result;
                var repID = Redis.db.HashGetAsync(hash, "repID");
                string text;
                if (!reporter.HasValue)
                {
                    text = Methods.GetLocaleString(lang, "solvedByReporter", solvedBy, solvedAt,
                        chatid, reporter);
                }
                else
                {
                    text = Methods.GetLocaleString(lang, "solvedBy", solvedBy, solvedAt,
                        chatid);
                }
                for (int i = 0; i < counter; i++)
                {
                    var id = Redis.db.HashGetAsync(hash, $"adminID{i}").Result;
                    var msgID = Redis.db.HashGetAsync(hash, $"Message{i}").Result;
                    if (id.HasValue && msgID.HasValue)
                    {
                        await Bot.Api.EditMessageTextAsync(id, msgid,
                            $"{text}\n{Methods.GetLocaleString(lang, "reportID", repID)}");
                    }
                }
                await Bot.Send(Methods.GetLocaleString(lang, "markedAsSolved",chatid, repID), chatid);
            }
            else if (isReported.TryParse(out isReport) && isReport == 1)
            {
                var solvedTime = Redis.db.HashGetAsync(hash, "SolvedAt");
                var solvedBy = Redis.db.HashGetAsync(hash, "solvedBy");
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "alreadySolved", solvedTime, solvedBy), true);
            }
        }
    }
}
