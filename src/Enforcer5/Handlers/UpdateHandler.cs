using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;

#pragma warning disable CS4014 // Because this call is not ed, execution of the current method continues before the call is completed
namespace Enforcer5.Handlers
{

    internal static class UpdateHandler
    {
        /// <summary>
        /// Entry point for the update consumers. The consumer awaits this, so the work it covers is
        /// what bounds concurrency - which is why only Redis-bound work is awaited here. Callbacks
        /// and inline queries run blocking Telegram handlers, so they go to the action pool.
        /// </summary>
        internal static async Task RouteAsync(Update update, DateTime receivedAt)
        {
            switch (update.Type)
            {
                case UpdateType.CallbackQuery:
                    PrincipalObserver.Observe(update);
                    Bot.DispatchInteractive(() => HandleCallback(update.CallbackQuery), "HandleCallback");
                    break;
                case UpdateType.InlineQuery:
                    PrincipalObserver.Observe(update);
                    Bot.DispatchInteractive(() => HandleInlineQuery(update.InlineQuery), "HandleInlineQuery");
                    break;
                case UpdateType.EditedMessage:
                    // A post edited into spam after it got past the checks: raid detection sees it again.
                    var edited = update.EditedMessage;
                    if (edited?.From == null || edited.Chat.Type is not (ChatType.Group or ChatType.Supergroup)) return;
                    if (!BotSenderFilter.Accept(edited)) return;
                    var editCtx = await MessageContext.BuildAsync(new Update { Id = update.Id, Message = edited }, receivedAt);
                    if (!editCtx.Complete) return;
                    // The edit rules, for posts of any age: an old post edited is the attack they are for.
                    Bot.Dispatch(() => Detection.EditCheck.OnEditAsync(editCtx), "EditCheck");
                    // The filters and the newcomer signs on what it says now.
                    Detection.FilterCheck.OnMessage(editCtx);
                    Detection.MessageSigns.OnMessage(editCtx);
                    Detection.NameSigns.OnMessage(editCtx);
                    if (edited.Date.ToUniversalTime() < Bot.StartTime.AddDays(-2)) return; // too old for raid detection
                    await Raids.RaidGuard.OnMessageAsync(editCtx, edited: true);
                    break;
                case UpdateType.MyChatMember:
                    // The bot's own status or rights changed: the admin list is asked again, and jobs
                    // waiting for the rights are tried now rather than at the next probe.
                    if (update.MyChatMember?.Chat is { } changed)
                    {
                        AdminDirectory.Forget(changed.Id);
                        if (update.MyChatMember.NewChatMember is ChatMemberAdministrator { CanRestrictMembers: true })
                            Enforcement.EnforcementQueue.RightsChanged(changed.Id);
                    }
                    break;
                case UpdateType.ChatMember:
                    // Joins and leaves, with the invite link used: for raid detection and its capture.
                    PrincipalObserver.Observe(update);
                    MemberEvents.FromChatMember(update.ChatMember);
                    break;
                default:
                    if (update.Message == null) return;
                    // Other bots' messages (Bot-to-Bot Communication Mode) are ignored, trusted ones aside.
                    if (!BotSenderFilter.Accept(update.Message)) return;
                    if ((update.Message?.Date.ToUniversalTime() ?? DateTime.MinValue) < Bot.StartTime.AddMinutes(-2))
                        return; //toss it
                    await HandleUpdate(update, receivedAt);
                    break;
            }
        }
        private static void Log(Update update, string text, Models.Commands command = null)
        {
            // journald already timestamps each line, so log content only.
            var latency = (DateTime.UtcNow - update.Message.Date.ToUniversalTime()).ToString(@"mm\:ss\.ff");
            var from = update.Message.From.FirstName;

            if (text.Equals("text"))
            {
                var name = command?.Method.GetMethodInfo().Name ?? "";
                Console.WriteLine($"{name} {latency} {from} -> [{update.Message.Chat.Title} {update.Message.Chat.Id}]");
            }
            else if (text.Equals("chatMember"))
            {
                Console.WriteLine($"{latency} {from} -> [{update.Message.NewChatMember?.FirstName} {update.Message.NewChatMember?.Id}]");
            }
            else if (text.Equals("extra"))
            {
                Console.WriteLine($"{latency} {from} -> [{update.Message.Chat.Title} {update.Message.Chat.Id}]");
            }
        }
        private static void Log(CallbackQuery update, Models.CallBacks command = null)
        {
            var name = command?.Method.GetMethodInfo().Name ?? "";
            Console.WriteLine($"{name} {update.Message?.From?.FirstName} -> [{update.From.FirstName} {update.From.Id}]");
        }

        private static void Log(InlineQuery update, string text, Models.Queries command = null)
        {
            var name = command?.Method?.GetMethodInfo().Name ?? "";
            Console.WriteLine($"{name} Query: {text} [{update.From.FirstName} {update.From.Id}]");
        }

        private static async Task HandleUpdate(Update update, DateTime receivedAt)
        {
            {
#if PREMIUM
                if (update.Message.Chat.Type != ChatType.Private)
                {
                    var allowed = await Redis.db.SetContainsAsync("premiumBot", update.Message.Chat.Id);
                    if (!allowed)
                    {
                        // Fires for every message from a non-premium group, so it must not occupy
                        // a consumer.
                        Bot.DispatchAction(() =>
                        {
                            Bot.Send(
                                "Hi there, this bot is no longer active, please use @enforcerbot instead of this bot and remove this bot from your group to stop the spam.\nIt has the same features and more.\nRemember to subscribe to our channel @greywolfdev for updates for @enforcerbot and more",
                                update);
                            Bot.Api.LeaveChat(update.Message.Chat.Id);
                        }, "PremiumNotice");
                        return;
                    }
                }
#endif

                // Both ban checks in one pipelined batch, before we spend anything else on this chat.
                var bannedGroupTask = Redis.db.SetContainsAsync("bot:bannedGroups", update.Message.Chat.Id);
                var bannedUserTask = Redis.db.SetContainsAsync("bot:bannedGroups", update.Message.From.Id);
                await Task.WhenAll(bannedGroupTask, bannedUserTask);
                var bannedGroup = bannedGroupTask.Result;
                if (bannedGroup || bannedUserTask.Result)
                {
                    if (update.Message.Chat.Type != ChatType.Private && bannedGroup)
                    {
                        Bot.Api.LeaveChat(update.Message.Chat.Id);
                    }
                    return;
                }

                // Names, titles and last-seen times for PostgreSQL; an in-memory write, flushed once a minute.
                PrincipalObserver.Observe(update);

                var background = new List<Task>();
                Interlocked.Increment(ref Bot.MessagesProcessed);

                try
                {
                    if (update.Message == null)
                    {
                        return;
                    }

                    // One batch for everything the per-message handlers need. Individual reads are
                    // allowed to fail: a timeout on one settings hash must not discard the whole
                    // update, which would silently drop commands.
                    var ctx = await MessageContext.BuildAsync(update, receivedAt);

                    // Joins and leaves first, so raid detection counts this join before anything acts on it.
                    if (ctx.IsGroup && (update.Message.NewChatMembers?.Length > 0 || update.Message.LeftChatMember != null))
                        MemberEvents.FromServiceMessage(update.Message);

                    // Stats and commands run regardless; moderation only on a context we fully read.
                    background.Add(CollectStats(ctx));

                    var moderate = ctx.IsGroup && ctx.Complete;
                    if (moderate)
                    {
                        // First, because a post made as a channel carries Telegram's shared
                        // placeholder as its sender: the per-user filters below would count every
                        // channel poster as one user, and try to ban that placeholder.
                        switch (OnMessage.ChannelPost(ctx))
                        {
                            case ChannelPostDecision.Remove:
                            case ChannelPostDecision.RemoveUnlessLinked:
                                return; // being deleted; the finally still awaits CollectStats
                            case ChannelPostDecision.Exempt:
                                moderate = false;
                                break;
                        }
                    }
                    if (moderate)
                    {
                        background.Add(Methods.IsRekt(ctx));
                        background.Add(OnMessage.AntiFlood(ctx));
                        background.Add(Raids.RaidGuard.OnMessageAsync(ctx));

                        // Ahead of the type switch, because most inline bot traffic is GIFs and
                        // any type can be sent via a bot. A blocked bot's message, or a sticker from
                        // a blocked pack, is being deleted, so nothing below should act on it; the
                        // finally still awaits the work queued above.
                        if (OnMessage.BlockedInlineBot(ctx) || OnMessage.BlockedStickerSet(ctx)) return;

                        // Text, captions and the sender's name, for every kind of message.
                        if (update.Message.Type != MessageType.NewChatMembers) Detection.FilterCheck.OnMessage(ctx);
                        // What a media post was, so an edit that swaps it can be seen (Detection step 8).
                        Detection.EditCheck.Record(ctx);
                        // Links, mentions and forwards from new members, and the same post from several (step 9).
                        Detection.MessageSigns.OnMessage(ctx);
                        // A name that copies an admin or a bot, or claims to be staff, changed since joining (step 10).
                        Detection.NameSigns.OnMessage(ctx);
                    }

                    // The group an admin picked for the settings menu (/menu in private).
                    if (update.Message.ChatShared != null && update.Message.Chat.Type == ChatType.Private)
                    {
                        var shared = update.Message;
                        Bot.DispatchInteractive(() => Commands.MenuForSharedChat(shared), "MenuForSharedChat");
                    }

                    switch (update.Message.Type)
                    {
                        case MessageType.Unknown:
                            break;
                        case MessageType.Text:
                            if (moderate)
                            {
                                background.Add(OnMessage.CheckMedia(ctx));
                            }

                            // Test the prefix here so an ordinary chat line costs nothing; only a
                            // real command pays for a dispatch. The handlers are synchronous and
                            // can block behind Telegram, so they never run on a consumer.
                            var text = update.Message.Text;

                            // A typed warn removal reason. The cheap test runs inline; the Redis
                            // lookup only for replies to the bot, and off the consumer.
                            if (ctx.IsGroup && WarnRemovals.MayBeReasonReply(update.Message, Bot.Me.Id))
                            {
                                var reply = update.Message;
                                Bot.DispatchInteractive(() => WarnRemovals.AnswerFromReply(reply), "WarnReasonReply");
                            }

                            if (text.StartsWith("/") || text.StartsWith("#") ||
                                text.StartsWith("@admin") || text.StartsWith("@pingall"))
                            {
                                Bot.DispatchInteractive(() => ExecuteTextCommands(update), "ExecuteTextCommands");
                            }
                            break;
                        case MessageType.Photo:
                        case MessageType.Animation:
                        case MessageType.Audio:
                        case MessageType.Video:
                        case MessageType.Voice:
                        case MessageType.Document:
                        case MessageType.Sticker:
                        case MessageType.Location:
                        case MessageType.Contact:
                        case MessageType.Venue:
                            if (moderate)
                            {
                                background.Add(OnMessage.CheckMedia(ctx));
                            }
                            break;
                        case MessageType.NewChatMembers:
                            if (update.Message.NewChatMembers != null && update.Message.NewChatMembers.Length > 0)
                            {
                                Bot.DispatchAction(() => HandleNewChatMembers(ctx), "HandleNewChatMembers");
                            }
                            break;
                        case MessageType.Game:
                            break;
                        default:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ErrorReporter.Report(ex, "HandleUpdate");
                }
                finally
                {
                    // Awaiting here is what makes the consumer pool a real concurrency limit -
                    // without it we would be back to unbounded fan-out.
                    try
                    {
                        await Task.WhenAll(background);
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "Message handler");
                    }
                }
            }
        }

        /// <summary>
        /// The blocking command-dispatch path, unchanged apart from logging inline instead of
        /// spawning a task per log line. Runs on the pool via Task.Run.
        /// </summary>
        private static void ExecuteTextCommands(Update update)
        {
            if (update.Message.Text.StartsWith("/"))
            {
                var args = GetParameters(update.Message.Text);
                var address = CommandAddress.Parse(args[0], Bot.Me.Username);
                if (!address.ForMe) return; // /cmd@otherbot
                args[0] = address.Trigger;
                var command = Bot.Commands.FirstOrDefault(
                    x => String.Equals(x.Trigger, args[0], StringComparison.CurrentCultureIgnoreCase));
                if (command != null && !AnswersHere(command, address, update.Message.Chat.Id)) return;
                if (command != null)
                {
                    // Esme (the relay bot) may run the language upload, and nothing else a bot could.
                    var granted = BotSenderFilter.IsGrantedCommand(update.Message.From.Id, command.Trigger);
                    // Every known command passes the limiter; unknown ones cost nothing and do nothing.
                    if (!CommandGate.Admit(update.Message))
                    {
                        return;
                    }
                    if (command.DevOnly && !Constants.Devs.Contains(update.Message.From.Id))
                    {
                        return;
                    }
                    if (command.GroupAdminOnly && !Methods.IsGroupAdmin(update) &
                        !Methods.IsGlobalAdmin(update.Message.From.Id) & !Constants.Devs.Contains(update.Message.From.Id))
                    {
                        Bot.SendReply(
                            Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message, true).Doc,
                                "userNotAdmin"), update.Message);
                        return;
                    }
                    if (Constants.Devs.Contains(update.Message.From.Id) & (command.GroupAdminOnly | command.DevOnly))
                    {
                        Service.LogDevCommand(update, update.Message.Text);
                    }

                    if (command.InGroupOnly && update.Message.Chat.Type == ChatType.Private)
                    {
                        return;
                    }
                    if (command.RequiresReply && update.Message.ReplyToMessage == null)
                    {
                        var lang = Methods.GetGroupLanguage(update.Message, true);
                        Bot.SendReply(Methods.GetLocaleString(lang.Doc, "noReply"), update);
                        return;
                    }
                    if (command.UploadAdmin && !granted && !Methods.IsLangAdmin(update.Message.From.Id))
                    {
                        return;
                    }
                    if (command.GlobalAdminOnly && !Methods.IsGlobalAdmin(update.Message.From.Id))
                    {
                        return;
                    }
                    Interlocked.Increment(ref Bot.CommandsReceived);
                    command.Method.Invoke(update, args);
                    Log(update, "text", command);
                }
            }
            else if (update.Message.Text.StartsWith("#"))
            {
                string[] args = new string[1];
                args[0] = update.Message.Text;
                if (update.Message.Chat.Type == ChatType.Private)
                {
                    return;
                }
                if (!CommandGate.Admit(update.Message))
                {
                    return;
                }
                Log(update, "extra");
                Commands.SendExtra(update, args);
            }
            else if (update.Message.Text.StartsWith("@admin") | update.Message.Text.StartsWith("@pingall"))
            {
                var args = GetParameters(update.Message.Text);
                var address = CommandAddress.Parse(args[0], Bot.Me.Username);
                if (!address.ForMe) return;
                args[0] = address.Trigger;
                var command = Bot.Commands.FirstOrDefault(
                    x => String.Equals(x.Trigger, args[0], StringComparison.CurrentCultureIgnoreCase));
                if (command != null)
                {
                    Log(update, "text", command);
                    // A burst of reports is a sign of a raid, whether or not this one gets through.
                    if (update.Message.Chat.Type != ChatType.Private)
                        Raids.RaidGuard.OnReport(update.Message.Chat.Id);
                    if (!CommandGate.Admit(update.Message))
                    {
                        return;
                    }
                    if (command.DevOnly & !Constants.Devs.Contains((long)update.Message.From.Id))
                    {
                        return;
                    }
                    if (command.GroupAdminOnly & !Methods.IsGroupAdmin(update) &
                        !Methods.IsGlobalAdmin(update.Message.From.Id))
                    {
                        Bot.SendReply(
                            Methods.GetLocaleString(Methods.GetGroupLanguage(update.Message, true).Doc,
                                "userNotAdmin"), update.Message);
                        return;
                    }
                    if (command.InGroupOnly & update.Message.Chat.Type == ChatType.Private)
                    {
                        return;
                    }
                    if (command.RequiresReply & update.Message.ReplyToMessage == null)
                    {
                        var lang = Methods.GetGroupLanguage(update.Message, true);
                        Bot.SendReply(Methods.GetLocaleString(lang.Doc, "noReply"), update);
                        return;
                    }
                    if (command.GlobalAdminOnly & !Methods.IsGlobalAdmin(update.Message.From.Id))
                    {
                        return;
                    }
                    Interlocked.Increment(ref Bot.CommandsReceived);
                    command.Method.Invoke(update, args);
                }
            }
        }

        /// <summary>Join handling. Blocking throughout, so it runs on the bounded action pool.</summary>
        private static void HandleNewChatMembers(MessageContext ctx)
        {
            var update = ctx.Update;
            try
            {
                // No longer skipped for someone throttled for command spam: that skipped Mute On Join
                // and the join checks for exactly the accounts most likely to be spammers.
                Log(update, "chatMember");
                var chatId = update.Message.Chat.Id;
                var joiners = update.Message.NewChatMembers ?? Array.Empty<User>();
                foreach (var joiner in joiners) ForgetTempban(chatId, joiner.Id);
                Detection.FilterCheck.OnJoin(ctx);
                Detection.MessageSigns.OnJoin(ctx);
                if (joiners.Any(j => j.Id == Bot.Me.Id))
                {
                    Service.BotAdded(update.Message);
                }
                else
                {
                    Service.Welcome(update.Message);
                    // Everyone added in one go, not just the first: a mass add is how a raid gets
                    // round Mute On Join.
                    var muteOnJoin = (ctx.Policy ?? Detection.DetectionSettings.For(chatId)).MuteOnJoin;
                    var lang = ctx.Lang ?? Methods.GetGroupLanguage(chatId).Doc;
                    if (muteOnJoin)
                    {
                        foreach (var joiner in joiners)
                            Methods.MuteUser(chatId, joiner.Id, lang, newJoiner: true);
                    }
                }
#if PREMIUM
                if ((update.Message.Chat.Id == -1001060486754 | update.Message.Chat.Id == -1001030085238) && update.Message.NewChatMembers.Length > 1)
                {
                    for (int i = 0; i < update.Message.NewChatMembers.Length; i++)
                    {
                        Detection.DetectionEngine.Checked(chatId, update.Message.NewChatMembers[i].Id, null, Detection.SignCatalog.MassAdd,
                            "tempban", evidence: new { added = update.Message.NewChatMembers.Length, by = update.Message.From.Id });
                        try
                        {
                            // No pause between them: the enforcement queue and the gateway pace the calls.
                            bool res = Commands.Tempban(update.Message.NewChatMembers[i].Id, update.Message.Chat.Id, 60, message: $"User: {update.Message.NewChatMembers[i].Id} has been tempbanned for an hour as they were added by {update.Message.From.Id}", reason: "Added in a mass add");
                        }
                        catch (Exception e)
                        {
                            ErrorReporter.Report(e, "Tempbanning a mass-added member");
                        }
                    }
                    Detection.DetectionEngine.Checked(chatId, update.Message.From.Id, update.Message, Detection.SignCatalog.MassAdd,
                        "tempban", evidence: new { added = update.Message.NewChatMembers.Length, adder = true });
                    try
                    {
                        bool res = Commands.Tempban(update.Message.From.Id, update.Message.Chat.Id, 120, message: $"User: {update.Message.From.Id} has been tempbanned for 2 hours as they added to many members", reason: "Mass-added members");
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "Tempbanning a mass-adder");
                    }
                }
#endif
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "NewChatMembers");
            }
        }

        /// <summary>
        /// Someone tempbanned here is back (an admin let them in early): their entry in the expiry
        /// schedule goes, so the expiry check does not act on them later. The schedule is keyed by the
        /// unban time, which two tempbans can share, so only an entry that names this chat and user is
        /// removed.
        /// </summary>
        private static void ForgetTempban(long chatId, long userId)
        {
#if PREMIUM
            const string index = "tempbannedPremium";
#else
            const string index = "tempbanned";
#endif
            var until = Redis.db.StringGetAsync($"chat:{chatId}:tempbanned:{userId}").Result;
            if (!until.HasValue) return;
            var entry = Redis.db.HashGetAsync(index, until.ToString()).Result;
            if (entry.HasValue && Data.RedisTempbanRepository.TryParseEntry(entry.ToString(), out var entryChat, out var entryUser) &&
                entryChat == chatId && entryUser == userId)
                Redis.db.HashDeleteAsync(index, until.ToString());
        }

        // One-time schema migrations. These were re-checked against Redis on every group message
        // forever - four blocking round-trips per message. Once a chat has been seen in this
        // process the check is skipped entirely.
        private static readonly ConcurrentDictionary<long, byte> _migratedSettings = new ConcurrentDictionary<long, byte>();
        private static readonly ConcurrentDictionary<long, byte> _migratedSettings2 = new ConcurrentDictionary<long, byte>();
        private static readonly ConcurrentDictionary<long, byte> _migratedSettings3 = new ConcurrentDictionary<long, byte>();
        private static readonly ConcurrentDictionary<string, byte> _migratedWarn0 = new ConcurrentDictionary<string, byte>();

        // dbUpdate:lenghtUpdat4 is keyed per chat *and* user, so its cache is the one that can
        // grow without bound. Past the cap we simply keep asking Redis rather than leak. Tracked
        // with a counter because ConcurrentDictionary.Count takes every bucket lock.
        private const int MigratedWarnCacheLimit = 200000;
        private static int _migratedWarnCount;

        private static async Task RunPendingMigrations(MessageContext ctx)
        {
            var chatId = ctx.ChatId;
            var warnKey = $"{chatId}:{ctx.UserId}";

            var needSettings = !_migratedSettings.ContainsKey(chatId);
            var needSettings2 = !_migratedSettings2.ContainsKey(chatId);
            var needSettings3 = !_migratedSettings3.ContainsKey(chatId);
            var needWarn0 = !_migratedWarn0.ContainsKey(warnKey);
            if (!needSettings && !needSettings2 && !needSettings3 && !needWarn0) return;

            var db = Redis.db;
            var doneTask = needSettings ? db.SetContainsAsync("lenghtUpdate3", chatId) : Task.FromResult(true);
            var done2Task = needSettings2 ? db.SetContainsAsync("lenghtUpdate", chatId) : Task.FromResult(true);
            var done3Task = needSettings3 ? db.SetContainsAsync("dbUpdate:lenghtUpdate", chatId) : Task.FromResult(true);
            var done4Task = needWarn0 ? db.SetContainsAsync("dbUpdate:lenghtUpdat4", warnKey) : Task.FromResult(true);
            await Task.WhenAll(doneTask, done2Task, done3Task, done4Task);

            if (needSettings)
            {
                if (!doneTask.Result)
                {
                    Service.NewSettings(chatId);
                    db.SetAddAsync("lenghtUpdate3", chatId);
                }
                _migratedSettings[chatId] = 0;
            }
            if (needSettings2)
            {
                if (!done2Task.Result)
                {
                    Service.NewSetting2(chatId);
                    db.SetAddAsync("lenghtUpdate", chatId);
                }
                _migratedSettings2[chatId] = 0;
            }
            if (needSettings3)
            {
                if (!done3Task.Result)
                {
                    Service.NewSetting3(chatId);
                    db.SetAddAsync("dbUpdate:lenghtUpdate", chatId);
                }
                _migratedSettings3[chatId] = 0;
            }
            if (needWarn0)
            {
                if (!done4Task.Result)
                {
                    // Two blocking reads inside, so it must not run on a consumer continuation.
                    // The flag is set from within, after the work: marking it here would leave the
                    // migration permanently "done" if the dispatched action never ran.
                    var userId = ctx.UserId;
                    Bot.DispatchAction(() =>
                    {
                        Service.removeWarn0(chatId, userId);
                        db.SetAddAsync("dbUpdate:lenghtUpdat4", warnKey);
                    }, "removeWarn0");
                }
                if (Volatile.Read(ref _migratedWarnCount) < MigratedWarnCacheLimit &&
                    _migratedWarn0.TryAdd(warnKey, 0))
                {
                    Interlocked.Increment(ref _migratedWarnCount);
                }
            }
        }

        private static async Task CollectStats(MessageContext ctx)
        {
            var updateMessage = ctx.Message;
            try
            {
                //Console.WriteLine("Collecting Stats");
                Redis.db.HashIncrementAsync("bot:general", "messages");
                Redis.db.HashSetAsync($"user:{updateMessage.From.Id}", "name", updateMessage.From.FirstName);            
                if (updateMessage?.From?.Username != null)
                {
                    Redis.db.HashSetAsync("bot:usernames", $"@{updateMessage.From.Username.ToLower()}", updateMessage.From.Id);                                   
                    Redis.db.HashSetAsync($"user:{updateMessage.From.Id}", "username", $"@{updateMessage.From.Username.ToLower()}");
                }
                if (updateMessage?.ForwardFrom?.Username != null)
                {
                    Redis.db.HashSetAsync("bot:usernames", $"@{updateMessage.ForwardFrom.Username.ToLower()}", updateMessage.ForwardFrom.Id);                    
                    Redis.db.HashSetAsync($"user:{updateMessage.ForwardFrom.Id}", "username", $"@{updateMessage.ForwardFrom.Username.ToLower()}");
                }
                if (updateMessage?.Chat.Type != ChatType.Private)
                {
                    Redis.db.HashSetAsync($"chat:{updateMessage.Chat.Id}:details", "name", updateMessage.Chat.Title);
                    if (updateMessage?.From != null)
                    {
                        Redis.db.HashIncrementAsync($"chat:{updateMessage.From.Id}", "msgs");
                        // Messages they wrote here: a join or leave is not one (it made every joiner
                        // one message closer to established).
                        if (updateMessage.NewChatMembers == null && updateMessage.LeftChatMember == null)
                        {
                            var member = $"{updateMessage.Chat.Id}:users:{updateMessage.From.Id}";
                            var count = await Redis.db.HashIncrementAsync(member, "msgs");
                            // When they first talked here, for the trust levels. Only known for members
                            // whose first message comes after this was added; for the others, their
                            // count says they are not new.
                            if (count == 1)
                            {
                                _ = Redis.db.HashSetAsync(member, "first", Clock.UtcNow.ToUnixTime(), When.NotExists);
                                // Media as a first post, a first post soon after joining (Detection step 9).
                                if (ctx.Complete) Detection.MessageSigns.OnFirstPost(ctx);
                            }
                            RaidCapture.Message(updateMessage, count);
                        }
                        Redis.db.HashSetAsync($"chat:{updateMessage.Chat.Id}:userlast", updateMessage.From.Id, System.DateTime.Now.Ticks);
                        Redis.db.StringSetAsync($"chat:{updateMessage.Chat.Id}:chatlast", DateTime.Now.Ticks);
                    }
                    await RunPendingMigrations(ctx);
                }
            }
            catch (Exception e)
            {
                // Never Bot.Send from here: this runs per message, so a systematic fault used to
                // produce one send per message, hit 429, and sleep pool threads for a minute.
                // ErrorReporter is safe: it is throttled and never blocks.
                ErrorReporter.Report(e, "CollectStats");
            }
        }
           
        /// <summary>
        /// Whether this edition answers <paramref name="command"/> here. A plain /uploadlanguage in the
        /// translators group reaches both bots if both are in it; only premium answers, so one file is not
        /// reviewed twice. Addressed to a bot (/uploadlanguage@enforcerbot), that bot answers.
        /// </summary>
        internal static bool AnswersHere(Models.Commands command, CommandAddress address, long chatId, bool premium = IsPremium) =>
            premium || address.Addressed || chatId != Constants.TranslatorsId ||
            !string.Equals(command.Trigger, "uploadlanguage", StringComparison.OrdinalIgnoreCase);

#if PREMIUM
        private const bool IsPremium = true;
#else
        private const bool IsPremium = false;
#endif

        private static string[] GetParameters(string input)
        {
            if (input.Length == 0)
            {
                return new string[] {"", null};
            }
            return input.Contains(" ") ? new[] { input.Substring(1, input.IndexOf(" ")).Trim(), input.Substring(input.IndexOf(" ") + 1) } : new[] { input.Substring(1).Trim(), null };
        }
        private static string[] GetInlineParameters(string input)
        {
            if (input.Length == 0)
            {
                return new string[] { "", null };
            }
            return input.Contains(" ") ? new[] { input.Substring(0, input.IndexOf(" ")).Trim(), input.Substring(input.IndexOf(" ") + 1) } : new[] { input, null };
        }
        private static string[] GetCallbackParameters(string input)
        {
            return input.Split(':');
        }

        internal static Message Send(string message, long id,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html)
        {
            return Bot.Send(message, id, customMenu, parseMode);
        }

        internal static void HandleInlineQuery(InlineQuery q)
        {
            try
            {
                var query = q.Query;
                var com = GetInlineParameters(query);
                var results = new List<InlineQueryResultArticle>();
                var userLang = Methods.GetGroupLanguage(q.From).Doc;
                
                Models.Queries matchedTrigger = null;
                if(!string.IsNullOrEmpty(com[0]))
                {
                    try
                    {
                        matchedTrigger = Bot.Queries.First(x => x.Trigger.Contains(com[0]));
                    }
                    catch (InvalidOperationException)
                    {
                        // No inline query matches; handled as an unknown query below.
                    }                   
                }
                if (matchedTrigger == null)
                    matchedTrigger = new Models.Queries()
                    {
                        Trigger = ""
                    };
               var optionDictionary = new Dictionary<string, string>();
                Log(q, query, matchedTrigger);
                if (string.IsNullOrEmpty(matchedTrigger.Trigger) && !string.IsNullOrEmpty(com[0]))
                {
                    var helpArticles = InlineMethods.GetHelpArticles(com[0], userLang);
                    foreach (var help in helpArticles)
                    {
                        var des = new string(help.details.Take(50).ToArray());
                        results.Add(new InlineQueryResultArticle
                        {
                            Description = $"{des}...",
                            Title = $"{help.name}",
                            Id = help.name,
                            InputMessageContent = new InputTextMessageContent
                            {
                                LinkPreviewOptions = new LinkPreviewOptions { IsDisabled = true },
                                MessageText = help.details,
                                ParseMode = ParseMode.Html
                            }
                        });
                    }
                }
                else
                {

                    if (!string.IsNullOrEmpty(matchedTrigger.Trigger))
                    {
                        results = matchedTrigger.Method.Invoke(q.From, com, userLang);
                    }
                    else
                    {
                        var choices = Bot.Queries.Where(x => x.Trigger.ToLower().Contains(query.ToLower()));
                        foreach (var choice in choices)
                        {
                            results.Add(new InlineQueryResultArticle()
                            {
                                Description = Methods.GetLocaleString(userLang, $"{choice.Trigger}Description"),
                                Title = $"{choice.Title}",
                                Id = choice.Trigger,
                                InputMessageContent = new InputTextMessageContent()
                                {
                                    LinkPreviewOptions = new LinkPreviewOptions { IsDisabled = true },
                                    MessageText = Methods.GetLocaleString(userLang, "typeMore"),
                                    ParseMode = ParseMode.None
                                }
                            });
                        }
                    }
                }
                var menu = results.Take(50).Cast<InlineQueryResult>().ToArray();
                Bot.Api.AnswerInlineQuery(q.Id, menu, 0).Wait();

            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Inline query");
            }
        }

        // Not async void: it never awaited anything, and an escaping exception would have taken the
        // process down. Runs on the pool via RouteAsync.
        public static void HandleCallback(CallbackQuery update)
        {
            var callback = update.Data;
            if (!string.IsNullOrEmpty(callback))
            {

                try
                {
                    //if ((update.Message?.Date ?? DateTime.MinValue) < Bot.StartTime.AddMinutes(-20))
                    //    return; //toss it
                    var args = GetCallbackParameters(update.Data);
                    args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                    //check for the command
                    // Console.WriteLine("Looking for command");
                    var callbacks = Bot.CallBacks.FirstOrDefault(
                        x =>
                            String.Equals(x.Trigger, args[0],
                                StringComparison.CurrentCultureIgnoreCase));
                    if (callbacks != null)
                    {                                               
                        if (!CommandGate.Admit(update))
                        {
                            return;
                        }
                        if (callbacks.DevOnly & !Constants.Devs.Contains(update.From.Id))
                        {
                            return;
                        }
                        if (callbacks.UploadAdmin & !Methods.IsLangAdmin(update.From.Id))
                        {
                            return;
                        }
                        if (args.Length >= 2)
                        {
                            if (!string.IsNullOrEmpty(args[1]))
                            {
                                // &&: the second part is only for buttons whose data starts with a chat id.
                                if (callbacks.GroupAdminOnly &&
                                    !Methods.IsGroupAdmin(update.From.Id, long.Parse(args[1])))
                                {
                                    
                                    return;
                                }
                                if (callbacks.GroupAdminOnly)
                                {
                                    Service.LogCallback(long.Parse(args[1]), update.From, update.Data, update.Message);
                                }
                            }
                        }
                        if (callbacks.InGroupOnly & update.Message.Chat.Type == ChatType.Private)
                        {
                            return;
                        }
                        Interlocked.Increment(ref Bot.CommandsReceived);
                        Log(update, callbacks);
                        try
                        {
                            callbacks.Method.Invoke(update, args);
                        }
                        catch (Exception e)
                        {
                            ErrorReporter.Report(e, $"Callback {callbacks.Trigger}");
                        }                         
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Callback");
                }
            }
        }
    }
}



