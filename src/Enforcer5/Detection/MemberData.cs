using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// What new members looked like (design section 12, build step 14): features at joining, at the first post and while
    /// they are new, and the channels and websites they pointed at. Collected in memory on the paths that already see
    /// them (joins, the first post, the newcomer signs, the profile lookups, the engine's score) and written with the
    /// detections once a minute, merged per member and group. Never text or names: lengths, scripts, yes/no and counts.
    /// Best effort: dropped while PostgreSQL is not ready, the oldest first when the buffer is full.
    /// </summary>
    internal static class MemberData
    {
        internal const int Capacity = 50_000;

        private static readonly object Lock = new();
        private static Dictionary<(long Chat, long User), SignalDelta> _signals = new();
        private static Dictionary<(long Chat, long User, string Kind, string Value), PointerRecord> _pointers = new();

        private static SignalDelta For(long chatId, long userId)
        {
            if (_signals.TryGetValue((chatId, userId), out var delta)) return delta;
            if (_signals.Count >= Capacity) return null;
            var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
            delta = new SignalDelta { ChatId = chatId, UserId = userId, FirstSeenAt = now, UpdatedAt = now };
            _signals[(chatId, userId)] = delta;
            return delta;
        }

        private static void Change(long chatId, long userId, User person, DateTime at, Action<SignalDelta> change)
        {
            if (!PostgresStorage.Ready || userId == 0 || chatId > 0) return;
            lock (Lock)
            {
                var delta = For(chatId, userId);
                if (delta == null) return;
                if (person != null) Describe(delta, person);
                delta.ActiveHours |= 1 << at.ToUniversalTime().Hour;
                delta.UpdatedAt = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
                change(delta);
            }
        }

        /// <summary>What the account and its name look like, never the name itself.</summary>
        internal static void Describe(SignalDelta delta, User person)
        {
            var name = $"{person.FirstName} {person.LastName}".Trim();
            delta.AccountIdRange = (int)(person.Id / 100_000_000);
            delta.HasUsername = person.Username != null;
            delta.IsPremium = person.IsPremium;
            delta.LanguageCode = person.LanguageCode?.ToLowerInvariant();
            delta.NameScript = MessageFingerprint.ScriptOf(name);
            delta.NameLetters = MessageFingerprint.Normalize(name).Length;
            delta.NameEmoji = name.EnumerateRunes().Any(r => System.Text.Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol);
            delta.NameDigits = name.Any(char.IsDigit);
            delta.NameStaffWord = NameSigns.HasKeyword(person);
            delta.NameLookalikes = FilterPresets.MixesLookalikes(name);
        }

        // ------------------------------------------------------------------ what is seen

        /// <summary>Someone joined (either way Telegram tells of it; merged per member).</summary>
        internal static void Joined(long chatId, User person, DateTimeOffset at, string source, string inviteLinkName, bool addedByMember)
        {
            if (person == null || person.IsBot) return;
            var duringRaid = Raids.RaidGuard.Quiet(chatId);
            Change(chatId, person.Id, person, at.UtcDateTime, d =>
            {
                d.JoinedAt ??= at;
                if (d.JoinSource == null || d.JoinSource == "unknown") d.JoinSource = source;
                d.InviteLinkName ??= inviteLinkName;
                d.AddedByMember ??= addedByMember;
                d.JoinedDuringRaid |= duringRaid;
            });
        }

        /// <summary>Their first message here: its type, and how long after joining (when that is known).</summary>
        internal static void FirstPost(long chatId, Message message, int? secondsAfterJoining)
        {
            var at = message.Date.ToUniversalTime();
            Change(chatId, message.From.Id, message.From, at, d =>
            {
                d.FirstPostAt ??= new DateTimeOffset(at, TimeSpan.Zero);
                d.FirstPostType ??= MessageFingerprint.ContentTypeOf(message);
                d.FirstPostDelaySeconds ??= secondsAfterJoining;
            });
        }

        /// <summary>A new member's message with links, mentions or a forward.</summary>
        internal static void Posted(long chatId, Message message, int links, int mentions, bool forward) =>
            Change(chatId, message.From.Id, message.From, message.Date.ToUniversalTime(), d =>
            {
                d.Links += links;
                d.Mentions += mentions;
                if (forward) d.Forwards++;
            });

        /// <summary>A new member's score went up.</summary>
        internal static void Scored(long chatId, long userId, int score) =>
            Change(chatId, userId, null, Clock.UtcNow, d => d.MaxScore = Math.Max(d.MaxScore, score));

        /// <summary>What a profile lookup found (flags only).</summary>
        internal static void Profile(long chatId, long userId, IReadOnlyList<string> flags) =>
            Change(chatId, userId, null, Clock.UtcNow, d => d.ProfileFlags = flags.ToArray());

        /// <summary>A new member pointed at a channel (by id) or a website (by host).</summary>
        internal static void Pointer(long chatId, long userId, string kind, string value)
        {
            if (!PostgresStorage.Ready || userId == 0 || string.IsNullOrEmpty(value)) return;
            lock (Lock)
            {
                if (_pointers.Count >= Capacity) return;
                _pointers[(chatId, userId, kind, value)] = new PointerRecord(chatId, userId, kind, value, new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero));
            }
        }

        // ------------------------------------------------------------------ writing

        /// <summary>Writes what was collected. Never throws; a failed batch is reported and dropped.</summary>
        internal static async Task FlushAsync()
        {
            if (!PostgresStorage.Ready) return;
            List<SignalDelta> signals;
            List<PointerRecord> pointers;
            lock (Lock)
            {
                signals = _signals.Values.ToList();
                pointers = _pointers.Values.ToList();
                _signals = new Dictionary<(long, long), SignalDelta>();
                _pointers = new Dictionary<(long, long, string, string), PointerRecord>();
            }
            try
            {
                await Repositories.DetectionData.WriteSignalsAsync(signals);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing {signals.Count} member signals");
            }
            try
            {
                await Repositories.DetectionData.WritePointersAsync(pointers);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing {pointers.Count} member pointers");
            }
        }

        internal static void ResetForTests()
        {
            lock (Lock)
            {
                _signals.Clear();
                _pointers.Clear();
            }
        }
    }
}
