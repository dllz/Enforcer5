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
    /// <remarks>
    /// While a raid alert or raid mode is on in a group (<see cref="Raids.RaidGuard.Quiet"/>), the
    /// per-member notices here ("kicked for flooding", "media not allowed") are not posted: during a
    /// raid there would be one per raider, using up the messages the raid notice needs, and the raid
    /// notice has the counts. The actions themselves and the log channel are unchanged.
    /// </remarks>
    /// <remarks>
    /// Admins (and anonymous admins, and members elevated with /elevate) are exempt from every
    /// check here. Whether someone is one is only asked once a check is about to act, from the
    /// shared <see cref="AdminDirectory"/>, so an ordinary message costs nothing extra.
    /// </remarks>
    public static class OnMessage
    {
        internal static async Task AntiFlood(MessageContext ctx)
        {
            var update = ctx.Update;
            // Measured from receipt, not from now: queue time must not count here. Both are UTC.
            var time = ctx.ReceivedAt - update.Message.Date.ToUniversalTime();
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

            var flood = ctx.Policy.Flood;
            if (!flood.Enabled)
            {
                return;
            }

            var msgType = Methods.GetContentType(update.Message);
            var lang = ctx.Lang;
            if (isIgnored(ctx, msgType))
            {
                return;
            }

            // No limit (a missing or unreadable one in the old settings) means no flood checking.
            if (flood.MaxMessages is not int maxmsgs) return;

            // One atomic count per window of FloodWindow from the first message. It used to be read
            // with the context and written back as count + 1, so with 32 consumers two messages read
            // the same count and one was lost; and every message renewed the expiry.
            var userid = ctx.UserId;
            var messageId = update.Message.MessageId;
            FloodTracker.Seen(chatId, userid, messageId);
            var num = await FloodTracker.CountAsync(chatId, userid);
            if (num <= maxmsgs) return;
            // Admins are exempt: only asked once someone is over the limit, from the admin list
            // shared with everything else (one call per group a minute at most).
            if (await Methods.IsGroupAdminAsync(userid, chatId)) return;
            var action = flood.Action;
            if (num > maxmsgs + 1)
            {
                // Already being dealt with: whatever else they send in this window goes too. A ban
                // takes everything they posted anyway.
                if (action != "ban")
                    await Enforcement.EnforcementQueue.SubmitAsync(Enforcement.EnforcementJob.Delete(chatId, messageId));
                return;
            }

            {
                var name = update.Message.From.FirstName;
                if (update.Message.From.Username != null) name = $"{name} (@{update.Message.From.Username})";
                var groupId = chatId;
                // The flood itself goes, whatever the action: in one call for up to 100 messages.
                var floodMessages = FloodTracker.Recent(chatId, userid);
                if (action != "ban" && floodMessages.Length > 0)
                    await Enforcement.EnforcementQueue.SubmitAsync(Enforcement.EnforcementJob.Delete(chatId, floodMessages));
                // Raid detection sees every flood, whatever the group does about it.
                FloodTracker.OnFlood(chatId, userid);
                // In raid mode an untrusted flooder is banned by the raid, with their messages.
                var raidBans = await Raids.RaidGuard.WillHandleFloodAsync(chatId, userid);
                Detection.DetectionEngine.Checked(chatId, userid, update.Message, Detection.SignCatalog.Flood,
                    raidBans ? "ban" : action, deleted: raidBans || action != "ban" && floodMessages.Length > 0,
                    evidence: new { messages = num, limit = maxmsgs, raid = raidBans });
                if (raidBans) return;

                Bot.DispatchAction(() =>
                {
                    try
                    {
                        switch (action)
                        {
                            case "kick":
                                var res = Methods.KickUser(chatId, userid, lang, "Flood");
                                if (res)
                                {
                                    Methods.SaveBan(userid, "flood");
                                    if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.Send(Methods.GetLocaleString(lang, "kickedForFlood", $"{name}, {userid}"), update);
                                }
                                break;
                            case "ban":
                                // Banned with everything they posted, in the same call.
                                res = Methods.BanUser(chatId, userid, lang, "Flood", revokeMessages: true);
                                if (res)
                                {
                                    Methods.SaveBan(userid, "flood");
                                    Methods.AddBanList(chatId, userid, update.Message.From.FirstName,
                                        Methods.GetLocaleString(lang, "bannedForFlood", ".."));
                                    if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.Send(Methods.GetLocaleString(lang, "bannedForFlood", name), update);
                                }
                                break;
                            case "warn":
                                Commands.Warn(userid, groupId, update, targetnick: userid.ToString(), reason: "Flood");
                                Methods.SaveBan(userid, "flood");
                                break;
                            case "tempban":
                                var time2 = Methods.GetGroupTempbanTime(groupId);
                                var timeBanned = TimeSpan.FromMinutes(time2);
                                string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                                var message = Methods.GetLocaleString(lang, "tempbannedForFlood",
                                    $"{userid}", timeText);
                                if (Commands.Tempban(userid, groupId, time2, userid.ToString(), message: message, reason: "Flood"))
                                {
                                    Methods.SaveBan(userid, "flood");
                                }
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, $"Flood action {action} on {userid} in {chatId}");
                    }
                }, "AntiFlood");
            }
        }

        private static void AntiTextLenght(MessageContext ctx)
        {
            var update = ctx.Update;
            var groupId = ctx.ChatId;
            var settings = ctx.Policy.TextLength;
            if (!settings.Enabled) return;

            var text = update.Message.Text;
            var intml = settings.MaxCharacters ?? DefaultMaxTextLength;
            var intmline = settings.MaxLines ?? DefaultMaxTextLines;
            if (text.Length < intml && LineCount(text) < intmline) return;

            var action = settings.Action;
            var lang = ctx.Lang;
            var userid = ctx.UserId;
            Bot.DispatchAction(() =>
            {
                if (Methods.IsGroupAdmin(userid, groupId)) return;
                Detection.DetectionEngine.Checked(groupId, userid, update.Message, Detection.SignCatalog.TextLength, action,
                    deleted: true, evidence: new { length = text.Length, lines = LineCount(text), maxLength = intml, maxLines = intmline });
                try
                {
                    Bot.DeleteMessage(groupId, update.Message.MessageId);
                }
                catch (Exception e)
                {
                    // Moving on to the action regardless. An already deleted message counts as
                    // success in DeleteMessage, so this is e.g. a missing delete right.
                    ErrorReporter.Report(e, $"Deleting an over-long message in {groupId}");
                }

                string reply;
                switch (action)
                {
                    case "kick":
                        Methods.KickUser(groupId, userid, lang, "Long message");
                        reply = Methods.GetLocaleString(lang, "kickformesslength", userid);
                        Service.LogBotAction(groupId, reply, userid);
                        if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(reply, update);
                        return;
                    case "ban":
                        var res = Methods.BanUser(groupId, userid, lang, "Long message");
                        if (res)
                        {
                            Methods.SaveBan(userid, "longmessages");
                            reply = Methods.GetLocaleString(lang, "banformesslength", userid);
                            Service.LogBotAction(groupId, reply, userid);
                            if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(reply, update);
                            return;
                        }
                        break;
                    case "warn":
                        Commands.Warn(userid, groupId, update, targetnick: userid.ToString(), reason: "Long message");
                        return;
                    case "tempban":
                        var time = Methods.GetGroupTempbanTime(groupId);
                        var timeBanned = TimeSpan.FromMinutes(time);
                        string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                        var message = Methods.GetLocaleString(lang, "tempbanformesslength",
                            $"{userid}", timeText);
                        Service.LogBotAction(groupId, message, userid);
                        Commands.Tempban(userid, groupId, time, userid.ToString(), message: message, reason: "Long message");
                        break;
                    default:
                        // No action chosen (this was a case for the string "default", so it never
                        // ran and the over-long message went without a word).
                        if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(Methods.GetLocaleString(lang, "actionNotSettext"), update);
                        break;
                }
            }, "AntiTextLenght");
        }

        private static void AntiNameLength(MessageContext ctx)
        {
            var update = ctx.Update;
            var groupId = ctx.ChatId;
            var settings = ctx.Policy.NameLength;
            if (!settings.Enabled) return;

            var text = update.Message.From.FirstName;
            if (update.Message.From.LastName != null)
                text = $"{text}{update.Message.From.LastName}";
            var intml = settings.MaxCharacters ?? DefaultMaxNameLength;
            if (text.Length < intml) return;

            var action = settings.Action;
            var lang = ctx.Lang;
            var userid = ctx.UserId;
            Bot.DispatchAction(() =>
            {
                if (Methods.IsGroupAdmin(userid, groupId)) return;
                Detection.DetectionEngine.Checked(groupId, userid, update.Message, Detection.SignCatalog.NameLength, action,
                    evidence: new { length = text.Length, maxLength = intml });
                string reply;
                switch (action)
                {
                    case "kick":
                        Methods.KickUser(groupId, userid, lang, "Name too long");
                        reply = Methods.GetLocaleString(lang, "kickfornamelength", userid);
                        Service.LogBotAction(groupId, reply, userid);
                        if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(reply, update);
                        break;
                    case "ban":
                        var res = Methods.BanUser(groupId, userid, lang, "Name too long");
                        if (res)
                        {
                            Methods.SaveBan(userid, "namelength");
                            reply = Methods.GetLocaleString(lang, "banfornamelength", userid);
                            Service.LogBotAction(groupId, reply, userid);
                            if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(reply, update);
                        }
                        break;
                    case "warn":
                        Commands.Warn(userid, groupId, update, targetnick: userid.ToString(), reason: "Name too long");
                        break;
                    case "tempban":
                        var time = Methods.GetGroupTempbanTime(groupId);
                        var timeBanned = TimeSpan.FromMinutes(time);
                        string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                        var message = Methods.GetLocaleString(lang, "tempbanfornamelength",
                            $"{userid}", timeText);
                        Service.LogBotAction(groupId, message, userid);
                        Commands.Tempban(userid, groupId, time, userid.ToString(), message: message, reason: "Name too long");
                        break;
                    default:
                        // No action chosen (this was a case for the string "default", so it never
                        // ran and the over-long message went without a word).
                        if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(Methods.GetLocaleString(lang, "actionNotSetname"), update);
                        break;
                }
            }, "AntiNameLength");
        }

        // The limits a new group gets (Service.GenerateSettings), for a group whose limit is missing,
        // unreadable or not positive. Such a limit used to read as 0, which made every message too
        // long and every name too long.
        internal const int DefaultMaxTextLength = 1024;
        internal const int DefaultMaxTextLines = 50;
        internal const int DefaultMaxNameLength = 50;

        /// <summary>
        /// Lines as the reader sees them; a trailing line break adds none. This used to be the length
        /// of a Regex.Split whose pattern captured each line, which counted every line about twice.
        /// </summary>
        internal static int LineCount(string text) => text.TrimEnd('\r', '\n').Split('\n').Length;

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
                ErrorReporter.Report(e, $"AntiNameLength in {ctx.ChatId}");
            }
            try
            {
                if (ctx.Message.Type == MessageType.Text)
                    AntiTextLenght(ctx);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"AntiTextLenght in {ctx.ChatId}");
            }
        }

        internal static Task CheckMedia(MessageContext ctx)
        {
            if (ctx.Watched) return Task.CompletedTask;

            var message = ctx.Message;
            var chatId = ctx.ChatId;
            var media = Methods.GetContentType(message);
            // A link in a caption is a link: a photo captioned with one used to get past the link lock.
            var settings = ctx.Policy.Media;
            if (!settings.IsBlocked(media) && settings.IsBlocked("link") &&
                (message.CaptionEntities ?? Array.Empty<Telegram.Bot.Types.MessageEntity>())
                    .Any(e => e.Type is MessageEntityType.Url or MessageEntityType.TextLink))
                media = "link";
            if (!settings.IsBlocked(media)) return Task.CompletedTask;

            var lang = ctx.Lang;
            var name = $"{message.From.FirstName} [{message.From.Id}]";
            if (message.From.Username != null)
                name = $"{name} (@{message.From.Username})";
            var configuredAction = settings.Action;
            var max = settings.EffectiveWarnLimit;

            // The warn counter is incremented and reset in the same unit of work as the action it
            // gates. Doing the increment out here and dispatching only the action would let a
            // dropped action reset someone's strike count for free.
            Bot.DispatchAction(() =>
            {
                if (Methods.IsGroupAdmin(message.From.Id, chatId)) return;
                var current = Redis.db.HashIncrementAsync($"chat:{chatId}:mediawarn", message.From.Id, 1).Result;
                var warned = WarnHistory.Added(chatId, message.From.Id, WarnKind.Media, 1, current, null, null, "Media");

                var overLimit = current >= max;
                var action = overLimit ? configuredAction : null;
                Detection.DetectionEngine.Checked(chatId, message.From.Id, message, Detection.SignCatalog.MediaLock,
                    action is "kick" or "ban" or "tempban" ? action : "delete", deleted: true,
                    evidence: new { type = media, strike = current, limit = max });
                if (overLimit)
                {
                    Redis.db.HashDeleteAsync($"chat:{chatId}:mediawarn", message.From.Id);
                    WarnHistory.Reset(chatId, message.From.Id, WarnKind.Media, current, null, "Media limit");
                }

                if (overLimit)
                {
                    string reply;
                    switch (action)
                    {
                        case "kick":
                            Methods.KickUser(chatId, message.From.Id, lang, "Media", trigger: warned);
                            reply = Methods.GetLocaleString(lang, "kickedformedia", $"{name}");
                            Service.LogBotAction(chatId, reply, message.From.Id);
                            if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(reply, message);
                            break;
                        case "ban":
                            var res = Methods.BanUser(chatId, message.From.Id, lang, "Media", trigger: warned);
                            if (res)
                            {
                                Methods.SaveBan(message.From.Id, "media");
                                reply = Methods.GetLocaleString(lang, "bannedformedia", name);
                                Service.LogBotAction(chatId, reply, message.From.Id);
                                Methods.AddBanList(chatId, message.From.Id, message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedformedia", ""));
                                if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(reply, message);
                            }
                            break;
                        case "tempban":
                            var time = Methods.GetGroupTempbanTime(chatId);
                            var timeBanned = TimeSpan.FromMinutes(time);
                            string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                            var messageText = Methods.GetLocaleString(lang, "tempbannedformedia",
                                $"{name}, {message.From.Id}", timeText);
                            Service.LogBotAction(chatId, messageText, message.From.Id);
                            Commands.Tempban(message.From.Id, chatId, time, message.From.Id.ToString(), message: messageText, reason: "Media");
                            break;
                    }
                }
                else
                {
                    if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.SendReply(Methods.GetLocaleString(lang, "mediaNotAllowed", current, max), message);
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

            DeleteUnlessAdmin(ctx, Detection.SignCatalog.InlineBot, new { bot = viaBot.Id }, "BlockedInlineBot");
            return true;
        }

        /// <summary>
        /// Deletes a sticker from a pack on the chat's sticker pack blocklist. Same contract and
        /// exemptions as <see cref="BlockedInlineBot"/>; returning true also keeps the media filter
        /// from issuing a strike for a sticker that is being deleted anyway.
        /// </summary>
        internal static bool BlockedStickerSet(MessageContext ctx)
        {
            if (!ctx.StickerSetBlocked) return false;
            if (ctx.Watched || ctx.Message.IsAutomaticForward) return false;

            DeleteUnlessAdmin(ctx, Detection.SignCatalog.StickerSet, new { set = ctx.Message.Sticker?.SetName }, "BlockedStickerSet");
            return true;
        }

        /// <summary>
        /// Silently deletes the message unless an admin sent it. Whether the sender is an admin can
        /// take a Telegram call, so the check and the delete are dispatched.
        /// </summary>
        private static void DeleteUnlessAdmin(MessageContext ctx, Detection.SignDefinition sign, object evidence, string name)
        {
            var message = ctx.Message;
            var chatId = ctx.ChatId;
            // An anonymous admin posts as the group itself. Any other sender chat is a channel a
            // member is posting as, which is never an admin, so it needs no lookup either.
            var senderChat = message.SenderChat;
            if (senderChat != null && senderChat.Id == chatId) return;
            var checkAdmin = senderChat == null;

            var userId = ctx.UserId;
            var messageId = message.MessageId;
            Bot.DispatchAction(() =>
            {
                if (checkAdmin && Methods.IsGroupAdmin(userId, chatId)) return;
                // Posted as a channel: the member behind it cannot be seen, so the channel is the evidence.
                if (senderChat != null)
                    Detection.DetectionEngine.Checked(chatId, null, message, sign, "delete", deleted: true,
                        evidence: new { channel = senderChat.Id, of = evidence });
                else
                    Detection.DetectionEngine.Checked(chatId, userId, message, sign, "delete", deleted: true, evidence: evidence);
                // Throws without the delete right; DispatchAction logs it. Nothing is sent to the
                // chat: one notice per blocked message would double the noise during a raid.
                Bot.DeleteMessage(chatId, messageId);
            }, name);
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
                        // Shared with raid detection, one call at a time per group: a raid of channel posts
                        // arriving before the first answer no longer asks once per post.
                        var details = ChatDetails.GetAsync(chatId).GetAwaiter().GetResult()
                                      ?? throw new InvalidOperationException("Telegram did not give the group's details");
                        var linked = details.LinkedChatId ?? 0;
                        Repositories.ChannelPosts.SetLinkedChannelAsync(chatId, linked).GetAwaiter().GetResult();
                        if (linked == channel.Id) return;
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, $"Linked channel lookup for {chatId}, deleting without a ban");
                        mayBan = false;
                    }
                }

                var deleted = TryTelegram(() => Bot.DeleteMessage(chatId, messageId), $"Deleting a channel post in {chatId}");
                var banned = mayBan &&
                             Enforcement.EnforcementQueue.Run(Enforcement.EnforcementJob.BanSenderChat(chatId, channel.Id)).Succeeded;
                if (!deleted && !banned) return;
                Detection.DetectionEngine.Checked(chatId, null, message, Detection.SignCatalog.ChannelPost,
                    banned ? "ban" : "delete", deleted: deleted, evidence: new { channel = channel.Id });

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
                ErrorReporter.Report(e, what);
                return false;
            }
        }

        internal static async Task RightToLeft(MessageContext ctx)
        {
            if (ctx.Watched) return;

            var update = ctx.Update;
            var chatId = ctx.ChatId;
            var status = ctx.Policy.Scripts.Rtl ?? "allowed";
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
                    if (Methods.IsGroupAdmin(userId, chatId)) return;
                    Detection.DetectionEngine.Checked(chatId, userId, update.Message, Detection.SignCatalog.ScriptRtl, status,
                        evidence: new { inText = text.Contains(rtl), inName = name.Contains(rtl) || lastName.Contains(rtl) });
                    string reply;
                    switch (status)
                    {
                        case "kick":
                            Methods.KickUser(chatId, userId, lang, "Right-to-left text");
                            reply = Methods.GetLocaleString(lang, "kickedForRtl", $"{name}, {userId}");
                            Service.LogBotAction(chatId, reply, userId);
                            if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.Send(reply, update);
                            break;
                        case "ban":
                            var res = Methods.BanUser(chatId, userId, lang, "Right-to-left text");
                            if (res)
                            {
                                Methods.SaveBan(userId, "rtl");
                                Methods.AddBanList(chatId, userId, update.Message.From.FirstName,
                                    Methods.GetLocaleString(lang, "bannedForRtl", ""));
                                reply = Methods.GetLocaleString(lang, "bannedForRtl", $"{name}, {userId}");
                                Service.LogBotAction(chatId, reply, userId);
                                if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.Send(reply, update);
                            }
                            break;
                        case "tempban":
                            var time = Methods.GetGroupTempbanTime(chatId);
                            var timeBanned = TimeSpan.FromMinutes(time);
                            string timeText = timeBanned.ToString(@"dd\:hh\:mm");
                            var message = Methods.GetLocaleString(lang, "tempbannedForRtl",
                                $"{name}, {userId}", timeText);
                            Service.LogBotAction(chatId, message, userId);
                            Commands.Tempban(userId, chatId, time, userId.ToString(), message: message, reason: "Right-to-left text");
                            break;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"RightToLeft action in {chatId}");
                }
            }, "RightToLeft");
        }

        private const string ArabicChars = "[ساینبتسیکبدثصکبثحصخبدوزطئظضچج]";

        internal static async Task ArabDetection(MessageContext ctx)
        {
            var m = ctx.Message;
            var text = $"{m.Text} {m.From.FirstName} {m.From.LastName} {m.ForwardFrom?.FirstName} {m.ForwardFrom?.LastName} {m.From.Username} {m.ForwardFrom?.Username}";
            await CharacterDetection(ctx, ArabicChars, text);
            await RightToLeft(ctx);
        }

        /// <summary>
        /// The names of everyone in a join message, each checked and acted on as themselves: the
        /// message's sender is whoever added them, who used to be the one kicked, and only the first
        /// person added was looked at.
        /// </summary>
        internal static async Task ArabJoinDetection(MessageContext ctx)
        {
            foreach (var joiner in ctx.Message.NewChatMembers ?? Array.Empty<Telegram.Bot.Types.User>())
            {
                if (joiner.Id == Bot.Me.Id) continue;
                await CharacterDetection(ctx, ArabicChars, $"{joiner.FirstName} {joiner.LastName}", joiner);
            }
        }

        /// <summary>
        /// Applies the chat's Arabic script preset (kick/ban/tempban) if any character
        /// in <paramref name="text"/> matches <paramref name="characterClass"/>, to
        /// <paramref name="target"/> (a joiner) or, without one, the message's sender.
        /// Users on the watch list and admins are exempt.
        /// </summary>
        private static async Task CharacterDetection(MessageContext ctx, string characterClass, string text,
            Telegram.Bot.Types.User target = null)
        {
            if (target == null && ctx.Watched) return;

            // Joins run whether or not the context is complete; without settings nothing is set.
            var status = ctx.Policy?.Scripts.Arabic;
            if (status == null) return; // allowed

            // One match over the whole string; this used to run the regex once per character.
            if (!Regex.IsMatch(text, characterClass)) return;

            var chatId = ctx.ChatId;
            var lang = ctx.Lang;
            var person = target ?? ctx.Message.From;
            var userId = person.Id;
            var name = person.FirstName;
            if (person.Username != null) name = $"{name} (@{person.Username})";
            var update = ctx.Update;

            Bot.DispatchAction(() =>
            {
                try
                {
                    // A joiner's watch list entry is not in the context, which is the sender's.
                    if (target != null && Redis.db.SetContainsAsync($"chat:{chatId}:watch", userId).Result) return;
                    if (Methods.IsGroupAdmin(userId, chatId)) return;
                    // A joiner's name: the join message is not theirs.
                    Detection.DetectionEngine.Checked(chatId, userId, target == null ? ctx.Message : null,
                        Detection.SignCatalog.ScriptArabic, status, evidence: new { onJoin = target != null });
                    string reply;
                    switch (status)
                    {
                        case "kick":
                            Methods.KickUser(chatId, userId, lang, "Non-Latin script");
                            reply = Methods.GetLocaleString(lang, "kickedForNoEnglishScript", $"{name}, {userId}");
                            Service.LogBotAction(chatId, reply, userId);
                            if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.Send(reply, update);
                            break;
                        case "ban":
                            var res = Methods.BanUser(chatId, userId, lang, "Non-Latin script");
                            if (res)
                            {
                                Methods.SaveBan(userId, "arab");
                                Methods.AddBanList(chatId, userId, person.FirstName,
                                    Methods.GetLocaleString(lang, "bannedForNoEnglishScript", "."));

                                reply = Methods.GetLocaleString(lang, "bannedForNoEnglishScript", $"{name}, {userId}");
                                Service.LogBotAction(chatId, reply, userId);
                                if (!Raids.RaidGuard.Quiet(ctx.ChatId)) Bot.Send(reply, update);
                            }
                            break;
                        case "tempban":
                            var time = Methods.GetGroupTempbanTime(chatId);
                            var timeText = TimeSpan.FromMinutes(time).ToString(@"dd\:hh\:mm");
                            var message = Methods.GetLocaleString(lang, "tempbanForNoEnglishScript",
                                $"{name}, {userId}", timeText);
                            Service.LogBotAction(chatId, message, userId);
                            Commands.Tempban(userId, chatId, time, userId.ToString(), message: message, reason: "Non-Latin script");
                            break;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"CharacterDetection action in {chatId}");
                }
            }, "CharacterDetection");
        }

        internal static bool isIgnored(MessageContext ctx, string msgType)
        {
            return ctx.Policy.Flood.IsExempt(msgType);
        }
    }
}
