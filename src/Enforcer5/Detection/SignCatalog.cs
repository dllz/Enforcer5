using System.Collections.Generic;

namespace Enforcer5.Detection
{
    /// <summary>When a sign can be known: on the message itself, or after a lookup.</summary>
    internal enum SignPhase
    {
        /// <summary>From the message and in-memory caches, in the message handler.</summary>
        Inline,

        /// <summary>After a lookup (profile, mention, model), best effort.</summary>
        Enriched
    }

    /// <summary>A sign the engine knows, with what a group gets unless it sets its own (design section 3).</summary>
    /// <param name="Name">Stored in <c>detections.sign</c> and as <c>sign:{name}</c> in <c>detection_policies</c>.</param>
    /// <param name="NewcomersOnly">Only counts for members who are not established, approved or admins.</param>
    /// <param name="LiveFromDayOne">Acts during the group's observe weeks too (edit rules, the ported checks).</param>
    internal sealed record SignDefinition(string Name, SignPhase Phase, SignPolicy Default, bool NewcomersOnly,
        bool LiveFromDayOne = false);

    /// <summary>
    /// Every sign there is. A check may only report a sign listed here, so that
    /// <c>detections</c> has one name per thing and a group's settings can name it. The signs of later
    /// steps are listed already with their default from the design; nothing reports them yet.
    /// </summary>
    internal static class SignCatalog
    {
        private static readonly Dictionary<string, SignDefinition> Signs = new();

        private static SignDefinition Add(string name, SignPhase phase, int weight, string action, bool newcomersOnly = true,
            bool delete = false, bool report = false, bool liveFromDayOne = false)
        {
            var sign = new SignDefinition(name, phase, new SignPolicy(true, action, weight, delete, report), newcomersOnly, liveFromDayOne);
            Signs[name] = sign;
            return sign;
        }

        // Newcomer signs (design section 3).
        internal static readonly SignDefinition LinkOutside = Add("link_outside", SignPhase.Inline, 2, "score");
        internal static readonly SignDefinition MentionOutside = Add("mention_outside", SignPhase.Enriched, 2, "score");
        internal static readonly SignDefinition ForwardChannel = Add("forward_channel", SignPhase.Inline, 1, "score");
        internal static readonly SignDefinition FirstPostMedia = Add("first_post_media", SignPhase.Inline, 1, "score");
        internal static readonly SignDefinition PostedSoon = Add("posted_soon", SignPhase.Inline, 1, "score");
        internal static readonly SignDefinition SameAsNewcomers = Add("same_as_newcomers", SignPhase.Inline, 2, "score");
        internal static readonly SignDefinition NameImpersonation = Add("name_impersonation", SignPhase.Inline, 4, "report", report: true);
        internal static readonly SignDefinition NameKeyword = Add("name_keyword", SignPhase.Inline, 2, "score");
        internal static readonly SignDefinition BioLink = Add("bio_link", SignPhase.Enriched, 1, "score");
        internal static readonly SignDefinition BioMention = Add("bio_mention", SignPhase.Enriched, 1, "score");
        internal static readonly SignDefinition PersonalChannel = Add("personal_channel", SignPhase.Enriched, 1, "score");
        internal static readonly SignDefinition ChannelReputation = Add("channel_reputation", SignPhase.Enriched, 3, "score");
        internal static readonly SignDefinition BannedMedia = Add("banned_media", SignPhase.Inline, 3, "delete", delete: true);
        internal static readonly SignDefinition SimilarToBanned = Add("similar_to_banned", SignPhase.Enriched, 2, "score");
        internal static readonly SignDefinition JoinedDuringRaid = Add("joined_during_raid", SignPhase.Inline, 1, "score");

        // Edit rules (section 4): for everyone but admins and approved members, live from day one.
        internal static readonly SignDefinition EditSwap = Add("edit_swap", SignPhase.Inline, 3, "delete", newcomersOnly: false,
            delete: true, report: true, liveFromDayOne: true);
        internal static readonly SignDefinition EditLate = Add("edit_late", SignPhase.Inline, 1, "report", newcomersOnly: false,
            report: true, liveFromDayOne: true);

        /// <summary>The sign, or null when there is no such sign.</summary>
        internal static SignDefinition Get(string name) =>
            name != null && Signs.TryGetValue(name, out var sign) ? sign : null;

        internal static IReadOnlyCollection<SignDefinition> All => Signs.Values;

        /// <summary>Adds a sign for a test; replaced when one with the same name is added again.</summary>
        internal static SignDefinition AddForTests(string name, int weight, string action, bool newcomersOnly = true,
            bool delete = false, bool report = false, bool liveFromDayOne = false) =>
            Add(name, SignPhase.Inline, weight, action, newcomersOnly, delete, report, liveFromDayOne);
    }
}
