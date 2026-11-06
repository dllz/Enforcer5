using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Groups the bot may not post in (Daniel, 2026-11-06, as blackwolf does since 2026-11-04):
    /// <list type="bullet">
    /// <item>Someone whose command or button the bot could not answer there is told in private, once a day per group:
    /// an admin how to fix it (<c>botMutedAdmin</c>), anyone else to ask an admin (<c>botMutedMember</c>). Someone who
    /// never started the bot: the group's admins are told instead, once a day.</item>
    /// <item>A group muted for <see cref="AskAfter"/> that someone still uses: Daniel is asked in private, Leave or Keep.
    /// Leave looks again, tells the admins (<c>botLeavingMuted</c>) and leaves; Keep asks again after
    /// <see cref="AskAgainAfter"/>. The bot never leaves on its own.</item>
    /// </list>
    /// Since when is kept in PostgreSQL (<c>bot_mutes</c>), per edition; without it people are told all the same and
    /// nobody is asked.
    /// </summary>
    internal static class MutedGroups
    {
        internal static TimeSpan AskAfter = TimeSpan.FromDays(7);
        internal static TimeSpan AskAgainAfter = TimeSpan.FromDays(30);
        internal static readonly TimeSpan NoticeEvery = TimeSpan.FromDays(1);
        // How often a group's muted state is looked up for the question, at most.
        private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

        internal const string Trigger = "mutedleave";

        private static readonly ConcurrentDictionary<(long Group, long User), DateTime> Told = new();
        private static readonly ConcurrentDictionary<long, DateTime> Checked = new();
        private static int _started;

        private static IBotMuteRepository Store => Repositories.BotMutes;
        private static bool Stored => Storage.PostgresStorage.Ready;

        /// <summary>Follows BotRights; once per process.</summary>
        internal static void Start()
        {
            if (System.Threading.Interlocked.Exchange(ref _started, 1) == 1) return;
            BotRights.BecameMuted += chat => Bot.Dispatch(() => MarkAsync(chat), "MutedGroups");
            BotRights.MayPostAgain += chat => Bot.Dispatch(() => ClearAsync(chat), "MutedGroups");
            BotRights.Removed += chat => Bot.Dispatch(() => RemovedAsync(chat), "MutedGroups");
        }

        // Removed from a group: nothing to ask about it. Removed from a linked admin chat: its groups' Detection
        // reports go to their admins in private again, instead of failing there one by one.
        private static async Task RemovedAsync(long chat)
        {
            await ClearAsync(chat);
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

        private static async Task MarkAsync(long chat)
        {
            if (!Stored) return;
            try { await Store.MarkAsync(chat, Clock.UtcNow); }
            catch (Exception e) { ErrorReporter.Report(e, $"Noting the bot may not post in {chat}"); }
        }

        private static async Task ClearAsync(long chat)
        {
            Checked.TryRemove(chat, out _);
            if (!Stored) return;
            try { await Store.ClearAsync(chat); }
            catch (Exception e) { ErrorReporter.Report(e, $"Noting the bot may post in {chat} again"); }
        }

        /// <summary>
        /// The gateway: a call to <paramref name="chatId"/> was refused (by Telegram, or by BotRights without asking).
        /// When it was a post the bot could not make while answering someone there, that person is told.
        /// </summary>
        internal static void Refused(long chatId, string method, Exception e)
        {
            if (chatId >= 0) return;
            if (TelegramErrors.Classify(e) != TelegramErrorKind.NoSendRights) return;
            var ctx = ErrorReporter.Current;
            if (ctx?.ChatId != chatId || ctx.UserId is not { } user || user <= 0) return;
            if (ctx.Kind is not ("message" or "callback")) return;
            if (!Take(chatId, user)) return;
            Bot.Dispatch(() => TellAsync(chatId, user), "MutedGroups");
        }

        // True (and remembered) when the person, or with 0 the group's admins, may be told now.
        private static bool Take(long chat, long user)
        {
            var now = Clock.UtcNow;
            if (Told.Count > 10000)
                foreach (var old in Told.Where(t => now - t.Value >= NoticeEvery).Select(t => t.Key).ToList())
                    Told.TryRemove(old, out _);
            var allowed = false;
            Told.AddOrUpdate((chat, user), _ => { allowed = true; return now; }, (_, last) =>
            {
                if (now - last < NoticeEvery) return last;
                allowed = true;
                return now;
            });
            return allowed;
        }

        private static async Task TellAsync(long chat, long user)
        {
            var doc = Methods.GetGroupLanguage(chat).Doc;
            var title = await TitleAsync(chat);
            var admin = await AdminDirectory.IsAdminAsync(chat, user) == true;
            if (!await SendPrivateAsync(user, Methods.GetLocaleString(doc, admin ? "botMutedAdmin" : "botMutedMember", title)))
                await TellAdminsAsync(chat);
            await MaybeAskAsync(chat, title);
        }

        /// <summary>
        /// Tells the group's admins in private that the bot may not post there, at most once a day per group: those who
        /// can fix it (the owner, admins who may add admins), else every admin.
        /// </summary>
        internal static async Task TellAdminsAsync(long chat)
        {
            if (!Take(chat, 0)) return;
            var doc = Methods.GetGroupLanguage(chat).Doc;
            var text = Methods.GetLocaleString(doc, "botMutedAdmin", await TitleAsync(chat));
            foreach (var admin in await FixersAsync(chat))
                await SendPrivateAsync(admin, text);
        }

        private static async Task<IReadOnlyList<long>> FixersAsync(long chat)
        {
            var admins = (await AdminDirectory.GetAsync(chat) ?? Array.Empty<ChatMember>())
                .Where(a => !a.User.IsBot).ToList();
            var fixers = admins.Where(a => a is ChatMemberOwner || a is ChatMemberAdministrator { CanPromoteMembers: true })
                .Select(a => a.User.Id).ToList();
            return fixers.Count > 0 ? fixers : admins.Select(a => a.User.Id).ToList();
        }

        private static async Task<bool> SendPrivateAsync(long user, string text)
        {
            try
            {
                await Bot.Api.SendMessage(user, text, ParseMode.Html, linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
                return true;
            }
            catch (Exception e) when (TelegramErrors.IsExpected(e))
            {
                // Never started the bot, or blocked it: nothing to tell them with.
                return false;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Telling {user} the bot may not post");
                return false;
            }
        }

        private static async Task<string> TitleAsync(long chat)
        {
            var update = ErrorReporter.Current?.Update;
            var title = (update?.Message ?? update?.CallbackQuery?.Message)?.Chat is { Id: var id, Title: { } t } && id == chat ? t : null;
            title ??= (await ChatDetails.GetAsync(chat))?.Title;
            return title ?? chat.ToString();
        }

        // Someone still uses a group the bot may not post in: Daniel is asked once it has been so for AskAfter.
        private static async Task MaybeAskAsync(long chat, string title)
        {
            if (!Stored) return;
            var now = Clock.UtcNow;
            if (Checked.TryGetValue(chat, out var last) && now - last < CheckEvery) return;
            Checked[chat] = now;
            try
            {
                if (!await Store.ClaimAskAsync(chat, now, now - AskAfter, now - AskAgainAfter)) return;
                var mute = await Store.GetAsync(chat);
                var days = mute == null ? (int)AskAfter.TotalDays : (int)(now - mute.MutedSince).TotalDays;
                var menu = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("Leave", $"{Trigger}:{chat}:leave"),
                    InlineKeyboardButton.WithCallbackData("Keep", $"{Trigger}:{chat}:keep")
                });
                await Bot.Api.SendMessage(Constants.Devs[0],
                    $"🔇 {BanHistory.Edition}: I may not post in {title.FormatHTML()} ({Ids.Mark(chat)}) for {days} days, " +
                    "and people there still use me. Leave the group?",
                    ParseMode.Html, replyMarkup: menu);
            }
            catch (Exception e) when (TelegramErrors.IsExpected(e))
            {
                // Daniel's private chat is not reachable: asked again in a month.
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Asking whether to leave {chat}");
            }
        }

        /// <summary>Daniel's answer: Leave (after looking again) or Keep.</summary>
        internal static async Task AnswerAsync(CallbackQuery query, long chat, string choice)
        {
            var now = Clock.UtcNow;
            string result;
            if (choice == "keep")
            {
                if (Stored) await Store.KeepAsync(chat, now);
                result = $"Kept. Asked again in {(int)AskAgainAfter.TotalDays} days if it is still muted.";
            }
            else
            {
                switch (await LookAsync(chat))
                {
                    case Standing.Gone:
                        await ClearAsync(chat);
                        result = "Already gone from that group.";
                        break;
                    case Standing.MayPost:
                        await ClearAsync(chat);
                        result = "I may post there again, so I stayed.";
                        break;
                    case Standing.Unknown:
                        result = "Telegram did not answer; try again later.";
                        break;
                    default:
                        var doc = Methods.GetGroupLanguage(chat).Doc;
                        var mute = Stored ? await Store.GetAsync(chat) : null;
                        var days = mute == null ? (int)AskAfter.TotalDays : (int)(now - mute.MutedSince).TotalDays;
                        var text = Methods.GetLocaleString(doc, "botLeavingMuted", await TitleAsync(chat), days);
                        foreach (var admin in await FixersAsync(chat))
                            await SendPrivateAsync(admin, text);
                        try
                        {
                            await Bot.Api.LeaveChat(chat);
                            await ClearAsync(chat);
                            result = "Left the group; its admins were told.";
                        }
                        catch (Exception e) when (TelegramErrors.IsExpected(e))
                        {
                            await ClearAsync(chat);
                            result = "Already gone from that group.";
                        }
                        break;
                }
            }
            try
            {
                await Bot.Api.AnswerCallbackQuery(query.Id);
                if (query.Message != null)
                    await Bot.Api.EditMessageText(query.Message.Chat.Id, query.Message.MessageId,
                        $"{query.Message.Text}\n\n{result}");
            }
            catch (Exception e) when (TelegramErrors.IsExpected(e))
            {
                // A tap Telegram no longer takes, or the question was deleted: the answer is done all the same.
            }
        }

        internal enum Standing { Unknown, MayPost, Muted, Gone }

        /// <summary>Asks Telegram whether the bot may post in the group now: its own member status and the group's permissions.</summary>
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
            Told.Clear();
            Checked.Clear();
        }
    }
}

namespace Enforcer5
{
    public static partial class CallBacks
    {
        /// <summary>Daniel's Leave or Keep on a group the bot has long been muted in (Helpers/MutedGroups).</summary>
        [Callback(Trigger = Helpers.MutedGroups.Trigger, DevOnly = true)]
        public static void MutedGroupLeave(CallbackQuery query, string[] args)
        {
            if (args.Length < 3 || !long.TryParse(args[1], out var chat)) return;
            Helpers.MutedGroups.AnswerAsync(query, chat, args[2]).GetAwaiter().GetResult();
        }
    }
}
