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
    /// Some bots are trusted in every group (Daniel, 2026-11-02): those a dev trusts with /globaltrustbot, and those
    /// an admin of a premium group added or trusted there. They are let be everywhere, unchecked, as the game's
    /// bots are; only a group's own /untrustbot still removes one there. A dev's /globaluntrustbot removes a bot
    /// from every group, premium trust or not (a group's admin bots aside: Telegram would refuse their ban).
    /// </para>
    /// <para>
    /// PostgreSQL holds the decisions (<c>group_bots</c>, <c>global_bots</c>) and who added or summoned which bot (<c>bot_events</c>);
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
        private static volatile ConcurrentDictionary<long, GlobalBotRecord> _global = new();
        // The premium groups, from Redis at each reload, and the bots their admins chose (TrustedByPremium).
        private static volatile HashSet<long> _premiumChats = new();
        private static volatile HashSet<long> _premiumTrusted = new();
        private static readonly ConcurrentDictionary<(long Chat, long Bot), DateTime> Removed = new();
        private static readonly ConcurrentDictionary<(long Chat, long Bot), DateTime> Joins = new();
        // A post that summoned several bots is one sign for its sender, not one per bot.
        private static readonly ConcurrentDictionary<(long Chat, long Message), DateTime> Summonings = new();

        internal static GroupBotRecord Get(long chatId, long botId) =>
            _records.TryGetValue((chatId, botId), out var record) ? record : null;

        /// <summary>
        /// The group's decision that counts now: a ban lifted because the bot was trusted everywhere ("global")
        /// counts only while it still is, and is then as if the group had decided nothing.
        /// </summary>
        private static GroupBotRecord Effective(long chatId, long botId) =>
            Get(chatId, botId) is var record && record?.Source == "global" && !IsTrustedEverywhere(botId) ? null : record;

        /// <summary>The bots a group decided about, allowed ones first.</summary>
        internal static IReadOnlyList<GroupBotRecord> ForChat(long chatId) =>
            _records.Values.Where(r => r.ChatId == chatId).OrderBy(r => r.IsAllowed ? 0 : 1).ThenBy(r => r.SetAt).ToList();

        /// <summary>The bots decided about for every group.</summary>
        internal static IReadOnlyList<GlobalBotRecord> Global() =>
            _global.Values.OrderBy(r => r.IsAllowed ? 0 : 1).ThenBy(r => r.SetAt).ToList();

        /// <summary>The bots an admin of a premium group added or trusted there.</summary>
        internal static IReadOnlyCollection<long> PremiumTrusted() => _premiumTrusted;

        internal static bool IsGloballyBlocked(long botId) => _global.TryGetValue(botId, out var r) && !r.IsAllowed;

        /// <summary>Trusted in every group: by a dev, or by the admins of a premium group, and not blocked by a dev.</summary>
        internal static bool IsTrustedEverywhere(long botId) =>
            _global.TryGetValue(botId, out var r) ? r.IsAllowed : _premiumTrusted.Contains(botId);

        /// <summary>A group's admins removed it themselves: that beats any trust from outside the group.</summary>
        private static bool UntrustedByAdmins(GroupBotRecord record) => record is { IsAllowed: false, Source: "command" };

        /// <summary>An admin chose the bot: added it, or trusted it with /trustbot. Not a bot found already there.</summary>
        private static bool ChosenByAdmins(GroupBotRecord record) =>
            record is { IsAllowed: true, Source: "admin_added" or "command" };

        /// <summary>
        /// An admin chose the bot for this group: as <see cref="ChosenByAdmins"/>, or with Trust under a Detection report
        /// (<c>report</c>). One tap on a report is for the group only: it never makes a premium group's bot trusted
        /// everywhere (Daniel, 2026-11-04).
        /// </summary>
        private static bool ChosenHere(GroupBotRecord record) => ChosenByAdmins(record) || record is { IsAllowed: true, Source: "report" };

        /// <summary>
        /// Whether <paramref name="botId"/> is trusted in the group by a decision (ours, a dev's, the admins'), without a
        /// lookup: a Detection report offers Trust only for a bot this says no to. A bot trusted for being an admin of the
        /// group is not seen here; its posts are not reported anyway.
        /// </summary>
        internal static bool IsChosenHere(long chatId, long botId)
        {
            var record = Effective(chatId, botId);
            if (UntrustedByAdmins(record) || IsGloballyBlocked(botId)) return false;
            return IsTrustedEverywhere(botId) || ChosenHere(record);
        }

        /// <summary>The group's admins removed it with /untrustbot, or a dev blocked it everywhere: a report never offers to trust it.</summary>
        internal static bool IsBlockedHere(long chatId, long botId) => UntrustedByAdmins(Effective(chatId, botId)) || IsGloballyBlocked(botId);

        private static void RecomputePremiumTrusted()
        {
            var premium = _premiumChats;
            _premiumTrusted = _records.Values.Where(r => premium.Contains(r.ChatId) && ChosenByAdmins(r))
                .Select(r => r.BotId).ToHashSet();
        }

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
                var global = await Repositories.GroupBots.AllGlobalAsync();
                _records = new ConcurrentDictionary<(long, long), GroupBotRecord>(all.ToDictionary(r => (r.ChatId, r.BotId)));
                _global = new ConcurrentDictionary<long, GlobalBotRecord>(global.ToDictionary(r => r.BotId));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Loading the groups' bots");
                return;
            }
            try
            {
                var premium = await Redis.db.SetMembersAsync("premiumBot");
                _premiumChats = premium.Select(v => long.TryParse(v.ToString(), out var id) ? id : 0).Where(id => id != 0).ToHashSet();
            }
            catch (Exception e)
            {
                // Keeps the premium groups it had; the next reload tries again.
                ErrorReporter.Report(e, "Loading the premium groups for the bots they trust");
            }
            RecomputePremiumTrusted();
            LiftBansOfTrusted();
        }

        /// <summary>
        /// A bot banned by our own checks in a group (a member added or summoned it) that is now trusted in every
        /// group: the ban is lifted and the group's record says why, in the groups this edition serves.
        /// </summary>
        private static void LiftBansOfTrusted()
        {
            foreach (var record in _records.Values)
            {
                if (record.IsAllowed || record.Source is not ("member_added" or "summoned") || !IsTrustedEverywhere(record.BotId))
                    continue;
                var (chatId, botId) = (record.ChatId, record.BotId);
                long? by = _global.TryGetValue(botId, out var global) ? global.SetBy : null;
                Bot.Dispatch(async () =>
                {
                    try
                    {
                        // The premium edition lifts it in premium groups, the normal one elsewhere: once, by the bot there.
                        if (!await Handlers.UpdateHandler.ServesAsync(chatId)) return;
#if !PREMIUM
                        if (_premiumChats.Contains(chatId)) return;
#endif
                        await RememberAsync(new GroupBotRecord(chatId, botId, GroupBotRecord.Allowed, "global", by, Now), overwrite: true);
                        Removed.TryRemove((chatId, botId), out _);
                        GroupBots.Forget(chatId, botId);
                        await RecordEventAsync(chatId, botId, by, false, "trusted", "none");
                        Bot.DispatchAction(() =>
                        {
                            try
                            {
                                Methods.UnbanUser(chatId, botId, Methods.GetGroupLanguage(chatId).Doc, by, ActionSource.Detection);
                            }
                            catch (Exception e)
                            {
                                ErrorReporter.Report(e, $"Lifting our ban of bot {botId} in {chatId}, trusted everywhere");
                            }
                        }, "BotGuard.LiftBan");
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, $"Lifting our ban of bot {botId} in {chatId}, trusted everywhere");
                    }
                }, "BotGuard.LiftBan");
            }
        }

        /// <summary>A dev trusts the bot in every group (<paramref name="allowed"/>) or removes it from every group.</summary>
        internal static async Task SetGlobalAsync(long botId, bool allowed, long devId)
        {
            var record = new GlobalBotRecord(botId, allowed ? GroupBotRecord.Allowed : GroupBotRecord.Blocked, devId, Now);
            _global[botId] = record;
            if (PostgresStorage.Ready) await Repositories.GroupBots.SetGlobalAsync(record);
            if (allowed)
            {
                foreach (var key in Removed.Keys.Where(k => k.Bot == botId).ToList()) Removed.TryRemove(key, out _);
                LiftBansOfTrusted();
            }
        }

        /// <summary>A dev forgets the decision for every group: each group's own, and premium trust, count again.</summary>
        internal static async Task ForgetGlobalAsync(long botId)
        {
            _global.TryRemove(botId, out _);
            if (PostgresStorage.Ready) await Repositories.GroupBots.RemoveGlobalAsync(botId);
        }

        /// <summary>One of ours: this bot, the bots every edition trusts, and the game's bots.</summary>
        internal static bool IsKnown(User bot) =>
            bot != null && (bot.Id == Bot.Me?.Id || BotSenderFilter.TrustedBotIds.Contains(bot.Id) ||
                            bot.Username != null && KnownBots.Contains(bot.Username, StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Whether the group can vouch for what <paramref name="bot"/> posts on a member's behalf (an inline bot's post, its
        /// later edits): one of ours, trusted in every group, chosen by the group's admins (added or /trustbot), or an admin
        /// of the group. Never when the group's admins removed it or a dev blocked it. A bot merely found in the group does
        /// not count: anyone could post through an inline bot that rewrites its posts.
        /// </summary>
        internal static async Task<bool> IsTrustedHereAsync(long chatId, User bot)
        {
            if (bot == null) return false;
            if (IsKnown(bot)) return true;
            var record = Effective(chatId, bot.Id);
            if (UntrustedByAdmins(record) || IsGloballyBlocked(bot.Id)) return false;
            if (IsTrustedEverywhere(bot.Id) || ChosenHere(record)) return true;
            if (record is { IsAllowed: false }) return false;
            return await GroupBots.IsAdminAsync(chatId, bot.Id);
        }

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

            var record = Effective(chatId, bot.Id);
            if (UntrustedByAdmins(record)) return BotVerdict.Remove;
            if (IsGloballyBlocked(bot.Id))
                return await GroupBots.IsAdminAsync(chatId, bot.Id) ? BotVerdict.Ignore : BotVerdict.Remove;
            if (IsTrustedEverywhere(bot.Id)) return BotVerdict.Ignore;
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
            var record = Effective(chatId, bot.Id);
            var lapsed = record == null && Get(chatId, bot.Id) != null;
            // Blocked by a dev and not by the group itself: no group record, and only a marked guest was summoned.
            var global = IsGloballyBlocked(bot.Id) && record is not { IsAllowed: false };
            var summoned = global ? IsGuestReply(message) : record == null || record.Source == "summoned";
            var caller = summoned ? Caller(message) : null;
            var now = Clock.UtcNow;
            var first = TryClaimRemoval(chatId, bot.Id, now);

            if (summoned && first)
            {
                if (!global)
                    Remember(new GroupBotRecord(chatId, bot.Id, GroupBotRecord.Blocked, "summoned", caller?.Id, Now), overwrite: lapsed);
                RecordEvent(chatId, bot.Id, caller?.Id, false, "summoned", "banned");
            }
            Bot.DispatchAction(() =>
            {
                try
                {
                    EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, message.MessageId)).GetAwaiter().GetResult();
                    if (!first) return;
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    Methods.BanUser(chatId, bot.Id, lang, global ? "bot:global" : summoned ? "bot:summoned" : $"bot:{record.Source}",
                        source: ActionSource.Detection);
                    if (global)
                        Log(chatId, Methods.GetLocaleString(lang, "botGlobalBlockedLog", BotName(bot)), caller?.Id);
                    else if (summoned)
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
                    { Detail = BotName(bot, id => id.ToString()) };
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
                    if (IsGloballyBlocked(bot.Id))
                    {
                        RecordEvent(chatId, bot.Id, addedBy?.Id, byAdmin, "added", "banned");
                        TryClaimRemoval(chatId, bot.Id, Clock.UtcNow);
                        Bot.DispatchAction(() =>
                        {
                            try
                            {
                                Methods.BanUser(chatId, bot.Id, lang, "bot:global", source: ActionSource.Detection);
                            }
                            catch (Exception e)
                            {
                                ErrorReporter.Report(e, $"Banning bot {bot.Id}, blocked everywhere, added to {chatId}");
                            }
                        }, "BotGuard.GlobalBan");
                        Log(chatId, Methods.GetLocaleString(lang, "botGlobalBlockedLog", BotName(bot)), addedBy?.Id);
                        return;
                    }
                    if (byAdmin)
                    {
                        Remember(new GroupBotRecord(chatId, bot.Id, GroupBotRecord.Allowed, "admin_added", addedBy.Id, Now), overwrite: true);
                        RecordEvent(chatId, bot.Id, addedBy.Id, true, "added", "none");
                        Log(chatId, Methods.GetLocaleString(lang, "botAddedByAdminLog", Name(addedBy), BotName(bot)), addedBy.Id);
                        return;
                    }
                    if (IsTrustedEverywhere(bot.Id) && !UntrustedByAdmins(Get(chatId, bot.Id)))
                    {
                        RecordEvent(chatId, bot.Id, addedBy?.Id, false, "added", "none");
                        Log(chatId, Methods.GetLocaleString(lang, "botAddedTrustedLog", Name(addedBy), BotName(bot)), addedBy?.Id);
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
                            new SignHit(SignCatalog.AddedBot.Name, JsonSerializer.Serialize(new { bot = bot.Id })) { Detail = BotName(bot, id => id.ToString()) });
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Handling bot {bot.Id} added to {chatId}");
                }
            }, "BotGuard.Joined");
        }

        /// <summary>An admin trusts the bot: its messages and guest answers are let through, and a ban of ours is lifted.</summary>
        /// <param name="source"><c>command</c> (/trustbot; in a premium group, trusted everywhere) or <c>report</c> (Trust under a
        /// Detection report; this group only).</param>
        internal static async Task TrustAsync(long chatId, long botId, long adminId, string source = "command")
        {
            var before = Get(chatId, botId);
            await RememberAsync(new GroupBotRecord(chatId, botId, GroupBotRecord.Allowed, source, adminId, Now), overwrite: true);
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
            if (_premiumChats.Contains(record.ChatId)) RecomputePremiumTrusted();
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

        // For the log lines, with the id copyable (Ids); the sign's stored Detail takes it plain.
        private static string Name(User user) => user == null ? "?" : $"{user.FirstName} ({Ids.Mark(user.Id)})";

        private static string BotName(User bot) => BotName(bot, Ids.Mark);

        private static string BotName(User bot, Func<long, string> showId) =>
            bot.Username != null ? $"@{bot.Username}" : $"{bot.FirstName} ({showId(bot.Id)})";

        internal static void ResetForTests()
        {
            _records = new ConcurrentDictionary<(long, long), GroupBotRecord>();
            _global = new ConcurrentDictionary<long, GlobalBotRecord>();
            _premiumChats = new HashSet<long>();
            _premiumTrusted = new HashSet<long>();
            Removed.Clear();
            Joins.Clear();
            Summonings.Clear();
        }
    }
}
