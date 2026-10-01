using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
#pragma warning disable CS4014
#pragma warning disable CS0168
namespace Enforcer5
{
    /// <summary>
    /// Per-message checks. Every handler reads its settings from the <see cref="MessageContext"/>
    /// built once per update, so the decision path touches neither Redis nor Telegram and runs
    /// inline on an update consumer.
    ///
    /// Enforcement (kick/ban/warn/tempban, and the replies that go with it) is still synchronous
    /// and can block for a minute or more behind a Telegram 429, so it goes through
    /// Bot.DispatchAction. Awaiting it here would let one rate-limited chat park every consumer,
    /// fill the update queue, and stop the poll loop for every other chat.
    /// </summary>
    public static class OnMessage
    {
        internal static async Task AntiFlood(MessageContext ctx)
        {
            var update = ctx.Update;
            // Measured from receipt, not from now: queue time must not count here.
            var time = (ctx.ReceivedAt - update.Message.Date);
            if (time.TotalSeconds > 7)
            {
                return;
            }
            if (ctx.Watched)
            {
                return;
            }

            var chatId = ctx.ChatId;
            AntiLength(ctx);

            if (ctx.Setting("Flood").Equals("yes"))
            {
                return;
            }

            var msgType = Methods.GetContentType(update.Message);
            var lang = ctx.Lang;
            if (isIgnored(ctx, msgType))
            {
                return;
            }

            var msgs = ctx.SpamCount;
            int num = msgs.HasValue ? int.Parse(msgs.ToString()) : 0;
            if (num == 0) num = 1;
            var maxTime = TimeSpan.FromSeconds(6);
            Redis.db.StringSetAsync($"spam:{chatId}:{ctx.UserId}", num + 1, maxTime);

            int maxmsgs;
            if (int.TryParse(ctx.FloodSetting("MaxFlood").ToString(), out maxmsgs) && num == maxmsgs + 1)
            {
                var action = ctx.FloodSetting("ActionFlood").ToString();
                var name = update.Message.From.FirstName;
                if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                var userid = ctx.UserId;
                var groupId = chatId;

                Bot.DispatchAction(() =>
                {
                    try
                    {
                        switch (action)
                        {
                            case "kick":
                                var res = Methods.KickUser(chatId, userid, lang);
                                if (res)
                                {
                                    Methods.SaveBan(userid, "flood");
                                    Bot.Send(Methods.GetLocaleString(lang, "kickedForFlood", $"{name}, {userid}"), update);
                                }
                                break;
                            case "ban":
                                res = Methods.BanUser(chatId, userid, lang);
                                if (res)
                                {
                                    Methods.SaveBan(userid, "flood");
                                    Methods.AddBanList(chatId, userid, update.Message.From.FirstName,
                                        Methods.GetLocaleString(lang, "bannedForFlood", ".."));
                                    Bot.Send(Methods.GetLocaleString(lang, "bannedForFlood", name), update);
                                }
                                break;
                            case "warn":
                                Commands.Warn(userid, groupId, update, targetnick: userid.ToString());
                                Methods.SaveBan(userid, "flood");
                                break;
                            case "tempban":
                                var time2 = Methods.GetGroupTempbanTime(groupId);
                                var timeBanned = TimeSpan.FromMinutes(time2);
                                string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                                var message = Methods.GetLocaleString(lang, "tempbannedForFlood",
                                    $"{userid}", timeText);
                                if (Commands.Tempban(userid, groupId, time2, userid.ToString(), message: message))
                                {
                                    Methods.SaveBan(userid, "flood");
                                }
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                    }
                }, "AntiFlood");
            }
        }

        private static void AntiTextLenght(MessageContext ctx)
        {
            var update = ctx.Update;
            var groupId = ctx.ChatId;
            var settings = ctx.TextLength;
            if (!MessageContext.Field(settings, "enabled").Equals("yes")) return;

            var text = update.Message.Text;
            int intml;
            int intmline;
            MessageContext.Field(settings, "maxlength").TryParse(out intml);
            MessageContext.Field(settings, "maxlines").TryParse(out intmline);
            var lines = Regex.Split(text, "(.+?)(?:\r\n|\n)");
            if (text.Length < intml && lines.Length < intmline) return;

            var action = MessageContext.Field(settings, "action");
            var lang = ctx.Lang;
            var userid = ctx.UserId;
            Bot.DispatchAction(() =>
            {
                try
                {
                    Bot.DeleteMessage(groupId, update.Message.MessageId);
                }
                catch (Exception e)
                {
                    //moving on
                }

                string reply;
                switch (action)
                {
                    case "kick":
                        Methods.KickUser(groupId, userid, lang);
                        reply = Methods.GetLocaleString(lang, "kickformesslength", userid);
                        Service.LogBotAction(groupId, reply, userid);
                        Bot.SendReply(reply, update);
                        return;
                    case "ban":
                        var res = Methods.BanUser(groupId, userid, lang);
                        if (res)
                        {
                            Methods.SaveBan(userid, "longmessages");
                            reply = Methods.GetLocaleString(lang, "banformesslength", userid);
                            Service.LogBotAction(groupId, reply, userid);
                            Bot.SendReply(reply, update);
                            return;
                        }
                        break;
                    case "Warn":
                        Commands.Warn(userid, groupId, update, targetnick: userid.ToString());
                        return;
                    case "tempban":
                        var time = Methods.GetGroupTempbanTime(groupId);
                        var timeBanned = TimeSpan.FromMinutes(time);
                        string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                        var message = Methods.GetLocaleString(lang, "tempbanformesslength",
                            $"{userid}", timeText);
                        Service.LogBotAction(groupId, message, userid);
                        Commands.Tempban(userid, groupId, time, userid.ToString(), message: message);
                        break;
                    case "default":
                        Bot.SendReply(Methods.GetLocaleString(lang, "actionNotSettext"), update);
                        break;
                }
            }, "AntiTextLenght");
        }

        private static void AntiNameLength(MessageContext ctx)
        {
            var update = ctx.Update;
            var groupId = ctx.ChatId;
            var settings = ctx.NameLength;
            if (!MessageContext.Field(settings, "enabled").Equals("yes")) return;

            var text = update.Message.From.FirstName;
            if (update.Message.From.LastName != null)
                text = $"{text}{update.Message.From.LastName}";
            int intml = 40;
            MessageContext.Field(settings, "maxlength").TryParse(out intml);
            if (text.Length < intml) return;

            var action = MessageContext.Field(settings, "action");
            var lang = ctx.Lang;
            var userid = ctx.UserId;
            Bot.DispatchAction(() =>
            {
                string reply;
                switch (action)
                {
                    case "kick":
                        Methods.KickUser(groupId, userid, lang);
                        reply = Methods.GetLocaleString(lang, "kickfornamelength", userid);
                        Service.LogBotAction(groupId, reply, userid);
                        Bot.SendReply(reply, update);
                        break;
                    case "ban":
                        var res = Methods.BanUser(groupId, userid, lang);
                        if (res)
                        {
                            Methods.SaveBan(userid, "namelength");
                            reply = Methods.GetLocaleString(lang, "banfornamelength", userid);
                            Service.LogBotAction(groupId, reply, userid);
                            Bot.SendReply(reply, update);
                        }
                        break;
                    case "Warn":
                        Commands.Warn(userid, groupId, update, targetnick: userid.ToString());
                        break;
                    case "tempban":
                        var time = Methods.GetGroupTempbanTime(groupId);
                        var timeBanned = TimeSpan.FromMinutes(time);
                        string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                        var message = Methods.GetLocaleString(lang, "tempbanfornamelength",
                            $"{userid}", timeText);
                        Service.LogBotAction(groupId, message, userid);
                        Commands.Tempban(userid, groupId, time, userid.ToString(), message: message);
                        break;
                    case "default":
                        Bot.SendReply(Methods.GetLocaleString(lang, "actionNotSetname"), update);
                        break;
                }
            }, "AntiNameLength");
        }

        /// <summary>
        /// Decision only - every read comes from the context and the enforcement inside each branch
        /// dispatches itself. Runs inline on the consumer because it does no I/O.
        /// </summary>
        internal static void AntiLength(MessageContext ctx)
        {
            try
            {
                AntiNameLength(ctx);
            }
            catch (Exception e)
            {
                LogHelper.Error($"AntiNameLength in {ctx.ChatId} failed: {e.Message}\n{e.StackTrace}");
            }
            try
            {
                if (ctx.Message.Type == MessageType.Text)
                    AntiTextLenght(ctx);
            }
            catch (Exception e)
            {
                LogHelper.Error($"AntiTextLenght in {ctx.ChatId} failed: {e.Message}\n{e.StackTrace}");
            }
        }

        internal static Task CheckMedia(MessageContext ctx)
        {
            if (ctx.Watched) return Task.CompletedTask;

            var message = ctx.Message;
            var chatId = ctx.ChatId;
            var media = Methods.GetContentType(message);
            if (!ctx.MediaSetting(media).Equals("blocked")) return Task.CompletedTask;

            var lang = ctx.Lang;
            var name = $"{message.From.FirstName} [{message.From.Id}]";
            if (message.From.Username != null)
                name = $"{name} (@{message.From.Username})";
            var configuredAction = ctx.MediaSetting("action").ToString();

            // The warn counter is incremented and reset in the same unit of work as the action it
            // gates. Doing the increment out here and dispatching only the action would let a
            // dropped action reset someone's strike count for free.
            Bot.DispatchAction(() =>
            {
                // Only needed once media is actually blocked, so it stays off the common path.
                // This read used to be issued twice - once for HasValue and once for the value.
                var configuredMax = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "mediamax").Result;
                var max = configuredMax.HasValue ? configuredMax : 2;
                var current = Redis.db.HashIncrementAsync($"chat:{chatId}:mediawarn", message.From.Id, 1).Result;

                var overLimit = current >= int.Parse(max);
                var action = overLimit ? configuredAction : null;
                if (overLimit)
                {
                    Redis.db.HashDeleteAsync($"chat:{chatId}:mediawarn", message.From.Id);
                }

                if (overLimit)
                {
                    string reply;
                    switch (action)
                    {
                        case "kick":
                            Methods.KickUser(chatId, message.From.Id, lang);
                            reply = Methods.GetLocaleString(lang, "kickedformedia", $"{name}");
                            Service.LogBotAction(chatId, reply, message.From.Id);
                            Bot.SendReply(reply, message);
                            break;
                        case "ban":
                            var res = Methods.BanUser(chatId, message.From.Id, lang);
                            if (res)
                            {
                                Methods.SaveBan(message.From.Id, "media");
                                reply = Methods.GetLocaleString(lang, "bannedformedia", name);
                                Service.LogBotAction(chatId, reply, message.From.Id);
                                Methods.AddBanList(chatId, message.From.Id, message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedformedia", ""));
                                Bot.SendReply(reply, message);
                            }
                            break;
                        case "tempban":
                            var time = Methods.GetGroupTempbanTime(chatId);
                            var timeBanned = TimeSpan.FromMinutes(time);
                            string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                            var messageText = Methods.GetLocaleString(lang, "tempbannedformedia",
                                $"{name}, {message.From.Id}", timeText);
                            Service.LogBotAction(chatId, messageText, message.From.Id);
                            Commands.Tempban(message.From.Id, chatId, time, message.From.Id.ToString(), message: messageText);
                            break;
                    }
                }
                else
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "mediaNotAllowed", current, max), message);
                }

                Bot.DeleteMessage(chatId, message.MessageId);
            }, "CheckMedia");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deletes a message sent via an inline bot on the chat's blocklist. Returns true when the
        /// message matched, so the caller skips the other content filters and command dispatch for
        /// a message that is going away.
        ///
        /// Exempt, and returning false so the message is processed as usual: the watch list, and
        /// automatic forwards from the group's linked channel (deleting those breaks the channel's
        /// comment thread). Admins are exempt too, but finding that out can mean a Telegram call,
        /// so it happens in the dispatched action; their message is then kept, and it has still
        /// skipped the other filters.
        /// </summary>
        internal static bool BlockedInlineBot(MessageContext ctx)
        {
            var message = ctx.Message;
            var viaBot = message.ViaBot;
            if (viaBot == null || ctx.InlineBotBlocks.Count == 0) return false;
            if (ctx.Watched || message.IsAutomaticForward) return false;
            if (InlineBotBlock.FirstMatch(ctx.InlineBotBlocks, viaBot.Username) == null) return false;

            var chatId = ctx.ChatId;
            // An anonymous admin posts as the group itself. Any other sender chat is a channel a
            // member is posting as, which is never an admin, so it needs no lookup either.
            var senderChat = message.SenderChat;
            if (senderChat != null && senderChat.Id == chatId) return true;
            var checkAdmin = senderChat == null;

            var userId = ctx.UserId;
            var messageId = message.MessageId;
            Bot.DispatchAction(() =>
            {
                if (checkAdmin && Methods.IsGroupAdmin(userId, chatId)) return;
                // Throws without the delete right; DispatchAction logs it. Nothing is sent to the
                // chat: one notice per blocked message would double the noise during a raid.
                Bot.DeleteMessage(chatId, messageId);
            }, "BlockedInlineBot");
            return true;
        }

        /// <summary>
        /// Posts made as a channel, when the chat has turned that off. The member behind the channel
        /// is invisible to bots, so the message is deleted and the channel is banned from the chat;
        /// if the ban fails, the delete still stands. Nothing is posted in the chat; the log channel
        /// records it.
        ///
        /// Decides from the context, so it runs inline. The Telegram calls are dispatched, including
        /// the linked-channel lookup when the short-lived cache is empty. If that lookup fails, the
        /// message is deleted but the channel is not banned, in case it was the linked channel.
        /// </summary>
        internal static ChannelPostDecision ChannelPost(MessageContext ctx)
        {
            var message = ctx.Message;
            var decision = ChannelPosts.Decide(message, ctx.ChatId, ctx.ChannelPosts);
            if (decision != ChannelPostDecision.Remove && decision != ChannelPostDecision.RemoveUnlessLinked)
                return decision;

            var chatId = ctx.ChatId;
            var channel = message.SenderChat;
            var messageId = message.MessageId;
            var lang = ctx.Lang;
            var checkLinked = decision == ChannelPostDecision.RemoveUnlessLinked;

            Bot.DispatchAction(() =>
            {
                var mayBan = true;
                if (checkLinked)
                {
                    try
                    {
                        var linked = Bot.Api.GetChat(chatId).GetAwaiter().GetResult().LinkedChatId ?? 0;
                        Repositories.ChannelPosts.SetLinkedChannelAsync(chatId, linked).GetAwaiter().GetResult();
                        if (linked == channel.Id) return;
                    }
                    catch (Exception e)
                    {
                        LogHelper.Error($"Linked channel lookup for {chatId} failed, deleting without a ban: {Bot.AsApiError(e)?.Message ?? e.Message}");
                        mayBan = false;
                    }
                }

                var deleted = TryTelegram(() => Bot.DeleteMessage(chatId, messageId), $"Deleting a channel post in {chatId}");
                var banned = mayBan && TryTelegram(() => Bot.Api.BanChatSenderChat(chatId, channel.Id).Wait(),
                    $"Banning channel {channel.Id} in {chatId}");
                if (!deleted && !banned) return;

                Service.LogBotAction(chatId, Methods.GetLocaleString(lang,
                    banned ? "channelPostBanned" : "channelPostDeleted", ChannelPosts.Describe(channel)));
            }, "ChannelPost");
            return decision;
        }

        private static bool TryTelegram(Action call, string what)
        {
            try
            {
                call();
                return true;
            }
            catch (Exception e)
            {
                LogHelper.Error($"{what} failed: {Bot.AsApiError(e)?.Message ?? e.Message}");
                return false;
            }
        }

        internal static async Task RightToLeft(MessageContext ctx)
        {
            if (ctx.Watched) return;

            var update = ctx.Update;
            var chatId = ctx.ChatId;
            var rtlStatus = ctx.CharSetting("Rtl");
            var status = rtlStatus.HasValue ? rtlStatus.ToString() : "allowed";
            if (!status.Equals("ban") && !status.Equals("kick")) return;

            var name = update.Message.From.FirstName;
            const string rtl = "‮";
            var lastName = "x";
            if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
            if (update.Message.From.LastName != null) lastName = update.Message.From.LastName;
            var text = update.Message.Text ?? "";
            if (!text.Contains(rtl) && !name.Contains(rtl) && !lastName.Contains(rtl)) return;

            var lang = ctx.Lang;
            var userId = ctx.UserId;

            Bot.DispatchAction(() =>
            {
                try
                {
                    string reply;
                    switch (status)
                    {
                        case "kick":
                            Methods.KickUser(chatId, userId, lang);
                            reply = Methods.GetLocaleString(lang, "kickedForRtl", $"{name}, {userId}");
                            Service.LogBotAction(chatId, reply, userId);
                            Bot.Send(reply, update);
                            break;
                        case "ban":
                            var res = Methods.BanUser(chatId, userId, lang);
                            if (res)
                            {
                                Methods.SaveBan(userId, "rtl");
                                Methods.AddBanList(chatId, userId, update.Message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedForRtl", ""));
                                reply = Methods.GetLocaleString(lang, "bannedForRtl", $"{name}, {userId}");
                                Service.LogBotAction(chatId, reply, userId);
                                Bot.Send(reply, update);
                            }
                            break;
                        case "tempban":
                            var time = Methods.GetGroupTempbanTime(chatId);
                            var timeBanned = TimeSpan.FromMinutes(time);
                            string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                            var message = Methods.GetLocaleString(lang, "tempbannedForRtl",
                                $"{name}, {userId}", timeText);
                            Service.LogBotAction(chatId, message, userId);
                            Commands.Tempban(userId, chatId, time, userId.ToString(), message: message);
                            break;
                    }
                }
                catch (Exception e)
                {
                    LogHelper.Error($"RightToLeft action in {chatId} failed: {e.Message}\n{e.StackTrace}");
                }
            }, "RightToLeft");
        }

        private const string ArabicChars = "[ساینبتسیکبدثصکبثحصخبدوزطئظضچج]";

        internal static async Task ArabDetection(MessageContext ctx)
        {
            var m = ctx.Message;
            var text = $"{m.Text} {m.From.FirstName} {m.From.LastName} {m.ForwardFrom?.FirstName} {m.ForwardFrom?.LastName} {m.From.Username} {m.ForwardFrom?.Username}";
            await CharacterDetection(ctx, "Arab", ArabicChars, text);
            await RightToLeft(ctx);
        }

        internal static Task ArabJoinDetection(MessageContext ctx)
        {
            var text = $"{ctx.Message.NewChatMember.FirstName} {ctx.Message.NewChatMember.LastName}";
            return CharacterDetection(ctx, "Arab", ArabicChars, text);
        }

        /// <summary>
        /// Applies the chat's chat:{id}:char/{setting} policy (kick/ban/tempban) if any character
        /// in <paramref name="text"/> matches <paramref name="characterClass"/>.
        /// Users on the watch list are exempt.
        /// </summary>
        private static async Task CharacterDetection(MessageContext ctx, string setting, string characterClass, string text)
        {
            if (ctx.Watched) return;

            var status = ctx.CharSetting(setting).ToString();
            if (string.IsNullOrEmpty(status)) status = "allowed";
            if (status.Equals("allowed")) return;

            // One match over the whole string; this used to run the regex once per character.
            if (!Regex.IsMatch(text, characterClass)) return;

            var chatId = ctx.ChatId;
            var lang = ctx.Lang;
            var userId = ctx.UserId;
            var name = ctx.Message.From.FirstName;
            if (ctx.Message.From.Username != null) name = $"{name} (@{ctx.Message.From.Username})";
            var update = ctx.Update;

            Bot.DispatchAction(() =>
            {
                try
                {
                    string reply;
                    switch (status)
                    {
                        case "kick":
                            Methods.KickUser(chatId, userId, lang);
                            reply = Methods.GetLocaleString(lang, "kickedForNoEnglishScript", $"{name}, {userId}");
                            Service.LogBotAction(chatId, reply, userId);
                            Bot.Send(reply, update);
                            break;
                        case "ban":
                            var res = Methods.BanUser(chatId, userId, lang);
                            if (res)
                            {
                                Methods.SaveBan(userId, "arab");
                                Methods.AddBanList(chatId, userId, ctx.Message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedForNoEnglishScript", "."));

                                reply = Methods.GetLocaleString(lang, "bannedForNoEnglishScript", $"{name}, {userId}");
                                Service.LogBotAction(chatId, reply, userId);
                                Bot.Send(reply, update);
                            }
                            break;
                        case "tempban":
                            var time = Methods.GetGroupTempbanTime(chatId);
                            var timeText = TimeSpan.FromMinutes(time).ToString(@"dd\:hh\:mm");
                            var message = Methods.GetLocaleString(lang, "tempbanForNoEnglishScript",
                                $"{name}, {userId}", timeText);
                            Service.LogBotAction(chatId, message, userId);
                            Commands.Tempban(userId, chatId, time, userId.ToString(), message: message);
                            break;
                    }
                }
                catch (Exception e)
                {
                    LogHelper.Error($"CharacterDetection action in {chatId} failed: {e.Message}");
                }
            }, "CharacterDetection");
        }

        internal static bool isIgnored(MessageContext ctx, string msgType)
        {
            var status = MessageContext.Field(ctx.FloodExceptions, msgType);
            return status.HasValue && status.Equals("no");
        }
    }
}
