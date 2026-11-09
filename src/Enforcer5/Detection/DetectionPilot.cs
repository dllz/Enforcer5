using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// While Detection's bugs are worked through (Daniel, 2026-11-02), it works only in the groups Daniel is a member
    /// of: there the signs score, record, act and report, and the trust learning runs. Elsewhere the new signs do
    /// nothing and are not even looked for, and nothing is recorded. What groups already relied on before Detection
    /// stays everywhere: the old checks (flood, length, media, channel posts), the filters (admins' own rules and
    /// the presets that replaced the Arabic and RTL checks, with their trials and reports), /block, and BotGuard.
    /// <para>
    /// Membership is asked of Telegram (getChatMember) and believed for <see cref="FreshFor"/>, and changed at once
    /// when a member update says Daniel joined or left. <c>DetectionEverywhere=true</c> (env or appsettings) ends
    /// the pilot without a release.
    /// </para>
    /// </summary>
    internal static class DetectionPilot
    {
        /// <summary>Daniel's account: Detection works where he is a member.</summary>
        internal const long Owner = 125311351;

        internal static TimeSpan FreshFor = TimeSpan.FromHours(6);
        internal static TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

        /// <summary>Detection in every group, as before the pilot. Tests set it; production reads <c>DetectionEverywhere</c>.</summary>
        internal static bool Everywhere = string.Equals(RegHelper.GetRegValue("DetectionEverywhere"), "true", StringComparison.OrdinalIgnoreCase);

        private sealed record Known(bool In, DateTime FreshUntil);

        private static readonly ConcurrentDictionary<long, Known> Groups = new();
        private static readonly ConcurrentDictionary<long, Lazy<Task<bool>>> Asking = new();

        /// <summary>
        /// Whether Detection works in <paramref name="chatId"/>, without waiting: when it is not known (or no longer
        /// fresh), Telegram is asked in the background and the last answer (no, at first) is used meanwhile.
        /// </summary>
        internal static bool Includes(long chatId)
        {
            if (Everywhere) return true;
            if (chatId > 0) return false;
            Groups.TryGetValue(chatId, out var known);
            if (known == null || known.FreshUntil <= Clock.UtcNow) _ = AskAsync(chatId);
            return known?.In ?? false;
        }

        /// <summary>Whether Detection works in <paramref name="chatId"/>, asking Telegram first when it is not known.</summary>
        internal static async Task<bool> IncludesAsync(long chatId)
        {
            if (Everywhere) return true;
            if (chatId > 0) return false;
            if (Groups.TryGetValue(chatId, out var known) && known.FreshUntil > Clock.UtcNow) return known.In;
            return await AskAsync(chatId);
        }

        /// <summary>A member update about Daniel: believed at once.</summary>
        internal static void Seen(long chatId, ChatMember member)
        {
            if (member?.User?.Id != Owner) return;
            Groups[chatId] = new Known(IsIn(member), Clock.UtcNow + FreshFor);
        }

        private static Task<bool> AskAsync(long chatId) =>
            Asking.GetOrAdd(chatId, id => new Lazy<Task<bool>>(() => LookUpAsync(id))).Value;

        private static async Task<bool> LookUpAsync(long chatId)
        {
            try
            {
                var member = await Bot.Api.GetChatMember(chatId, Owner);
                var isIn = IsIn(member);
                Groups[chatId] = new Known(isIn, Clock.UtcNow + FreshFor);
                return isIn;
            }
            catch (Exception e)
            {
                // Never in the group ("member not found"): not in it. Anything else (the bot was removed, a
                // 429): the last answer stands, asked again shortly; not an error, the next message asks again.
                var api = Bot.AsApiError(e);
                var never = api?.ErrorCode == 400 && api.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
                Groups.TryGetValue(chatId, out var before);
                var isIn = !never && (before?.In ?? false);
                Groups[chatId] = new Known(isIn, Clock.UtcNow + (never ? FreshFor : RetryAfterFailure));
                if (!never) LogHelper.Error($"Detection pilot: asking whether {Owner} is in {chatId}: {e.Message}");
                return isIn;
            }
            finally
            {
                Asking.TryRemove(chatId, out _);
            }
        }

        private static bool IsIn(ChatMember member) => member switch
        {
            ChatMemberOwner or ChatMemberAdministrator or ChatMemberMember => true,
            ChatMemberRestricted r => r.IsMember,
            _ => false
        };

        internal static void ResetForTests(bool everywhere = true)
        {
            Everywhere = everywhere;
            Groups.Clear();
            Asking.Clear();
        }
    }
}
