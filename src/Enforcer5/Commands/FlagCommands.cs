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
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Enforcer5.Storage.PrincipalContent;
using Enforcer5.Models;
using Newtonsoft.Json;
using StackExchange.Redis;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;
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
            var blocked = ReportPolicyStore.IsReporterBlockedAsync(update.Message.Chat.Id,
                update.Message.From.Id).Result;
            if (blocked)
                return;
            var ReportOn = GroupSettingsStore.GetAsync(update.Message.Chat.Id, GroupSettingGroups.Settings, "Flagged").Result;
            if (ReportOn.Equals("yes"))
                return;
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (update.Message.ReplyToMessage != null)
            {
                if (update.Message.ReplyToMessage.From.Id == Bot.Me.Id)
                {
                    return;
                }
                if (!update.Message.ReplyToMessage.From.IsBot &&
                    Methods.IsGroupAdmin(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id))
                {
                    return;
                }
                if (!StorageRouting.GroupPostgresIsPrimary(update.Message.Chat.Id) && !Redis.db.HashSetAsync(
                        $"flaggedReply:{update.Message.Chat.Id}:{update.Message.ReplyToMessage.MessageId}",
                        "reported", "true", StackExchange.Redis.When.NotExists).Result)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "alreadyFlagged"), update.Message);
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
            var create = ReportStore.TryCreateAsync(new ReportCreate(update.Message.Chat.Id, repId,
                update.Message.From.Id, reporter, update.Message.ReplyToMessage?.From?.Id,
                update.Message.ReplyToMessage?.MessageId, DateTimeOffset.UtcNow)).Result;
            if (!create.Created)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "alreadyFlagged"), update.Message);
                return;
            }
            SendToAdmins(mods, update.Message.Chat.Id, msgId, reporter, isReply,
                update.Message.Chat.Title, update.Message, repId, username, lang, create.Report);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "admins", InGroupOnly = true)]
        public static void Admins(Update update, string[] args) => Admin(update, args);

        [Command(Trigger = "report", InGroupOnly = true)]
        public static void Report(Update update, string[] args) => Admin(update, args);

        [Command(Trigger = "adminoff", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AdminOff(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var userId = Methods.GetUserId(update, args);
            var chatId = update.Message.Chat.Id;
             ReportPolicyStore.SetAdminNotificationsDisabledAsync(chatId, userId, true).GetAwaiter().GetResult();
             Bot.SendReply(Methods.GetLocaleString(lang, "off"), update);
        }
        [Command(Trigger = "adminon", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AdminOn(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            var userId = Methods.GetUserId(update, args);
            var chatId = update.Message.Chat.Id;
             ReportPolicyStore.SetAdminNotificationsDisabledAsync(chatId, userId, false).GetAwaiter().GetResult();
             Bot.SendReply(Methods.GetLocaleString(lang, "on"), update);
        }

        [Command(Trigger = "solved", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Solved(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            var messageId = update.Message.ReplyToMessage?.MessageId;
            if (!messageId.HasValue && args.Length == 2 && int.TryParse(args[1], out var parsed))
                messageId = parsed;
            if (!messageId.HasValue)
            {
                Bot.Send(Methods.GetLocaleString(lang, args.Length == 2 ? "incorrectArgument" : "solvedNoReply"),
                    update.Message.Chat.Id);
                return;
            }
            var result = TrySolveReport(update.Message.Chat.Id, messageId.Value, update.Message.From.Id,
                DisplayName(update.Message.From), update.Message.Chat.Title, lang);
            if (result.Outcome == ReportSolveOutcome.NotFound)
                Bot.SendReply(Methods.GetLocaleString(lang, "reportNotFound"), update.Message);
            else if (result.Outcome == ReportSolveOutcome.AlreadySolved)
                Bot.Send(Methods.GetLocaleString(lang, "alreadySolved", result.Report?.SolvedAt,
                    result.Report?.SolvedByDisplay), update.Message.Chat.Id);
            else
            {
                Bot.Send(Methods.GetLocaleString(lang, "markSolved"), update.Message.Chat.Id);
                Service.LogCommand(update, update.Message.Text);
            }
        }

        [Command(Trigger = "reporton", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void ReportOn(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:reportblocked";
            var userId = Methods.GetUserId(update, args);
             ReportPolicyStore.SetReporterBlockedAsync(update.Message.Chat.Id, userId, false).GetAwaiter().GetResult();
             Bot.SendReply(Methods.GetLocaleString(lang, "userUnblocked"), update);
            Service.LogCommand(update, update.Message.Text);
        }
        [Command(Trigger = "reportoff", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void ReportOff(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;
            var hash = $"chat:{update.Message.Chat.Id}:reportblocked";
            var userId = Methods.GetUserId(update, args);
             ReportPolicyStore.SetReporterBlockedAsync(update.Message.Chat.Id, userId, true).GetAwaiter().GetResult();
             Bot.SendReply(Methods.GetLocaleString(lang, "userBlocked"), update);
            Service.LogCommand(update, update.Message.Text);
        }

        private static void SendToAdmins(List<ChatMember> mods, long chatId, int msgId, string reporter, bool isReply, string chatTitle, Message updateMessage, int repId, string username, XDocument groupLang, ReportRecord report)
        {
            var sendMessageIds = new List<int>();
            var modsSentTo = new List<long>();
            var count = 0;
            var groupLink = Task.FromResult((RedisValue)(Methods.ReadGroupContent(chatId,
                GroupContentTypes.Link) ?? string.Empty));
            foreach (var user in mods)
            {
                if (user != null)
                {
                    // Telegram forbids bots messaging other bots, so a bot promoted to admin
                    // can never receive a report - forwarding to it always throws "Forbidden".
                    if (user.User.IsBot)
                    {
                        continue;
                    }
                    var lang = Methods.GetGroupLanguage(user.User).Doc;
                    var mod = user.User.Id;
                    var allowed = ReportPolicyStore.AreAdminNotificationsDisabledAsync(chatId, mod);
                    if (allowed.Result)
                    {
                        continue;
                    }
                    try
                    {
                        // Scoped per-admin: falling back to repId on a failed forward must not
                        // leak into the next admin's attempt, or every admin after the first
                        // failure gets the wrong message id forwarded to them.
                        var currentMsgId = msgId;
                        var replyFailure = false;
                        try
                        {
                            var resulted = Bot.Api.ForwardMessage(mod, chatId, currentMsgId).Result;
                        }
                        catch (ApiRequestException e)
                        {
                            currentMsgId = repId;
                            replyFailure = true;
                            Console.WriteLine(e.Message + e.StackTrace);
                        }
                        catch (AggregateException e)
                        {
                            currentMsgId = repId;
                            replyFailure = true;
                            Console.WriteLine(e.Message + e.StackTrace);
                        }
                        catch (Exception e)
                        {
                            currentMsgId = repId;
                            replyFailure = true;
                            Console.WriteLine(e.Message + e.StackTrace);
                        }
                        if (replyFailure && updateMessage.ReplyToMessage != null)
                        {
                            // Can't forward (deleted, protected content, etc.) - the Update
                            // payload already has the flagged message's content in hand, so
                            // send that as text instead of leaving the admin with no context.
                            var originalContent = !string.IsNullOrEmpty(updateMessage.ReplyToMessage.Text)
                                ? updateMessage.ReplyToMessage.Text
                                : updateMessage.ReplyToMessage.Caption;
                            if (!string.IsNullOrEmpty(originalContent))
                            {
                                Bot.Send($"Could not forward the original message, here is the text:\n{originalContent.FormatHTML()}", mod);
                            }
                        }
                        Message result;
                        if (!string.IsNullOrEmpty(username))
                        {
                            if (updateMessage.ReplyToMessage != null && replyFailure == false)
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
                                            Url = Methods.GetChatMessageLink(updateMessage.Chat.Id, repId.ToString(), username: username)
                                        },
                                        new InlineButton(Methods.GetLocaleString(lang, "delete"),
                                            $"delflag:{updateMessage.Chat.Id}:{currentMsgId}")
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
                                            Url = Methods.GetChatMessageLink(updateMessage.Chat.Id, repId.ToString(), username: username)
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
                            if (updateMessage.ReplyToMessage != null && replyFailure == false)
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
                                        new InlineButton(Methods.GetLocaleString(lang, "delete"),$"delflag:{updateMessage.Chat.Id}:{currentMsgId}"),
                                        groupLink.Result.HasValue && !groupLink.Result.ToString().ToLower().Equals("no")
                                            ? new InlineButton(Methods.GetLocaleString(lang, "goToChat"))
                                            {
                                                Url = Methods.GetChatMessageLink(updateMessage.Chat.Id, repId.ToString(), chatLink: groupLink.Result.ToString())
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
                                                Url = Methods.GetChatMessageLink(updateMessage.Chat.Id, repId.ToString(), chatLink: groupLink.Result.ToString())
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
                            ReportStore.RecordDeliveryAsync(report, count, result.Chat.Id,
                                result.MessageId, chatId, currentMsgId).GetAwaiter().GetResult();
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
            ReportStore.SetDeliveryCountAsync(report, count).GetAwaiter().GetResult();
            if (count > 0)
            {
                 Bot.SendReply(Methods.GetLocaleString(groupLang, "reported", repId), chatId, repId);
            }
        }

        private static List<ChatMember> GetModId(long id)
        {
            var res = Bot.Api.GetChatAdministrators(id).Result;
            return res.ToList();
        }

        internal static ReportSolveResult TrySolveReport(long chatId, long reportMessageId,
            long solverUserId, string solvedBy, string chatTitle, XDocument lang)
        {
            var solvedAt = DateTimeOffset.UtcNow;
            var result = ReportStore.TrySolveAsync(chatId, reportMessageId, solverUserId,
                solvedBy, solvedAt).GetAwaiter().GetResult();
            if (result.Outcome != ReportSolveOutcome.Solved) return result;
            var text = Methods.GetLocaleString(lang, "solvedBy", $"{solvedBy} {solverUserId}",
                solvedAt.UtcDateTime.ToString("hh:mm:ss dd-MM-yyyy"), chatTitle);
            foreach (var delivery in ReportStore.ListDeliveriesAsync(result.Report).GetAwaiter().GetResult())
            {
                try
                {
                    var link = Methods.GetChatMessageLink(chatId, reportMessageId.ToString());
                    Bot.Api.EditMessageText(delivery.AdminChatId, delivery.AdminMessageId,
                        string.IsNullOrEmpty(link)
                            ? $"{text}\n{Methods.GetLocaleString(lang, "reportID", reportMessageId)}"
                            : $"{text}\n{Methods.GetLocaleString(lang, "reportID", reportMessageId)}\n<a href=\"{link}\">View Report</a>",
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Html);
                }
                catch (Exception e) { Console.WriteLine(e); }
            }
            return result;
        }

        internal static string DisplayName(User user) => user.Username == null
            ? user.FirstName : $"{user.FirstName} (@{user.Username})";
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "banflag", GroupAdminOnly = true)]
        public static void BanFlag(CallbackQuery call, string[] args)
        {
            var grouplang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userLang = Methods.GetGroupLanguage(call.Message, false).Doc;
            var chatId = long.Parse(args[1]);
            var userId = long.Parse(args[2]);
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
                var nick = PrincipalContentStore.GetUserAsync(userId).Result?.Name + $" ({userId})";
                var admin = PrincipalContentStore.GetUserAsync(call.From.Id).Result?.Name + $" ({call.From.Id})";
                Bot.Send(Methods.GetLocaleString(grouplang, "SuccesfulBan", nick, admin), chatId);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(userLang, "userBanned"));
            }
        }

        [Callback(Trigger = "kickflag", GroupAdminOnly = true)]
        public static void KickFlag(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message,true).Doc;
            var userlang = Methods.GetGroupLanguage(call.Message, false).Doc;
            var chatId = long.Parse(args[1]);
            var userId = long.Parse(args[2]);
             var res = Methods.KickUser(chatId, userId, lang);
            if (res)
            {
                Methods.SaveBan(userId, "kick");
                var nick = PrincipalContentStore.GetUserAsync(userId).Result?.Name + $" ({userId})";
                var admin = PrincipalContentStore.GetUserAsync(call.From.Id).Result?.Name + $" ({call.From.Id})";
                Bot.Send(Methods.GetLocaleString(lang, "SuccesfulKick", nick, admin), chatId);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(userlang, "userKicked"));
            }
        }

        [Callback(Trigger = "warnflag", GroupAdminOnly = true)]
        public static void WarnFlag(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var userId = long.Parse(args[2]);
            var callId = call.Id;
            var callFromId = call.From.Id;
            var nick = PrincipalContentStore.GetUserAsync(userId).Result?.Name + $" ({userId})";
            Commands.Warn(userId, chatId, targetnick: nick, callbackid: callId, callbackfromid: callFromId);
        }

        [Callback(Trigger = "solveflag", GroupAdminOnly = true)]
        public static void SolveFlag(CallbackQuery call, string[] args)
        {
            var chatid = long.Parse(args[1]);
            var msgid = long.Parse(args[2]);
            var lang = Methods.GetGroupLanguage(chatid).Doc;
            var result = Commands.TrySolveReport(chatid, msgid, call.From.Id,
                Commands.DisplayName(call.From), chatid.ToString(), lang);
            if (result.Outcome == ReportSolveOutcome.NotFound)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "reportNotFound"), true);
                return;
            }
            if (result.Outcome == ReportSolveOutcome.Solved)
            {
                Bot.Api.AnswerCallbackQuery(call.Id,
                    Methods.GetLocaleString(lang, "markedAsSolved", chatid, msgid));
                Bot.Send(Methods.GetLocaleString(lang, "markedAsSolved", call.From.FirstName, msgid), chatid);
            }
            else
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "alreadySolved",
                    result.Report?.SolvedAt, result.Report?.SolvedByDisplay), true);
            }
        }

        [Callback(Trigger = "delflag", GroupAdminOnly = true)]
        public static void DeleteFlag(CallbackQuery call, string[] args)
        {
            var chatid = long.Parse(args[1]);
            var msgid = int.Parse(args[2]);
            Bot.DeleteMessage(chatid, msgid);
            var lang = Methods.GetGroupLanguage(chatid).Doc;
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "messageDeleted"));
        }
    }
}
