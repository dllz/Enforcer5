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
        // Written by the polling threads and read by SpamDetection() concurrently.
        internal static ConcurrentDictionary<long, SpamDetector> UserMessages = new ConcurrentDictionary<long, SpamDetector>();

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
                    Bot.DispatchInteractive(() => HandleCallback(update.CallbackQuery), "HandleCallback");
                    break;
                case UpdateType.InlineQuery:
                    Bot.DispatchInteractive(() => HandleInlineQuery(update.InlineQuery), "HandleInlineQuery");
                    break;
                default:
                    if (update.Message == null) return;
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

                        // Ahead of the type switch, because most inline bot traffic is GIFs and
                        // any type can be sent via a bot. A blocked bot's message, or a sticker from
                        // a blocked pack, is being deleted, so nothing below should act on it; the
                        // finally still awaits the work queued above.
                        if (OnMessage.BlockedInlineBot(ctx) || OnMessage.BlockedStickerSet(ctx)) return;
                    }

                    switch (update.Message.Type)
                    {
                        case MessageType.Unknown:
                            break;
                        case MessageType.Text:
                            if (moderate)
                            {
                                background.Add(OnMessage.ArabDetection(ctx));
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
                                // ArabJoinDetection is started from inside, after the spammers
                                // check, so a rate-limited joiner is skipped exactly as before.
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
                    LogHelper.Error($"HandleUpdate for {update.Message?.Chat.Id} failed: {ex.Message}\n{ex.StackTrace}");
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
                        LogHelper.Error($"Message handler in {update.Message?.Chat.Id} failed: {e.Message}");
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
                args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                var command = Bot.Commands.FirstOrDefault(
                    x => String.Equals(x.Trigger, args[0], StringComparison.CurrentCultureIgnoreCase));
                if (command != null)
                {
                    var blocked = Redis.db.StringGetAsync($"spammers{update.Message.From.Id}").Result;
                    if (blocked.HasValue)
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
                    if (command.UploadAdmin && !Methods.IsLangAdmin(update.Message.From.Id))
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
                var blocked = Redis.db.StringGetAsync($"spammers{update.Message.From.Id}").Result;
                if (blocked.HasValue)
                {
                    return;
                }
                Log(update, "extra");
                Commands.SendExtra(update, args);
            }
            else if (update.Message.Text.StartsWith("@admin") | update.Message.Text.StartsWith("@pingall"))
            {
                var args = GetParameters(update.Message.Text);
                args[0] = args[0].Replace("@" + Bot.Me.Username, "");
                var command = Bot.Commands.FirstOrDefault(
                    x => String.Equals(x.Trigger, args[0], StringComparison.CurrentCultureIgnoreCase));
                if (command != null)
                {
                    Log(update, "text", command);
                    AddCount(update.Message.From.Id, update.Message.Text);
                    var blocked = Redis.db.StringGetAsync($"spammers{update.Message.From.Id}").Result;
                    if (blocked.HasValue)
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
                var blocked = Redis.db.StringGetAsync($"spammers{update.Message.NewChatMember.Id}").Result;
                if (blocked.HasValue)
                {
                    return;
                }
                Log(update, "chatMember");
                var isBanned = Redis.db.StringGetAsync($"chat:{update.Message.Chat.Id}:tempbanned:{update.Message.NewChatMember}").Result;
                if (isBanned.HasValue)
                {
#if NORMAL
                    Redis.db.HashDeleteAsync("tempbanned", isBanned.ToString());
#endif
#if PREMIUM
                    Redis.db.HashDeleteAsync("tempbannedPremium", isBanned.ToString());
#endif
                }
                // Matches the original ordering: only reached once the joiner passed the
                // spammers check above.
                Bot.Dispatch(() => OnMessage.ArabJoinDetection(ctx));
                if (update.Message.NewChatMember.Id == Bot.Me.Id)
                {
                    Service.BotAdded(update.Message);
                }
                else
                {
                    Service.Welcome(update.Message);
                    var hash = $"chat:{update.Message.Chat.Id}:settings";
                    var muteOnJoin = Redis.db.HashGet(hash, "MuteOnJoin");
                    var lang = Methods.GetGroupLanguage(update.Message.Chat.Id).Doc;
                    if (muteOnJoin == "no")
                    {
                        Methods.MuteUser(update.Message.Chat.Id, update.Message.NewChatMember.Id, lang, true);
                    }
                }
#if PREMIUM
                if ((update.Message.Chat.Id == -1001060486754 | update.Message.Chat.Id == -1001030085238) && update.Message.NewChatMembers.Length > 1)
                {
                    for (int i = 0; i < update.Message.NewChatMembers.Length; i++)
                    {
                        try
                        {
                            bool res = Commands.Tempban(update.Message.NewChatMembers[i].Id, update.Message.Chat.Id, 60, message: $"User: {update.Message.NewChatMembers[i].Id} has been tempbanned for an hour as they were added by {update.Message.From.Id}");
                            Thread.Sleep(2000);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                        }
                    }
                    try
                    {
                        bool res = Commands.Tempban(update.Message.From.Id, update.Message.Chat.Id, 120, message: $"User: {update.Message.From.Id} has been tempbanned for 2 hours as they added to many members");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }
#endif
            }
            catch (Exception e)
            {
                LogHelper.Error($"NewChatMembers in {update.Message.Chat.Id} failed: {e.Message}");
            }
        }

        private static void AddCount(long id, string command)
        {
            try
            {
                UserMessages.TryAdd(id, new SpamDetector { Messages = new HashSet<UserMessage>() });
                UserMessages[id].Messages.Add(new UserMessage(command));
            }
            catch (Exception e)
            {
                // ignored
            }
        }

        private static void AddCount(int id, Message m)
        {
            try
            {
                UserMessages.TryAdd(id, new SpamDetector { Messages = new HashSet<UserMessage>() });
               


            }
            catch
            {
                // ignored

            }
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
                        Redis.db.HashIncrementAsync($"{updateMessage.Chat.Id}:users:{updateMessage.From.Id}", "msgs");
                        Redis.db.HashSetAsync($"chat:{updateMessage.Chat.Id}:userlast", updateMessage.From.Id, System.DateTime.Now.Ticks);
                        Redis.db.StringSetAsync($"chat:{updateMessage.Chat.Id}:chatlast", DateTime.Now.Ticks);
                    }
                    await RunPendingMigrations(ctx);
                }
            }
            catch (Exception e)
            {
                // Never Telegram-send from here: this runs per message, so a systematic fault used
                // to produce one send per message, hit 429, and sleep pool threads for a minute.
                LogHelper.Error($"CollectStats for {updateMessage?.Chat.Id} failed: {e.Message}");
            }
        }
           
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

         internal static void SpamDetection()
        {
            while (true)
            {
                try
                {
                    var temp = UserMessages.ToDictionary(entry => entry.Key, entry => entry.Value);
                    //var quickRemove = BlockReplies.ToDictionary(entry => entry.Key, entry => entry.Value);
                    //clone the dictionary
                    foreach (var key in temp.Keys.ToList())
                    {
                        try
                        {
                            //drop older messages (1 minute)
/*                            temp[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddMinutes(-1));
#if NORMAL
                            quickRemove[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddSeconds(-10));
#endif
#if PREMIUM
                            quickRemove[key].Messages.RemoveWhere(x => x.Time < DateTime.Now.AddSeconds(-4));
#endif*/
                            //comment this out - if we remove it, it doesn't keep the warns
                            //if (temp[key].Messages.Count == 0)
                            //{
                            //    temp.Remove(key);
                            //    continue;
                            //}
                            //now count, notify if limit hit
#if NORMAL
                            if (temp[key].Messages.Count() < 5)
                            {
                                temp[key].NotifiedAdmin = false;
                            }
#endif
#if PREMIUM
                            if (temp[key].Messages.Count() < 10)
                            {
                                temp[key].NotifiedAdmin = false;
                            }
#endif
#if NORMAL
                            if (temp[key].Messages.Count() >= 5) // 20 in a minute
                            {
#endif
#if PREMIUM
                            if (temp[key].Messages.Count() >= 10) // 20 in a minute
                            {
#endif
#if NORMAL
                                if (temp[key].Messages.Count < 10)
                                {
#endif
#if PREMIUM
                                if (temp[key].Messages.Count < 15)
                                {
#endif
                                    if (temp[key].NotifiedAdmin == false)
                                    {
                                        try
                                        {
                                            Bot.Send($"Please do not spam me. Next time is automated ban.", key);
                                        }
                                        catch (Exception e)
                                        {
                                            Console.WriteLine(e);
                                            
                                        }
                                        temp[key].NotifiedAdmin = true;
                                    }
                                    //Send($"User {key} has been warned for spamming: {temp[key].Warns}\n{temp[key].Messages.GroupBy(x => x.Command).Aggregate("", (a, b) => a + "\n" + b.Count() + " " + b.Key)}",
                                    //    Para);                                    
                                    continue;
                                }
                                var number = 11;
#if PREMIUM
                                number = 15;
#endif
                        if ((temp[key].Warns >= 3 || temp[key].Messages.Count > number))
                                {
                                    Redis.db.StringSetAsync($"spammers{key}", key, TimeSpan.FromMinutes(10));
                                    Console.WriteLine($"{key} - Banned for 10 minutes");
                                    temp[key].Warns = 1;
                                    temp[key].NotifiedAdmin = false;
                                    try
                                    {
                                        Bot.Send("You have been banned for 10 minutes due to spam", long.Parse(key.ToString()));
                                        // Deliberate pacing for Telegram's rate limits, and it costs
                                        // nothing: SpamDetection owns a dedicated thread, so this
                                        // never holds a thread-pool worker.
                                        Thread.Sleep(10000);
                                        Bot.Send(
                                            $"{long.Parse(key.ToString())}, {Methods.GetName(long.Parse(key.ToString()))}, {Methods.GetUsername(long.Parse(key.ToString()))} has been spam banned for 10 minutes.",
                                            Bot.ErrorChatId);
                                    }
                                    catch (Exception e)
                                    {
                                        Console.WriteLine(e);

                                    }
                                }

                                temp[key].Messages.Clear();
                            }
                        }
                        catch (Exception e)
                        {
                            //Console.WriteLine(e.Message);
                        }
                    }
                }
                catch (Exception e)
                {
                    //Console.WriteLine(e.Message);
                }
                Thread.Sleep(1000);
            }
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
                    catch (Exception e)
                    {
                        //nothing happens                        
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
                Console.WriteLine($"{e.Message}\n{e.StackTrace}");
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
                        var blocked = Redis.db.StringGetAsync($"spammers{update.From.Id}").Result;
                        if (blocked.HasValue)
                        {
                            return;
                            ;
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
                                if (callbacks.GroupAdminOnly &
                                    !Methods.IsGroupAdmin(update.From.Id, long.Parse(args[1])))
                                {
                                    
                                    return;
                                }
                                if (callbacks.GroupAdminOnly)
                                {
                                    Service.LogCallback(long.Parse(args[1]), update.From, update.Data);
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
                            Console.WriteLine($"{e.Message}\n{e.StackTrace}");
                        }                         
                    }
                }
                catch (ApiRequestException e)
                {
                    Console.WriteLine(e);
                }
                catch (AggregateException e)
                {
                    Console.WriteLine(e);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }
    }
}



