using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// What a sign, and a decision about a member, is about (build step 7c, Daniel 2026-10-28): rules that hold
    /// everywhere (spam, ads, scams, raids, impersonation) or the group's own rules (its language, media and
    /// length limits, flood, a game's rules). A member can be trusted to post safely without being trusted to
    /// keep a group's rules, so only <see cref="Abuse"/> says anything about trust.
    /// </summary>
    internal static class SignCategory
    {
        internal const string Abuse = "abuse";
        internal const string GroupRule = "group";
    }

    /// <summary>A sign the engine knows, with what a group gets unless it sets its own (design section 3).</summary>
    /// <param name="Name">Stored in <c>detections.sign</c> and as <c>sign:{name}</c> in <c>detection_policies</c>.</param>
    /// <param name="NewcomersOnly">Only counts for members who are not established, approved or admins.</param>
    /// <param name="LiveFromDayOne">Acts during the group's observe weeks too (edit rules, the ported checks).</param>
    /// <param name="Ported">
    /// One of the checks that existed before the engine (design section 1). The check keeps its own
    /// settings, exemptions, action and notices, and tells the engine what it did
    /// (<see cref="DetectionEngine.Checked"/>), which records it. Build step 4 moved them behind the
    /// engine with no change in what they do; step 18 gives them the shared actions.
    /// </param>
    /// <param name="Category"><see cref="SignCategory"/>: abuse, or one of the group's own rules.</param>
    internal sealed record SignDefinition(string Name, SignPhase Phase, SignPolicy Default, bool NewcomersOnly,
        bool LiveFromDayOne = false, bool Ported = false, string Category = SignCategory.Abuse);

    /// <summary>
    /// Every sign there is. A check may only report a sign listed here, so that
    /// <c>detections</c> has one name per thing and a group's settings can name it. The signs of later
    /// steps are listed already with their default from the design; nothing reports them yet.
    /// </summary>
    internal static class SignCatalog
    {
        private static readonly Dictionary<string, SignDefinition> Signs = new();

        private static SignDefinition Add(string name, SignPhase phase, int weight, string action, bool newcomersOnly = true,
            bool delete = false, bool report = false, bool liveFromDayOne = false, bool ported = false)
        {
            var sign = new SignDefinition(name, phase, new SignPolicy(true, action, weight, delete, report), newcomersOnly,
                liveFromDayOne, ported);
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
        // Late edits: report and delete for newcomers, report for established members (Daniel); each group can change it.
        internal static readonly SignDefinition EditLate = Register(Add("edit_late", SignPhase.Inline, 1, "report", newcomersOnly: false,
            report: true, liveFromDayOne: true) with { Default = new SignPolicy(true, "report", 1, Report: true, DeleteNewcomers: true) });

        // The checks that existed before the engine (build step 4). Each applies to whom it always has and
        // does what its own settings say; the engine records it. They add nothing to the score (weight 0),
        // so a member's score still means what the newcomer signs saw.
        private static SignDefinition Ported(string name, string category = SignCategory.GroupRule) =>
            Add(name, SignPhase.Inline, 0, "none", newcomersOnly: false, liveFromDayOne: true, ported: true) with { Category = category };

        // The group's own limits (flood too: chatty games flood), and raids and mass adds, which are abuse.
        internal static readonly SignDefinition Flood = Register(Ported("flood"));
        internal static readonly SignDefinition MediaLock = Register(Ported("media_lock"));
        internal static readonly SignDefinition TextLength = Register(Ported("text_length"));
        internal static readonly SignDefinition NameLength = Register(Ported("name_length"));
        internal static readonly SignDefinition InlineBot = Register(Ported("inline_bot"));
        internal static readonly SignDefinition StickerSet = Register(Ported("sticker_set"));
        internal static readonly SignDefinition ChannelPost = Register(Ported("channel_post"));
        internal static readonly SignDefinition RaidLink = Register(Ported("raid_link", SignCategory.Abuse));
        internal static readonly SignDefinition MassAdd = Register(Ported("mass_add", SignCategory.Abuse));

        private static SignDefinition Register(SignDefinition sign) => Signs[sign.Name] = sign;

        /// <summary>
        /// A filter (design section 5): <c>filter:{preset}</c> or <c>filter:{id}</c> of an admin's rule. Like
        /// the ported checks, a filter acts itself and the engine records it.
        /// </summary>
        internal const string FilterPrefix = "filter:";

        private static readonly SignDefinition Filter =
            new SignDefinition(FilterPrefix, SignPhase.Inline, new SignPolicy(true, "none", 0), false, true, true);

        /// <summary>The sign, or null when there is no such sign.</summary>
        internal static SignDefinition Get(string name)
        {
            if (name == null) return null;
            if (Signs.TryGetValue(name, out var sign)) return sign;
            return name.StartsWith(FilterPrefix, System.StringComparison.Ordinal) && name.Length > FilterPrefix.Length
                ? Filter with { Name = name }
                : null;
        }

        internal static IReadOnlyCollection<SignDefinition> All => Signs.Values;

        /// <summary>Adds a sign for a test; replaced when one with the same name is added again.</summary>
        internal static SignDefinition AddForTests(string name, int weight, string action, bool newcomersOnly = true,
            bool delete = false, bool report = false, bool liveFromDayOne = false, string category = SignCategory.Abuse) =>
            Register(Add(name, SignPhase.Inline, weight, action, newcomersOnly, delete, report, liveFromDayOne) with { Category = category });

        /// <summary>
        /// What <paramref name="sign"/> is about in a group with <paramref name="filters"/>: a filter by its preset,
        /// or by what the admins marked their own rule; any other sign by the catalog.
        /// </summary>
        internal static string CategoryOf(string sign, FilterSettings filters)
        {
            if (sign != null && sign.StartsWith(FilterPrefix, System.StringComparison.Ordinal))
            {
                var rule = sign.Substring(FilterPrefix.Length);
                if (FilterPresets.Get(rule) is { } preset) return preset.Category;
                return long.TryParse(rule, out var id) && filters?.OwnRules.FirstOrDefault(r => r.Id == id) is { } own
                    ? own.Category
                    : SignCategory.GroupRule;
            }
            return Get(sign)?.Category ?? SignCategory.Abuse;
        }
    }
}
