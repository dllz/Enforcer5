using System;
using System.Collections.Concurrent;
using System.Linq;
using Enforcer5.Data;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Joins and leaves, from both places Telegram reports them: the "joined" service message (not
    /// sent in large groups, or where the group hides them) and the member update (needs the bot to
    /// be an admin; says which invite link was used and whether a join request was approved). The
    /// same join seen both ways within two minutes is raised once, with what the member update says.
    /// </summary>
    internal static class MemberEvents
    {
        private static readonly TimeSpan SameJoin = TimeSpan.FromMinutes(2);
        private static readonly ConcurrentDictionary<(long Chat, long User), (DateTime At, bool Detailed)> RecentJoins = new();
        private static DateTime _nextPrune;

        /// <summary>A join, raised once per join; raid detection listens.</summary>
        internal static event Action<JoinEvent> Joined;

        /// <summary>
        /// A join already raised without detail turned out to be an approved join request (the member
        /// update came after the service message): raid detection takes it back.
        /// </summary>
        internal static event Action<JoinEvent> Vetted;

        /// <summary>The service message for members joining or leaving.</summary>
        internal static void FromServiceMessage(Message message)
        {
            try
            {
                var at = RaidCapture.Utc(message.Date);
                foreach (var user in message.NewChatMembers ?? Array.Empty<User>())
                {
                    if (user.Id == Bot.Me?.Id) continue;
                    var addedBy = message.From != null && message.From.Id != user.Id ? message.From.Id : (long?)null;
                    Join(new JoinEvent(message.Chat.Id, user.Id, at, addedBy.HasValue ? "added" : "unknown", null, null, addedBy), false);
                }
                if (message.LeftChatMember is { } left && left.Id != Bot.Me?.Id)
                    RaidCapture.Left(message.Chat.Id, left.Id, at);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Reading a join service message");
            }
        }

        /// <summary>A member update (chat_member): joins, leaves, approved join requests.</summary>
        internal static void FromChatMember(ChatMemberUpdated update)
        {
            try
            {
                var user = update.NewChatMember?.User;
                if (user == null || user.Id == Bot.Me?.Id || update.Chat.Type == ChatType.Private) return;
                var at = RaidCapture.Utc(update.Date);
                var wasIn = IsIn(update.OldChatMember);
                var isIn = IsIn(update.NewChatMember);
                // Promoted or demoted: the admin list is right at once, not within the minute.
                if (update.NewChatMember is ChatMemberAdministrator or ChatMemberOwner)
                    AdminDirectory.Promoted(update.Chat.Id, update.NewChatMember);
                else if (update.OldChatMember is ChatMemberAdministrator or ChatMemberOwner)
                {
                    AdminDirectory.Demoted(update.Chat.Id, user.Id);
                    _ = Redis.db.KeyDeleteAsync($"chat:{update.Chat.Id}:adminses:{user.Id}", CommandFlags.FireAndForget);
                }
                // A mute lifted, by us or by hand: the queue no longer takes them as muted.
                if (update.OldChatMember is ChatMemberRestricted && update.NewChatMember is not ChatMemberRestricted)
                    Enforcement.EnforcementQueue.ForgetMute(update.Chat.Id, user.Id);
                if (!wasIn && isIn)
                {
                    var addedBy = update.From != null && update.From.Id != user.Id && !update.ViaJoinRequest ? update.From.Id : (long?)null;
                    var source = update.ViaJoinRequest ? "request"
                        : update.InviteLink != null ? "link"
                        : addedBy.HasValue ? "added"
                        : "unknown";
                    Join(new JoinEvent(update.Chat.Id, user.Id, at, source, update.InviteLink?.InviteLink, update.InviteLink?.Name, addedBy), true);
                }
                else if (wasIn && !isIn)
                {
                    RaidCapture.Left(update.Chat.Id, user.Id, at);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Reading a member update");
            }
        }

        private static bool IsIn(ChatMember member) => member switch
        {
            ChatMemberOwner or ChatMemberAdministrator or ChatMemberMember => true,
            ChatMemberRestricted r => r.IsMember,
            _ => false
        };

        private static void Join(JoinEvent join, bool detailed)
        {
            var now = Clock.UtcNow;
            PruneIfDue(now);
            var key = (join.ChatId, join.UserId);
            var seenBefore = RecentJoins.TryGetValue(key, out var seen) && now - seen.At <= SameJoin;
            RecentJoins[key] = (now, detailed || seenBefore && seen.Detailed);
            // Capture keeps both and merges them itself; raid detection hears each join once.
            RaidCapture.Joined(join.ChatId, join.UserId, join.At, join.Source,
                join.InviteLink == null ? null : new ChatInviteLink { InviteLink = join.InviteLink, Name = join.InviteLinkName },
                join.AddedBy);
            if (seenBefore)
            {
                if (detailed && !seen.Detailed && join.Source == "request")
                {
                    try { Vetted?.Invoke(join); }
                    catch (Exception e) { ErrorReporter.Report(e, "Join listener"); }
                }
                return;
            }
            // When they joined, for the trust levels when PostgreSQL is not there.
            _ = Redis.db.HashSetAsync($"{join.ChatId}:users:{join.UserId}", "joined", join.At.ToUnixTimeSeconds(), When.Always, CommandFlags.FireAndForget);
            try
            {
                Joined?.Invoke(join);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Join listener");
            }
        }

        private static void PruneIfDue(DateTime now)
        {
            if (now < _nextPrune) return;
            _nextPrune = now.AddMinutes(1);
            foreach (var stale in RecentJoins.Where(p => now - p.Value.At > SameJoin).Select(p => p.Key).ToList())
                RecentJoins.TryRemove(stale, out _);
        }

        internal static void ResetForTests() => RecentJoins.Clear();
    }
}
