using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Actions;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Handlers;
using Telegram.Bot.Types;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Net.Http;
using Newtonsoft.Json;
using Telegram.Bot;

#pragma warning disable CS4014
#pragma warning disable CS0168
namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "kickme", InGroupOnly = true)]
        public static void Kickme(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true);
            // Leaving by choice, not moderation: kept out of the sanction history.
            var res = Methods.KickUser(update.Message.Chat.Id, update.Message.From.Id, lang.Doc, record: false);
            if (res)
            {
                return;
            }
        }

        [Command(Trigger = "kick", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Kick(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true);
                try
                {
                    try
                    {
                        var userid = Methods.GetUserId(update, args);
                        if (userid == Bot.Me.Id || userid == update.Message.From.Id)
                            return;
                        // As /ban: what the admin typed, or the message the kick replied to.
                        var reason = args.Length > 1 && !string.IsNullOrEmpty(args[1]) ? args[1] : update.Message.ReplyToMessage?.Text;
                        // The group hears it from the kick's own message (KickNotices), edited once they can rejoin.
                        Methods.KickUser(update.Message.Chat.Id, userid, lang.Doc, reason, update.Message.From.Id,
                            stat: "kick");
                    }
                    catch (Exception e)
                    {
                        // An unknown @username is the admin's typo, not a bug.
                        if (e.Message != "UnableToResolveUsername")
                            ErrorReporter.Report(e, "Kicking a user");
                        Methods.SendError(e.Message, update.Message, lang.Doc);
                    }
                }
                catch (AggregateException e)
                {
                    ErrorReporter.Report(e, "Kicking a user");
                    Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang.Doc);
                }
            
        }

       [Command(Trigger = "warn", InGroupOnly = true, GroupAdminOnly = true)]
        public static void  Warn(Update update, string[] args)
        {
           if (update.Message.ReplyToMessage != null)
           {
                Warn(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id, update, targetnick:Methods.GetNick(update.Message, args, update.Message.From.Id), actorId: update.Message.From.Id);
            }
           else 
           {
               try
               {
                   var warnedid = Methods.GetUserId(update, args);
                   var chatid = update.Message.Chat.Id;
                   
                   Warn(warnedid, chatid, update, targetnick: Ids.Named(warnedid, Redis.db.HashGetAsync($"user:{warnedid}", "name").Result), actorId: update.Message.From.Id);
               }
               catch (Exception e)
               {
                   // An unknown @username is the admin's typo, not a bug.
                   if (e.Message != "UnableToResolveUsername")
                       ErrorReporter.Report(e, "Warning a user");
                   var lang = Methods.GetGroupLanguage(update.Message,true);
                   Methods.SendError(e.Message, update.Message, lang.Doc);
               }
           }
        }

        [Command(Trigger = "prewarn", InGroupOnly = true, GroupAdminOnly = true)]
        public static void PreWarn(Update update, string[] args)
        {
            if (update.Message.ReplyToMessage != null)
            {
                Warn(update.Message.ReplyToMessage.From.Id, update.Message.Chat.Id, update, targetnick: Methods.GetNick(update.Message, args, update.Message.From.Id), preWarn: true, actorId: update.Message.From.Id);
            }
            else
            {
                try
                {
                    var warnedid = Methods.GetUserId(update, args);
                    var chatid = update.Message.Chat.Id;

                    Warn(warnedid, chatid, update, targetnick: Ids.Named(warnedid, Redis.db.HashGetAsync($"user:{warnedid}", "name").Result), preWarn: true, actorId: update.Message.From.Id);
                }
                catch (Exception e)
                {
                    // An unknown @username is the admin's typo, not a bug.
                    if (e.Message != "UnableToResolveUsername")
                        ErrorReporter.Report(e, "Prewarning a user");
                    var lang = Methods.GetGroupLanguage(update.Message, true);
                    Methods.SendError(e.Message, update.Message, lang.Doc);
                }
            }
        }

        /// <summary>
        /// Warns (or prewarns) the member through the action seam and tells the group. <paramref name="actorId"/>
        /// is the admin who asked, null when Enforcer warns on its own (flood, message or name length; pass
        /// <paramref name="source"/>); <paramref name="reason"/> goes to the warn history; <paramref name="report"/>
        /// is the report it was given from. Answered as a reply to <paramref name="update"/>, or in a popup for
        /// <paramref name="callbackid"/> plus a message.
        /// </summary>
        internal static ActionResult Warn(long warnedId, long chatId, Update update = null, string[] args = null, string targetnick = null, string callbackid = "", long callbackfromid = 0, bool preWarn = false,
            long? actorId = null, string reason = null, ReportRef report = null, ActionSource? source = null, string logLine = null)
        {
            var result = ActionSeam.Run(new ActionRequest
            {
                Kind = preWarn ? ActionKind.Prewarn : ActionKind.Warn,
                ChatId = chatId,
                TargetId = warnedId,
                ActorId = actorId,
                Source = source ?? ActionSources.Derive(ErrorReporter.Current?.Update, actorId, report != null),
                Reason = reason,
                Report = report,
                Log = ActionLogEntry.Bot(logLine)
            });
            if (result.Outcome == ActionOutcome.Blocked && !string.IsNullOrEmpty(result.BlockMessage))
            {
                // An action hook said no, in its own words.
                if (update != null) Bot.SendReply(result.BlockMessage, update.Message);
                else Bot.Send(result.BlockMessage, chatId);
            }
            if (!result.Succeeded) return result; // an admin, or the bot: never warned, nothing said
            var lang = Methods.GetGroupLanguage(chatId);
            if (preWarn)
            {
                var text = Methods.GetLocaleString(lang.Doc, "prewarn", targetnick, result.Count);
                var solvedMenu = new Menu(2)
                {
                    Buttons = new List<InlineButton>
                    {
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "resetPreWarn"),
                            $"resetPrewarns:{chatId}:{warnedId}"),
                        new InlineButton(Methods.GetLocaleString(lang.Doc, "removePreWarn"),
                            $"removePrewarn:{chatId}:{warnedId}"),
                    }
                };
                Bot.SendReply(text, update, Key.CreateMarkupFromMenu(solvedMenu));
                return result;
            }
            var preText = result.Converted > 0
                ? Methods.GetLocaleString(lang.Doc, "preWarnConverted", targetnick, result.Converted) + "\n"
                : "";
            if (result.Escalation != null)
            {
                var limitText = WarnLimitText(lang.Doc, result, targetnick);
                if (update != null)
                {
                    Bot.SendReply(preText + limitText, update.Message);
                }
                else if (!string.IsNullOrEmpty(callbackid))
                {
                    Bot.Api.AnswerCallbackQuery(callbackid, preText + limitText, true);
                    Bot.Send(limitText, chatId);
                }
                else
                {
                    Bot.Send(preText + limitText, chatId);
                }
                return result;
            }
            {
                var text = Methods.GetLocaleString(lang.Doc, "warn", targetnick, result.Count, result.Max);
                var solvedMenu = WarnButtons(lang.Doc, chatId, warnedId);
                if (update != null)
                {
                    Bot.SendReply(preText + text, update, Key.CreateMarkupFromMenu(solvedMenu));
                }
                else if (!string.IsNullOrEmpty(callbackid) && !string.IsNullOrEmpty(targetnick))
                {
                    var nick = Redis.db.HashGetAsync($"user:{callbackfromid}", "name").Result;
                    text = Methods.GetLocaleString(lang.Doc, "warnFlag", targetnick, Ids.Named(callbackfromid, nick), result.Count, result.Max);
                    Bot.Api.AnswerCallbackQuery(callbackid, preText + text, true);
                    Bot.Send(text, chatId);
                }
                else if (!string.IsNullOrEmpty(callbackid))
                {
                    text = Methods.GetLocaleString(lang.Doc, "warnFlag", Ids.Mark(warnedId), Ids.Mark(callbackfromid), result.Count, result.Max);
                    Bot.Api.AnswerCallbackQuery(callbackid, preText + text, true);
                    Bot.Send(text, chatId);
                }
                else
                {
                    Bot.Send(preText + text, chatId, customMenu: Key.CreateMarkupFromMenu(solvedMenu));
                }
            }
            return result;
        }

        /// <summary>The Reset and Remove buttons under a warn.</summary>
        internal static Menu WarnButtons(System.Xml.Linq.XDocument lang, long chatId, long userId) => new Menu(2)
        {
            Buttons = new List<InlineButton>
            {
                new InlineButton(Methods.GetLocaleString(lang, "resetWarn"), $"resetwarns:{chatId}:{userId}"),
                new InlineButton(Methods.GetLocaleString(lang, "removeWarn"), $"removewarn:{chatId}:{userId}"),
            }
        };

        /// <summary>
        /// What a warn reaching the limit says: banned or kicked, or, when that did not happen, why, and that the
        /// warns stay (then also in the log channel).
        /// </summary>
        internal static string WarnLimitText(System.Xml.Linq.XDocument lang, ActionResult warn, object name)
        {
            var escalation = warn.Escalation;
            var kick = escalation.Applied.Kind == ActionKind.Kick;
            if (escalation.Succeeded)
                return Methods.GetLocaleString(lang, kick ? "warnMaxKick" : "warnMaxBan", name);
            // An action hook that blocked it says why in its own words; without words, as any refusal.
            var why = escalation.Outcome == ActionOutcome.Blocked && !string.IsNullOrEmpty(escalation.BlockMessage)
                ? escalation.BlockMessage
                : Methods.GetLocaleString(lang, escalation.Refusal switch
                {
                    Enforcement.Refusal.BotNotAdmin => "warnLimitBotNotAdmin",
                    Enforcement.Refusal.TargetIsAdmin => "cannotbanadmin",
                    _ => "warnLimitRefused"
                });
            var text = Methods.GetLocaleString(lang, kick ? "warnMaxKickNotDone" : "warnMaxBanNotDone", name, warn.Count, warn.Max, why);
            Service.LogBotAction(warn.Applied.ChatId, text, warn.Applied.TargetId);
            return text;
        }

        [Command(Trigger = "ban", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Ban(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message,true);
            try
            {
                try
                {
                    // A post made as a channel carries a shared placeholder sender, so the user
                    // path below cannot handle it: ban the channel instead.
                    switch (ChannelPosts.FromModerationCommand(update.Message, args.Length > 1 ? args[1] : null,
                                update.Message.Chat.Id, out var channelId, out var channelTitle))
                    {
                        case ChannelTargetKind.Group:
                            Bot.SendReply(Methods.GetLocaleString(lang.Doc, "cannotbanadmin"), update);
                            return;
                        case ChannelTargetKind.Channel:
                            BanChannel(update, channelId, channelTitle, lang.Doc);
                            return;
                    }

                    var userid = Methods.GetUserId(update, args);
                    if (userid == Bot.Me.Id || userid == update.Message.From.Id)
                        return;

                    var isChatMember = Bot.Api.GetChatMember(update.Message.Chat.Id, userid).Result;

                    // What the admin typed, or the message the ban replied to: in the ban history and,
                    // for /status, in the ban list.
                    var banReason = !string.IsNullOrEmpty(args[1]) ? args[1] : update.Message.ReplyToMessage?.Text;
                    object[] arguments =
                    {
                        Methods.GetNick(update.Message, args, userid),
                        Methods.GetNick(update.Message, args, true)
                    };
                    var res = Methods.BanUser(update.Message.Chat.Id, userid, lang.Doc, banReason,
                        update.Message.From.Id,
                        // Counted in their ban record only when they were in the group, not for a ban of someone gone.
                        stat: isChatMember.Status == ChatMemberStatus.Member ? "ban" : null,
                        banListReason: banReason ?? "", banListName: Ids.Strip(arguments[0].ToString()));
                    if (res)
                    {
                        if (userid == 321720895 | userid == 9375804)
                        {
                            Redis.db.SetAdd("bot:lookaround",
                                $"{userid}:\n{update.Message.Chat.Id} {update.Message.Chat.Title} {update.Message.From.Id} {update.Message.From.FirstName}");
                        }
                        try
                        {
                            // Service messages (joins, title changes) cannot be forwarded, and a
                            // ban by id or @username has no message to forward.
                            if (update.Message.ReplyToMessage != null && !update.Message.ReplyToMessage.IsServiceMessage)
                            {
                                Bot.Api.ForwardMessage(update.Message.From.Id, update.Message.Chat.Id,
                                    update.Message.ReplyToMessage.MessageId, disableNotification: true);
                            }
                        }
                        catch (Exception e)
                        {
                            // Best effort. 403: the admin has not started the bot or blocked it;
                            // 400: the message cannot be forwarded (protected content, deleted).
                            var api = Bot.AsApiError(e);
                            if (api?.ErrorCode != 403 && api?.ErrorCode != 400)
                                ErrorReporter.Report(e, "Forwarding the banned message to the admin");
                        }
                         Bot.SendReply(Methods.GetLocaleString(lang.Doc, "SuccesfulBan", arguments), update.Message);
                    }
                }
                catch (Exception e)
                {
                    // An unknown @username is the admin's typo, not a bug.
                    if (e.Message != "UnableToResolveUsername")
                        ErrorReporter.Report(e, "Banning a user");
                    Methods.SendError(e.Message, update.Message, lang.Doc);
                }
            }
                                    
            catch (AggregateException e)
            {
                ErrorReporter.Report(e, "Banning a user");
                Methods.SendError($"{e.InnerExceptions[0]}\n{e.StackTrace}", update.Message, lang.Doc);
            }
        }

        [Command(Trigger = "unban", GroupAdminOnly = true)]
        public static void UnBan(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message,true).Doc;

            switch (ChannelPosts.FromModerationCommand(update.Message, args.Length > 1 ? args[1] : null,
                        chatId, out var channelId, out var channelTitle))
            {
                case ChannelTargetKind.Group:
                    return; // anonymous admins are never banned
                case ChannelTargetKind.Channel:
                    UnbanChannel(update, channelId, channelTitle, lang);
                    return;
            }

            // Every step used to fail silently: an unknown username or a failed lookup threw on the
            // pool, and a user who was not banned got no answer at all.
            long userId;
            ChatMemberStatus status;
            try
            {
                userId = Methods.GetUserId(update, args);
                status = Bot.Api.GetChatMember(chatId, userId).GetAwaiter().GetResult().Status;
            }
            catch (Exception e)
            {
                // An unknown @username is the admin's typo, not a bug.
                if (e.Message != "UnableToResolveUsername")
                    ErrorReporter.Report(e, $"Looking up the user to unban in {chatId}");
                Methods.SendError(Bot.AsApiError(e)?.Message ?? e.Message, update.Message, lang);
                return;
            }

            if (status != ChatMemberStatus.Kicked)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "userNotBanned", Methods.GetNick(update.Message, args, userId)), update);
                return;
            }

            // The unban also ends any tempban in the schedule (ActionEffects).
            var res = Methods.UnbanUser(chatId, userId, lang, update.Message.From.Id);
            if (res)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "userUnbanned"), update);
            }
        }

        /// <summary>
        /// Tempbans for <paramref name="time"/> minutes. <paramref name="reason"/> and
        /// <paramref name="actorId"/> (the admin, null when automatic) go to the ban history.
        /// </summary>
        internal static bool Tempban(long userId, long chatId, double time,
            string nick = null, Update update = null, string message = null, string reason = null,
            long? actorId = null, ActionSource? source = null, string logLine = null)
        {           
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            // Pure UTC: it is handed to Telegram as untilDate. The action schedules its end and forgets any
            // earlier tempban (ActionEffects).
            var until = Clock.UtcNow.AddSeconds(time * 60);
            if (!Methods.TempBanUser(chatId, userId, until, lang, reason, actorId, source, stat: "tempban", logLine: logLine)) return false;
            var timeText = TimeSpan.FromMinutes(time).ToString(@"dd\:hh\:mm");
            message ??= Methods.GetLocaleString(lang, "tempbanned", timeText, nick, Ids.Mark(userId));
            if (update != null) Bot.SendReply(message, update);
            else Bot.Send(message, chatId);
            return true;
        }

        [Command(Trigger = "tempban", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Tempban(Update update, string[] args)
        {

            var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
            long userId;
            if (update.Message.ReplyToMessage != null) // by reply: the whole argument is the length
            {
                userId = update.Message.ReplyToMessage.From.Id;
            }
            else if (!string.IsNullOrWhiteSpace(args[1])) // an id or @username first, then the length
            {
                var user = args[1].Trim().Split(' ')[0];
                if (user.StartsWith("@")) userId = Methods.ResolveIdFromusername(user);
                else if (!long.TryParse(user, out userId))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "incorrectArgument"), update);
                    return;
                }
            }
            else return;
            if (userId == Bot.Me.Id) return;

            // The length as /tempmute reads it (Models/Duration): 30, 2 h, 2hours, 1d; a word it does not know, or a
            // length outside 1 minute to 365 days, is refused. Its own reading took anything but min/hour(s)/day(s)
            // as minutes, so "/tempban 2 h" banned for 2 minutes (issue #31).
            double calculatedTime;
            switch (Duration.TryParse(Duration.AfterTarget(update.Message, args.Length > 1 ? args[1] : null), out var duration))
            {
                case DurationParseResult.Ok:
                    calculatedTime = duration.TotalMinutes;
                    break;
                case DurationParseResult.Empty:
                    calculatedTime = Methods.GetGroupTempbanTime(update.Message.Chat.Id);
                    break;
                default:
                    Bot.SendReply(Methods.GetLocaleString(lang, "invalidDuration"), update);
                    return;
            }
            if (userId != 0)
            {
                Tempban(userId, update.Message.Chat.Id, calculatedTime, Methods.GetNick(update.Message, args, userId),
                    actorId: update.Message.From.Id);
            }
        }

        [Command(Trigger = "delmsg", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void DeleteMessageInGroup(Update update, string[] args)
        {
            // Told when refused, logged when done: both by the action.
            Methods.DeleteForAdmin(update.Message.ReplyToMessage, update.Message.From.Id, ActionSource.Command);
        }

        /// <summary>
        /// Who a mute command is about, or 0 when there is nobody to act on. A post made as a
        /// channel, or by an anonymous admin, carries a placeholder sender that must not be muted
        /// in its place, so those get an explanation instead. Like /ban, the bot and the admin
        /// themselves are skipped silently: without a reply or an argument, GetUserId returns the
        /// admin.
        /// </summary>
        private static long MuteTarget(Update update, string[] args, System.Xml.Linq.XDocument lang)
        {
            switch (ChannelPosts.FromModerationCommand(update.Message, args.Length > 1 ? args[1] : null,
                        update.Message.Chat.Id, out _, out _))
            {
                case ChannelTargetKind.Group:
                    Bot.SendReply(Methods.GetLocaleString(lang, "cannotmuteadmin"), update);
                    return 0;
                case ChannelTargetKind.Channel:
                    Bot.SendReply(Methods.GetLocaleString(lang, "cannotMuteChannel"), update);
                    return 0;
            }

            var userId = Methods.GetUserId(update, args);
            if (userId == Bot.Me.Id || userId == update.Message.From.Id) return 0;
            return userId;
        }

        [Command(Trigger = "mute", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Mute(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var userId = MuteTarget(update, args, lang);
                if (userId == 0) return;

                // On failure MuteUser has already told the chat why.
                // As /ban: what the admin typed, or the message the mute replied to.
                var reason = args.Length > 1 && !string.IsNullOrEmpty(args[1]) ? args[1] : update.Message.ReplyToMessage?.Text;
                if (!Methods.MuteUser(chatId, userId, lang, reason: reason, actorId: update.Message.From.Id)) return;

                object[] arguments =
                {
                    Methods.GetNick(update.Message, args, userId),
                    Methods.GetNick(update.Message, args, true)
                };
                Bot.SendReply(Methods.GetLocaleString(lang, "muted", arguments), update);
            }
            catch (Exception e)
            {
                // An unknown @username is the admin's typo, not a bug.
                if (e.Message != "UnableToResolveUsername")
                    ErrorReporter.Report(e, "Muting a user");
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "tempmute", GroupAdminOnly = true, InGroupOnly = true)]
        public static void TempMute(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var userId = MuteTarget(update, args, lang);
                if (userId == 0) return;

                TimeSpan duration;
                switch (Duration.TryParse(Duration.AfterTarget(update.Message, args.Length > 1 ? args[1] : null), out duration))
                {
                    case DurationParseResult.Ok:
                        break;
                    case DurationParseResult.Empty:
                        duration = TimeSpan.FromMinutes(Methods.GetGroupTempMuteTime(chatId));
                        break;
                    default:
                        Bot.SendReply(Methods.GetLocaleString(lang, "invalidDuration"), update);
                        return;
                }

                // Pure UTC: Telegram reads untilDate as UTC.
                var until = DateTime.UtcNow.Add(duration);
                // The text after the target is the duration, so the reason is the replied-to message.
                if (!Methods.TempMuteUser(chatId, userId, until, lang, update.Message.ReplyToMessage?.Text,
                        update.Message.From.Id)) return;

                Bot.SendReply(Methods.GetLocaleString(lang, "temmpmuted", Duration.ToDisplay(duration),
                    Methods.GetNick(update.Message, args, userId), userId), update);
            }
            catch (Exception e)
            {
                // An unknown @username is the admin's typo, not a bug.
                if (e.Message != "UnableToResolveUsername")
                    ErrorReporter.Report(e, "Temporarily muting a user");
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "unmute", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Unmute(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var userId = MuteTarget(update, args, lang);
                if (userId == 0) return;

                if (!Methods.UnmuteUser(chatId, userId, lang, actorId: update.Message.From.Id)) return;

                object[] arguments =
                {
                    Methods.GetNick(update.Message, args, userId),
                    Methods.GetNick(update.Message, args, true)
                };
                Bot.SendReply(Methods.GetLocaleString(lang, "unmuted", arguments), update);
            }
            catch (Exception e)
            {
                // An unknown @username is the admin's typo, not a bug.
                if (e.Message != "UnableToResolveUsername")
                    ErrorReporter.Report(e, "Unmuting a user");
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "mutelist", GroupAdminOnly = true, InGroupOnly = true)]
        public static void MuteList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var chatId = update.Message.Chat.Id;
                var muted = Repositories.Mutes.GetMutedAsync(chatId).GetAwaiter().GetResult();
                var now = DateTime.UtcNow;
                var tempMuted = Repositories.Mutes.GetTempMutedAsync(chatId, now).GetAwaiter().GetResult();

                var mutedLines = StillMuted(chatId, muted.Select(id => (id, "")));
                var tempLines = StillMuted(chatId, tempMuted
                    .OrderBy(t => t.UntilUtc)
                    .Select(t => (t.UserId, $" - {Duration.ToDisplay(t.UntilUtc - now)}")));

                if (mutedLines.Count == 0 && tempLines.Count == 0)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "noMutedUsers"), update);
                    return;
                }
                Bot.SendReply(Methods.GetLocaleString(lang, "muteList",
                    mutedLines.Count == 0 ? "-" : string.Join("\n", mutedLines),
                    tempLines.Count == 0 ? "-" : string.Join("\n", tempLines)), update);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Listing muted users");
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        [Command(Trigger = "tempbanlist", GroupAdminOnly = true, InGroupOnly = true)]
        public static void TempbanList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            try
            {
                var now = DateTime.UtcNow;
                var tempbans = Repositories.Tempbans.GetActiveAsync(update.Message.Chat.Id, now).GetAwaiter().GetResult();
                if (tempbans.Count == 0)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "noTempbans"), update);
                    return;
                }
                // Tempbanned users are not in the chat, so names come from the bot's own records.
                var lines = tempbans.Select(t =>
                    $"{DescribeUser(t.UserId, null)} - {Duration.ToDisplay(t.UntilUtc - now)}");
                Bot.SendReply(Methods.GetLocaleString(lang, "tempbanList", string.Join("\n", lines)), update);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Listing tempbanned users");
                Methods.SendError(e.Message, update.Message, lang);
            }
        }

        /// <summary>
        /// One line per user that Telegram still shows as muted, each followed by its suffix. Users
        /// it shows as free (unmuted by hand in Telegram, banned, or gone) are dropped from the
        /// bot's records. If Telegram cannot be asked, the user is listed and kept.
        /// </summary>
        internal static List<string> StillMuted(long chatId, IEnumerable<(long UserId, string Suffix)> users)
        {
            var lines = new List<string>();
            foreach (var (userId, suffix) in users)
            {
                User user = null;
                try
                {
                    var member = Bot.Api.GetChatMember(chatId, userId).Result;
                    user = member.User;
                    if (!(member is ChatMemberRestricted restricted) || restricted.CanSendMessages)
                    {
                        Repositories.Mutes.ClearAsync(chatId, userId).GetAwaiter().GetResult();
                        continue;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Checking the mute of {userId} in {chatId}");
                }
                lines.Add(DescribeUser(userId, user) + suffix);
            }
            return lines;
        }

        /// <summary>
        /// "Name @username (id)", the name a mention and the id copyable, unescaped (a language argument). Falls back
        /// to the stored name, then the id alone.
        /// </summary>
        internal static string DescribeUser(long userId, User user)
        {
            string name = user != null ? user.FirstName : Redis.db.HashGetAsync($"user:{userId}", "name").Result;
            if (string.IsNullOrWhiteSpace(name)) return Ids.Mark(userId);
            var username = string.IsNullOrEmpty(user?.Username) ? "" : $" @{user.Username}";
            return $"{Ids.Mention(userId, name)}{username} ({Ids.Mark(userId)})";
        }

    }


    public static partial class CallBacks
    {
        // The buttons under a /warn message. Reset has always cleared media warns as well.
        [Callback(Trigger = "resetwarns", GroupAdminOnly = true)]
        public static void ResetWarns(CallbackQuery call, string[] args) =>
            WarnRemovals.FromButton(call, args, WarnKind.Warn, reset: true, alsoMediaWarns: true);

        [Callback(Trigger = "removewarn", GroupAdminOnly = true)]
        public static void RemoveWarn(CallbackQuery call, string[] args) =>
            WarnRemovals.FromButton(call, args, WarnKind.Warn, reset: false);

        // The buttons under a /prewarn message and in the /user menu.
        [Callback(Trigger = "resetPrewarns", GroupAdminOnly = true)]
        public static void ResetPreWarns(CallbackQuery call, string[] args) =>
            WarnRemovals.FromButton(call, args, WarnKind.Prewarn, reset: true);

        [Callback(Trigger = "removePrewarn", GroupAdminOnly = true)]
        public static void RemovePreWarn(CallbackQuery call, string[] args) =>
            WarnRemovals.FromButton(call, args, WarnKind.Prewarn, reset: false);
    }
}
