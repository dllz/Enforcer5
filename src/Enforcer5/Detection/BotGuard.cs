using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Actions;
using Enforcer5.Data;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Detection
{
    /// <summary>What happens to a message from another bot (see <see cref="BotGuard.JudgeAsync"/>).</summary>
    internal enum BotVerdict
    {
        /// <summary>Not looked at: one of ours, an admin of the group, or not in a group.</summary>
        Ignore,
        /// <summary>Checked like a member's message, flood and commands aside.</summary>
        Check,
        /// <summary>Deleted, and the bot banned from the group.</summary>
        Remove
    }

    /// <summary>
    /// Other bots in a group (Daniel, 2026-10-31, after a spam wave in the support group). Two ways in:
    /// <list type="bullet">
    /// <item>a member mentions a bot that has guest mode (Bot API 10.0) and it answers once in the group without
    /// ever joining: nothing in the admin log, and the messages carry who summoned it
    /// (<c>guest_bot_caller_user</c>). Summoned by a member who is not an admin, the answer is deleted, the bot
    /// banned, and the member gets the <c>summoned_bot</c> sign, reported to the admins;</item>
    /// <item>a member adds a bot. Added by an admin it is trusted; by anyone else it is banned at once, and the
    /// member gets the <c>added_bot</c> sign. Every bot added is logged in the log channel, with who added it.</item>
    /// </list>
    /// Bots that were in a group before (no record of their join) are let be, as are the bots of the game
    /// (<see cref="KnownBots"/>) and the group's admin bots. Every other bot's message is checked like a member's,
    /// flood aside: a bot that breaks the group's rules is dealt with as a member would be. Admins trust a bot
    /// with /trustbot and remove one with /untrustbot.
    /// <para>
    /// PostgreSQL holds the decisions (<c>group_bots</c>) and who added or summoned which bot (<c>bot_events</c>);
    /// memory answers, reloaded every <see cref="ReloadEvery"/> for the other edition's changes.
    /// </para>
    /// </summary>
    internal static class BotGuard
    {
        internal static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(5);

        /// <summary>Both a service message and a member update tell of one bot added; the second is the same add.</summary>
        internal static readonly TimeSpan SameJoin = TimeSpan.FromMinutes(2);

        /// <summary>A bot removed is banned and logged once; its later messages are only deleted.</summary>
        internal static readonly TimeSpan RemovedFor = TimeSpan.FromHours(24);

        /// <summary>The game's own bots, trusted in every group, by username (Daniel, 2026-10-31).</summary>
        internal static readonly string[] KnownBots = { "werewolfbot", "blackwerewolfbot" };

        private const long GroupAnonymousBot = 1087968824;

        private static volatile ConcurrentDictionary<(long Chat, long Bot), GroupBotRecord> _records = new();
        private static readonly ConcurrentDictionary<(long Chat, long Bot), DateTime> Removed = new();
        private static readonly ConcurrentDictionary<(long Chat, long Bot), DateTime> Joins = new();
        // A post that summoned several bots is one sign for its sender, not one per bot.
        private static readonly ConcurrentDictionary<(long Chat, long Message), DateTime> Summonings = new();

        internal static GroupBotRecord Get(long chatId, long botId) =>
            _records.TryGetValue((chatId, botId), out var record) ? record : null;

        /// <summary>The bots a group decided about, allowed ones first.</summary>
        internal static IReadOnlyList<GroupBotRecord> ForChat(long chatId) =>
            _records.Values.Where(r => r.ChatId == chatId).OrderBy(r => r.IsAllowed ? 0 : 1).ThenBy(r => r.SetAt).ToList();

        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await ReloadAsync();
                    await Clock.WaitAsync(ReloadEvery, ct);
                }
            }, stop, "BotGuard");

        /// <summary>Reads every group's decisions again; keeps what memory has when PostgreSQL does not answer.</summary>
        internal static async Task ReloadAsync()
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                var all = await Repositories.GroupBots.AllAsync();
                _records = new ConcurrentDictionary<(long, long), GroupBotRecord>(all.ToDictionary(r => (r.ChatId, r.BotId)));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Loading the groups' bots");
            }
        }

        /// <summary>One of ours: this bot, the bots every edition trusts, and the game's bots.</summary>
        internal static bool IsKnown(User bot) =>
            bot != null && (bot.Id == Bot.Me?.Id || BotSenderFilter.TrustedBotIds.Contains(bot.Id) ||
                            bot.Username != null && KnownBots.Contains(bot.Username, StringComparer.OrdinalIgnoreCase));

        /// <summary>A guest bot's answer: it was mentioned and replied without being in the group.</summary>
        internal static bool IsGuestReply(Message message) =>
            message.GuestBotCallerUser != null || message.GuestBotCallerChat != null || !string.IsNullOrEmpty(message.GuestQueryId);

        /// <summary>
        /// Who summoned the bot: Telegram's caller, or, from a Bot API server that does not mark guest answers,
        /// the person whose message it answers. Null when nobody can be seen (a channel).
        /// </summary>
        internal static User Caller(Message message) =>
            message.GuestBotCallerUser ??
            (message.GuestBotCallerChat == null && message.ReplyToMessage?.From is { IsBot: false } from ? from : null);

        /// <summary>What to do with <paramref name="message"/>, from a bot that is not trusted everywhere.</summary>
        internal static async Task<BotVerdict> JudgeAsync(Message message)
        {
            var bot = message.From;
            if (bot == null || !bot.IsBot || IsKnown(bot)) return BotVerdict.Ignore;
            var chatId = message.Chat.Id;
            if (message.Chat.Type is not (ChatType.Group or ChatType.Supergroup)) return BotVerdict.Ignore;
            if (AdminChats.IsAdminChat(chatId)) return BotVerdict.Ignore;

            var record = Get(chatId, bot.Id);
            if (record != null && !record.IsAllowed) return BotVerdict.Remove;
            if (await GroupBots.IsAdminAsync(chatId, bot.Id)) return BotVerdict.Ignore;
            if (record != null) return BotVerdict.Check;

            if (IsGuestReply(message))
                return await CalledByAdminAsync(message) ? BotVerdict.Check : BotVerdict.Remove;
            // Neither a record nor a guest's marks: a member from before these checks, or a guest our Bot API
            // server did not mark. Telegram not answering counts as a member: never remove on a guess.
            var member = await GroupBots.MembershipAsync(chatId, bot.Id);
            if (member == false)
                return await CalledByAdminAsync(message) ? BotVerdict.Check : BotVerdict.Remove;
            if (member == true)
                Remember(new GroupBotRecord(chatId, bot.Id, GroupBotRecord.Allowed, "present", null, Now), overwrite: false);
            return BotVerdict.Check;
        }

        private static DateTimeOffset Now => new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);

        private static async Task<bool> CalledByAdminAsync(Message message)
        {
            var chatId = message.Chat.Id;
            // Posted as the group itself: an anonymous admin.
            if (message.GuestBotCallerChat?.Id == chatId) return true;
            var caller = Caller(message);
            if (caller == null) return false;
            if (caller.Id == GroupAnonymousBot) return true;
            return await AdminDirectory.IsAdminAsync(chatId, caller.Id) == true;
        }

        /// <summary>
        /// Deletes a message the bot may not post here and, the first time, bans the bot and says so in the log
        /// channel. A guest summoned by a member: that member gets the <c>summoned_bot</c> sign.
        /// </summary>
        internal static void Remove(Message message)
        {
            var chatId = message.Chat.Id;
            var bot = message.From;
            var record = Get(chatId, bot.Id);
            var summoned = record == null || record.Source == "summoned";
            var caller = summoned ? Caller(message) : null;
            var now = Clock.UtcNow;
            var first = TryClaimRemoval(chatId, bot.Id, now);

            if (summoned && first)
            {
                Remember(new GroupBotRecord(chatId, bot.Id, GroupBotRecord.Blocked, "summoned", caller?.Id, Now), overwrite: false);
                RecordEvent(chatId, bot.Id, caller?.Id, false, "summoned", "banned");
            }
            Bot.DispatchAction(() =>
            {
                try
                {
                    EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, message.MessageId)).GetAwaiter().GetResult();
                    if (!first) return;
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    Methods.BanUser(chatId, bot.Id, lang, summoned ? "bot:summoned" : $"bot:{record.Source}", source: ActionSource.Detection);
                    if (summoned)
                        Log(chatId, Methods.GetLocaleString(lang, "botSummonedLog", Name(caller), BotName(bot)), caller?.Id);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Removing bot {bot.Id}'s message in {chatId}");
                }
            }, "BotGuard.Remove");

            var summoning = caller != null && message.ReplyToMessage?.From?.Id == caller.Id ? message.ReplyToMessage : null;
            if (caller != null && !caller.IsBot && (summoning == null || Claim(Summonings, (chatId, summoning.MessageId), now, RemovedFor)))
            {
                var hit = new SignHit(SignCatalog.SummonedBot.Name, JsonSerializer.Serialize(new { bot = bot.Id }))
                    { Detail = BotName(bot) };
                Bot.Dispatch(async () => await DetectionEngine.HitAsync(chatId, caller.Id, summoning, hit), "BotGuard.Summoned");
            }
        }

        private static bool TryClaimRemoval(long chatId, long botId, DateTime now) => Claim(Removed, (chatId, botId), now, RemovedFor);

        /// <summary>True the first time in <paramref name="keep"/>; the map is pruned as it grows.</summary>
        private static bool Claim(ConcurrentDictionary<(long, long), DateTime> map, (long, long) key, DateTime now, TimeSpan keep)
        {
            // TryAdd/TryUpdate rather than AddOrUpdate, whose factories can run and lose the race: a flag set
            // inside one claimed twice when three guest answers came at once.
            bool claimed;
            while (true)
            {
                if (map.TryAdd(key, now)) { claimed = true; break; }
                if (!map.TryGetValue(key, out var at)) continue;
                if (now - at < keep) { claimed = false; break; }
                if (map.TryUpdate(key, now, at)) { claimed = true; break; }
            }
            if (claimed && map.Count > 50_000)
                foreach (var (old, at) in map)
                    if (now - at >= keep) map.TryRemove(old, out _);
            return claimed;
        }

        /// <summary>
        /// A bot joined: added by <paramref name="addedBy"/> (null when Telegram did not say). Logged in the log
        /// channel whoever added it; trusted when an admin did, banned at once otherwise.
        /// </summary>
        internal static void Joined(long chatId, User bot, User addedBy)
        {
            if (bot == null || !bot.IsBot || IsKnown(bot) || AdminChats.IsAdminChat(chatId)) return;
            if (!Claim(Joins, (chatId, bot.Id), Clock.UtcNow, SameJoin)) return;
            GroupBots.Forget(chatId, bot.Id);

            Bot.Dispatch(async () =>
            {
                try
                {
                    // Both editions see a member update; the one that serves the group acts.
                    if (!await Handlers.UpdateHandler.ServesAsync(chatId)) return;
                    var byAdmin = addedBy != null && (addedBy.Id == GroupAnonymousBot ||
                                                      await AdminDirectory.IsAdminAsync(chatId, addedBy.Id) == true);
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    if (byAdmin)
                    {
                        Remember(new GroupBotRecord(chatId, bot.Id, GroupBotRecord.Allowed, "admin_added", addedBy.Id, Now), overwrite: true);
                        RecordEvent(chatId, bot.Id, addedBy.Id, true, "added", "none");
                        Log(chatId, Methods.GetLocaleString(lang, "botAddedByAdminLog", Name(addedBy), BotName(bot)), addedBy.Id);
                        return;
                    }
                    Remember(new GroupBotRecord(chatId, bot.Id, GroupBotRecord.Blocked, "member_added", addedBy?.Id, Now), overwrite: true);
                    RecordEvent(chatId, bot.Id, addedBy?.Id, false, "added", "banned");
                    TryClaimRemoval(chatId, bot.Id, Clock.UtcNow);
                    Bot.DispatchAction(() =>
                    {
                        try
                        {
                            Methods.BanUser(chatId, bot.Id, lang, "bot:member_added", source: ActionSource.Detection);
                        }
                        catch (Exception e)
                        {
                            ErrorReporter.Report(e, $"Banning bot {bot.Id} a member added to {chatId}");
                        }
                    }, "BotGuard.AddedBan");
                    Log(chatId, Methods.GetLocaleString(lang, "botAddedByMemberLog", Name(addedBy), BotName(bot)), addedBy?.Id);
                    if (addedBy is { IsBot: false })
                        await DetectionEngine.HitAsync(chatId, addedBy.Id, null,
                            new SignHit(SignCatalog.AddedBot.Name, JsonSerializer.Serialize(new { bot = bot.Id })) { Detail = BotName(bot) });
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Handling bot {bot.Id} added to {chatId}");
                }
            }, "BotGuard.Joined");
        }

        /// <summary>An admin trusts the bot: its messages and guest answers are let through, and a ban of ours is lifted.</summary>
        internal static async Task TrustAsync(long chatId, long botId, long adminId)
        {
            var before = Get(chatId, botId);
            await RememberAsync(new GroupBotRecord(chatId, botId, GroupBotRecord.Allowed, "command", adminId, Now), overwrite: true);
            Removed.TryRemove((chatId, botId), out _);
            GroupBots.Forget(chatId, botId);
            await RecordEventAsync(chatId, botId, adminId, true, "trusted", "none");
            if (before != null && !before.IsAllowed)
                Methods.UnbanUser(chatId, botId, Methods.GetGroupLanguage(chatId).Doc, adminId, ActionSource.Command);
        }

        /// <summary>An admin removes the bot: banned, and anything it posts here deleted.</summary>
        internal static async Task UntrustAsync(long chatId, long botId, long adminId)
        {
            await RememberAsync(new GroupBotRecord(chatId, botId, GroupBotRecord.Blocked, "command", adminId, Now), overwrite: true);
            TryClaimRemoval(chatId, botId, Clock.UtcNow);
            GroupBots.Forget(chatId, botId);
            await RecordEventAsync(chatId, botId, adminId, true, "untrusted", "banned");
            Methods.BanUser(chatId, botId, Methods.GetGroupLanguage(chatId).Doc, "bot:untrusted", adminId, source: ActionSource.Command);
        }

        private static void Remember(GroupBotRecord record, bool overwrite) =>
            Bot.Dispatch(() => RememberAsync(record, overwrite), "BotGuard.Remember");

        private static async Task RememberAsync(GroupBotRecord record, bool overwrite)
        {
            var key = (record.ChatId, record.BotId);
            if (overwrite) _records[key] = record;
            else if (!_records.TryAdd(key, record)) return;
            if (!PostgresStorage.Ready) return;
            try
            {
                await Repositories.GroupBots.SetAsync(record, overwrite);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Saving bot {record.BotId} as {record.Status} in {record.ChatId}");
            }
        }

        private static void RecordEvent(long chatId, long botId, long? actorId, bool actorWasAdmin, string kind, string action) =>
            Bot.Dispatch(() => RecordEventAsync(chatId, botId, actorId, actorWasAdmin, kind, action), "BotGuard.Event");

        private static async Task RecordEventAsync(long chatId, long botId, long? actorId, bool actorWasAdmin, string kind, string action)
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                await Repositories.GroupBots.RecordEventAsync(new BotEvent(chatId, botId, actorId, actorWasAdmin, kind, action,
                    BanHistory.Edition, Now));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Recording bot {botId} {kind} in {chatId}");
            }
        }

        private static void Log(long chatId, string text, long? targetId)
        {
            try
            {
                Service.LogBotAction(chatId, text, targetId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Logging a bot in {chatId}");
            }
        }

        private static string Name(User user) => user == null ? "?" : $"{user.FirstName} ({user.Id})";

        private static string BotName(User bot) => bot.Username != null ? $"@{bot.Username}" : $"{bot.FirstName} ({bot.Id})";

        internal static void ResetForTests()
        {
            _records = new ConcurrentDictionary<(long, long), GroupBotRecord>();
            Removed.Clear();
            Joins.Clear();
            Summonings.Clear();
        }
    }
}
