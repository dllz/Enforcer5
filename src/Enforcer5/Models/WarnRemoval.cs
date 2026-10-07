using System;
using System.Collections.Generic;
using System.Linq;

namespace Enforcer5.Models
{
    /// <summary>The three warn counters a group keeps per user.</summary>
    internal enum WarnKind
    {
        /// <summary>chat:{id}:warns, from /warn. Reaching the maximum kicks or bans.</summary>
        Warn,

        /// <summary>chat:{id}:prewarns, from /prewarn. Converted into warns at the next /warn.</summary>
        Prewarn,

        /// <summary>chat:{id}:mediawarn, the automatic strikes for blocked media.</summary>
        Media
    }

    /// <summary>
    /// Which warn removals wait for a reason, chosen per group in /menu. Each level includes the
    /// ones before it. Stored as the number, so the order is part of the storage format.
    /// </summary>
    internal enum WarnReasonMode
    {
        Off = 0,
        Warns = 1,
        WarnsAndPrewarns = 2,
        All = 3
    }

    internal static class WarnReasonModes
    {
        /// <summary>Whether removing or resetting warns of <paramref name="kind"/> needs a reason.</summary>
        public static bool Requires(this WarnReasonMode mode, WarnKind kind) => kind switch
        {
            WarnKind.Warn => mode >= WarnReasonMode.Warns,
            WarnKind.Prewarn => mode >= WarnReasonMode.WarnsAndPrewarns,
            _ => mode >= WarnReasonMode.All
        };

        /// <summary>The next level in the /menu cycle: Off, Warns, Warns + pre-warns, All, Off.</summary>
        public static WarnReasonMode Next(this WarnReasonMode mode) =>
            mode >= WarnReasonMode.All || mode < WarnReasonMode.Off ? WarnReasonMode.Off : mode + 1;
    }

    /// <summary>
    /// What an admin asked for: take one warn off <paramref name="TargetId"/>, or reset the count.
    /// <paramref name="AlsoMediaWarns"/> is set by the reset button under a /warn message, which has
    /// always cleared media warns together with warns.
    /// </summary>
    internal sealed record WarnChange(WarnKind Kind, bool Reset, long TargetId, bool AlsoMediaWarns = false);

    /// <summary>
    /// A removal waiting for its reason, stored under the id of the bot's question.
    /// </summary>
    /// <param name="AdminId">Who asked; only they may answer (see <see cref="WarnReasons.WhoMayAnswer"/>).</param>
    /// <param name="SourceMessageId">The message to update once applied.</param>
    /// <param name="SourceIsBotMessage">True when that is the bot's own message with the buttons, which is
    /// edited; false for an admin's /removewarn, which is replied to instead.</param>
    /// <param name="Reasons">The reasons offered as buttons, as shown. Buttons carry an index into this,
    /// so editing the saved list while a question is open cannot change what a button means.</param>
    internal sealed record PendingWarnRemoval(
        WarnChange Change,
        long AdminId,
        string AdminName,
        int SourceMessageId,
        bool SourceIsBotMessage,
        string[] Reasons);

    internal enum Answerer
    {
        Allowed,

        /// <summary>Allowed if the responder is a group admin, which the caller has to check.</summary>
        AllowedIfAdmin,
        Refused
    }

    /// <summary>Rules for warn removal reasons that do not depend on Telegram or Redis.</summary>
    internal static class WarnReasons
    {
        /// <summary>Longest reason accepted, typed or saved. Reasons are also button labels.</summary>
        internal const int MaxLength = 100;

        /// <summary>Most reasons a group can save with /addwarnreason.</summary>
        internal const int MaxSaved = 10;

        /// <summary>How many recently used reasons are remembered per group.</summary>
        internal const int MaxRecent = 5;

        /// <summary>Most reason buttons under a question, not counting Cancel.</summary>
        internal const int MaxChoices = 8;

        /// <summary>Telegram's GroupAnonymousBot: the sender of every message an anonymous admin posts.</summary>
        internal const long AnonymousAdminId = 1087968824;

        /// <summary>
        /// A reason as typed: trimmed, inner whitespace collapsed to single spaces. Null if empty or
        /// longer than <see cref="MaxLength"/>.
        /// </summary>
        public static string Normalise(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var reason = string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return reason.Length <= MaxLength ? reason : null;
        }

        /// <summary>
        /// The buttons under a question: saved reasons in their order, then recent ones not already
        /// shown (compared ignoring case), at most <see cref="MaxChoices"/>.
        /// </summary>
        public static string[] Choices(IEnumerable<string> saved, IEnumerable<string> recent)
        {
            var choices = new List<string>(MaxChoices);
            foreach (var reason in (saved ?? Enumerable.Empty<string>()).Concat(recent ?? Enumerable.Empty<string>()))
            {
                if (choices.Count == MaxChoices) break;
                if (string.IsNullOrWhiteSpace(reason)) continue;
                if (choices.Any(c => string.Equals(c, reason, StringComparison.OrdinalIgnoreCase))) continue;
                choices.Add(reason);
            }
            return choices.ToArray();
        }

        /// <summary>
        /// Parses the language file's default list, written as <c>Mistake|Appeal accepted|...</c>.
        /// </summary>
        public static IReadOnlyList<string> ParseDefaults(string text) =>
            (text ?? "").Split('|')
                .Select(Normalise)
                .Where(reason => reason != null)
                .Take(MaxSaved)
                .ToList();

        /// <summary>
        /// Whether <paramref name="responderId"/> may give the reason for a removal that
        /// <paramref name="adminId"/> started. Anonymous admins all post as the same account, so
        /// neither side can be told apart: an anonymous answer is accepted (only admins can post
        /// anonymously), and any admin may answer a question an anonymous admin started.
        /// </summary>
        public static Answerer WhoMayAnswer(long adminId, long responderId, bool responderIsAnonymousAdmin)
        {
            if (responderId == adminId || responderIsAnonymousAdmin) return Answerer.Allowed;
            if (adminId == AnonymousAdminId) return Answerer.AllowedIfAdmin;
            return Answerer.Refused;
        }
    }
}
