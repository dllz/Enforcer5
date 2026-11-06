using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// What the bot does about its own standing in a group (Daniel, 2026-11-06):
    /// <list type="bullet">
    /// <item>Muted (it may not post) but an admin: it says nothing there and still acts (bans, deletes).</item>
    /// <item>Muted and not an admin: it can do nothing there, so it leaves. It looks again after <see cref="LeaveAfter"/>
    /// (an admin who restricted it by mistake has that long), tells in private those admins who could have fixed it
    /// (<c>botLeavingMuted</c>), and leaves.</item>
    /// <item>Not an admin but may post: extra-only mode (Handlers/UpdateHandler). Nothing is moderated; members'
    /// commands, extras and welcomes work; an admin command gets <c>botNotAdmin</c> at most every
    /// <see cref="NotAdminEvery"/> and is otherwise ignored.</item>
    /// </list>
    /// </summary>
    internal static class MutedGroups
    {
        internal static TimeSpan LeaveAfter = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan NotAdminEvery = TimeSpan.FromHours(3);

        private static readonly ConcurrentDictionary<long, byte> Looking = new();
        private static readonly ConcurrentDictionary<long, DateTime> ToldNotAdmin = new();
        private static int _started;

        /// <summary>Follows BotRights; once per process.</summary>
        internal static void Start()
        {
            if (System.Threading.Interlocked.Exchange(ref _started, 1) == 1) return;
            BotRights.BecameMuted += chat => Bot.Dispatch(() => LeaveIfStillMutedAsync(chat), "MutedGroups");
            BotRights.Removed += chat => Bot.Dispatch(() => RemovedAsync(chat), "MutedGroups");
        }

        /// <summary>
        /// True (and remembered) when the group may be told now that the bot is not an admin there: at most once every
        /// <see cref="NotAdminEvery"/>, for every way it comes up (an admin command, an action Telegram refused).
        /// </summary>
        internal static bool MayTellNotAdmin(long chat)
        {
            var now = Clock.UtcNow;
            var allowed = false;
            ToldNotAdmin.AddOrUpdate(chat, _ => { allowed = true; return now; }, (_, last) =>
            {
                if (now - last < NotAdminEvery) return last;
                allowed = true;
                return now;
            });
            return allowed;
        }

        // Muted: leaves when it is still so after LeaveAfter and the bot is no admin there.
        private static async Task LeaveIfStillMutedAsync(long chat)
        {
            if (!Looking.TryAdd(chat, 0)) return;
            try
            {
                await Clock.Delay(LeaveAfter);
                if (await LookAsync(chat) != Standing.Muted) return;
                var doc = Methods.GetGroupLanguage(chat).Doc;
                var text = Methods.GetLocaleString(doc, "botLeavingMuted", await TitleAsync(chat));
                foreach (var admin in await FixersAsync(chat))
                    await SendPrivateAsync(admin, text);
                LogHelper.Info($"MutedGroups: leaving {chat}, muted and not an admin there");
                await Bot.Api.LeaveChat(chat);
            }
            catch (Exception e) when (TelegramErrors.IsExpected(e))
            {
                // Gone meanwhile: nothing to leave.
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Leaving {chat}, where the bot is muted");
            }
            finally
            {
                Looking.TryRemove(chat, out _);
            }
        }

        // Removed from a linked admin chat: its groups' Detection reports go to their admins in private again,
        // instead of failing there one by one.
        private static async Task RemovedAsync(long chat)
        {
            if (!Detection.AdminChats.IsAdminChat(chat)) return;
            foreach (var group in Detection.AdminChats.GroupsOf(chat).ToList())
            {
                // A premium group's admin chat is the premium edition's to keep (it stays there); the normal edition
                // removed from it changes nothing, and the premium edition speaks only for premium groups.
                var premium = await Roles.ContainsAsync(0, Data.RoleList.Premium, group);
#if PREMIUM
                if (!premium) continue;
#else
                if (premium) continue;
#endif
                try
                {
                    await Detection.DetectionSettings.UpdateAsync(group, p => p.Engine.AdminChatId == chat
                        ? p with { Engine = p.Engine with { AdminChatId = null } }
                        : p);
                    Detection.AdminChats.Unlinked(group);
                    LogHelper.Info($"MutedGroups: the admin chat {chat} of {group} is unlinked, the bot was removed from it");
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Unlinking the admin chat {chat} of {group}");
                }
            }
        }

        // Those who can fix the bot's rights (the owner, admins who may add admins), else every admin.
        private static async Task<IReadOnlyList<long>> FixersAsync(long chat)
        {
            var admins = (await AdminDirectory.GetAsync(chat) ?? Array.Empty<ChatMember>())
                .Where(a => !a.User.IsBot).ToList();
            var fixers = admins.Where(a => a is ChatMemberOwner || a is ChatMemberAdministrator { CanPromoteMembers: true })
                .Select(a => a.User.Id).ToList();
            return fixers.Count > 0 ? fixers : admins.Select(a => a.User.Id).ToList();
        }

        private static async Task SendPrivateAsync(long user, string text)
        {
            try
            {
                await Bot.Api.SendMessage(user, text, ParseMode.Html, linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
            }
            catch (Exception e) when (TelegramErrors.IsExpected(e))
            {
                // Never started the bot, or blocked it: nothing to tell them with.
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Telling {user} the bot is leaving a group");
            }
        }

        private static async Task<string> TitleAsync(long chat) =>
            (await ChatDetails.GetAsync(chat))?.Title ?? chat.ToString();

        internal enum Standing { Unknown, MayPost, Muted, Gone }

        /// <summary>
        /// Asks Telegram about the bot in the group now: an admin (it may post), muted and no admin (its own restriction,
        /// or the group's permissions for members), able to post, or gone.
        /// </summary>
        internal static async Task<Standing> LookAsync(long chat)
        {
            try
            {
                var me = await Bot.Api.GetChatMember(chat, Bot.Me.Id);
                switch (me)
                {
                    case ChatMemberLeft:
                    case ChatMemberBanned:
                        return Standing.Gone;
                    case ChatMemberOwner:
                    case ChatMemberAdministrator:
                        return Standing.MayPost;
                    case ChatMemberRestricted restricted:
                        if (!restricted.IsMember) return Standing.Gone;
                        return restricted.CanSendMessages ? Standing.MayPost : Standing.Muted;
                }
                var info = await Bot.Api.GetChat(chat);
                return info.Permissions?.CanSendMessages == false ? Standing.Muted : Standing.MayPost;
            }
            catch (Exception e) when (TelegramErrors.Classify(e) == TelegramErrorKind.ChatGone)
            {
                return Standing.Gone;
            }
            catch (Exception e)
            {
                LogHelper.Error($"Looking whether the bot may post in {chat}: {e.Message}");
                return Standing.Unknown;
            }
        }

        internal static void ResetForTests()
        {
            Looking.Clear();
            ToldNotAdmin.Clear();
        }
    }
}
