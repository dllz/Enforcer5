using System;
using System.Collections.Generic;
using System.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Everything the automatic checks need to know about one group's settings, as one immutable
    /// value (Detection v2, step 2; docs/detection-design.md section 2). PostgreSQL holds it
    /// (<c>group_settings</c>, <c>detection_policies</c>, <c>group_filters</c>);
    /// <see cref="DetectionSettings"/> keeps it in memory and caches it in Redis.
    /// <para>
    /// The values mean what the checks do with them, not what the old Redis hashes said: flood is
    /// <see cref="FloodSettings.Enabled"/> where the hash had <c>Flood = "no"</c>, actions are
    /// lowercase, and a value a check treats as "not set" stays null so the check keeps behaving as
    /// it did (a missing flood limit turns flood off; a missing media or length action takes no
    /// action). Only <see cref="LegacySettings"/> knows the old encodings.
    /// </para>
    /// </summary>
    internal sealed record DetectionPolicy(
        long Version,
        FloodSettings Flood,
        MediaSettings Media,
        bool MuteOnJoin,
        FilterSettings Filters,
        LengthSettings TextLength,
        LengthSettings NameLength,
        WarnSettings Warns,
        int TempbanMinutes,
        int TempmuteMinutes,
        IReadOnlyList<InlineBotBlock> InlineBots,
        IReadOnlyList<string> StickerSets,
        ChannelPostSettings ChannelPosts,
        EngineSettings Engine)
    {
        /// <summary>The actions an automatic check can take (design section 1).</summary>
        internal static readonly string[] Actions =
            { "none", "score", "report", "delete", "warn", "mute", "tempmute", "kick", "tempban", "ban" };

        /// <summary>A group nothing was ever stored for: what the checks did with empty hashes.</summary>
        internal static DetectionPolicy Empty { get; } = new DetectionPolicy(
            Version: 0,
            Flood: new FloodSettings(true, null, null, new Dictionary<string, bool>()),
            Media: new MediaSettings(new Dictionary<string, bool>(), null, null),
            MuteOnJoin: false,
            Filters: FilterSettings.Empty,
            TextLength: new LengthSettings(false, null, null, null),
            NameLength: new LengthSettings(false, null, null, null),
            Warns: new WarnSettings(Methods.DefaultMaxWarns, WarnSettings.Kick),
            TempbanMinutes: Duration.DefaultMinutes,
            TempmuteMinutes: Duration.DefaultMinutes,
            InlineBots: Array.Empty<InlineBotBlock>(),
            StickerSets: Array.Empty<string>(),
            ChannelPosts: new ChannelPostSettings(false, Array.Empty<AllowedChannel>()),
            Engine: EngineSettings.Default);

        /// <summary>
        /// What <c>Service.GenerateSettings</c> gives a new group (or one reset with /menu): it sets
        /// these and leaves everything else as it was, as it did on the old hashes.
        /// </summary>
        internal DetectionPolicy WithNewGroupDefaults()
        {
            var counted = new Dictionary<string, bool>(Flood.Counted);
            foreach (var type in new[] { "image", "video", "sticker", "gif" }) counted[type] = true;
            var blocked = new Dictionary<string, bool>(Media.Blocked);
            foreach (var type in MediaSettings.MenuTypes) blocked[type] = false;
            return this with
            {
                Flood = new FloodSettings(false, 5, "kick", counted),
                Media = new MediaSettings(blocked, "tempban", 3),
                Filters = Filters.WithPreset(FilterPresets.Arabic, null).WithPreset(FilterPresets.Rtl, null),
                TextLength = new LengthSettings(true, OnMessage.DefaultMaxTextLength, OnMessage.DefaultMaxTextLines, "kick"),
                NameLength = new LengthSettings(true, OnMessage.DefaultMaxNameLength, null, "kick"),
                Warns = new WarnSettings(Methods.DefaultMaxWarns, WarnSettings.Ban)
            };
        }

        /// <summary>Same settings, ignoring <see cref="Version"/>.</summary>
        internal bool SameSettingsAs(DetectionPolicy other) =>
            other != null && DetectionPolicyJson.Serialize(Canonical(this)) == DetectionPolicyJson.Serialize(Canonical(other));

        /// <summary>Version 0 and the maps in key order (PostgreSQL's jsonb does not keep the order).</summary>
        private static DetectionPolicy Canonical(DetectionPolicy policy) => policy with
        {
            Version = 0,
            Flood = policy.Flood with { Counted = new SortedDictionary<string, bool>(policy.Flood.Counted.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal) },
            Media = policy.Media with { Blocked = new SortedDictionary<string, bool>(policy.Media.Blocked.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal) },
            Engine = policy.Engine with { Signs = new SortedDictionary<string, SignPolicy>(policy.Engine.Signs.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal) },
            // Ids are the database's; a rule is the same rule by what it is.
            Filters = policy.Filters with
            {
                Rules = policy.Filters.Rules
                    .Where(r => r.Enabled || r.Kind != FilterRule.PresetKind) // a preset that is off is no preset
                    .Select(r => r with { Id = 0 })
                    .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Preset, StringComparer.Ordinal)
                    .ThenBy(r => r.Target, StringComparer.Ordinal).ThenBy(r => r.Pattern, StringComparer.Ordinal)
                    .ToList()
            }
        };
    }

    /// <param name="Enabled">Flood checking is on (the old <c>Flood = "no"</c>).</param>
    /// <param name="MaxMessages">Messages allowed per window; null turns flood off, as a missing or unreadable <c>MaxFlood</c> did.</param>
    /// <param name="Action">kick, warn, tempban or ban; null or anything else takes no action.</param>
    /// <param name="Counted">Content types the group set: true counts, false is exempt. Unlisted types count.</param>
    internal sealed record FloodSettings(bool Enabled, int? MaxMessages, string Action, IReadOnlyDictionary<string, bool> Counted)
    {
        internal const int DefaultMaxMessages = 5;

        /// <summary>The exception types the flood menu always shows (text is shown on its own).</summary>
        internal static readonly string[] MenuTypes = { "image", "video", "sticker", "gif" };

        internal bool IsExempt(string contentType) =>
            contentType != null && Counted.TryGetValue(contentType, out var counted) && !counted;
    }

    /// <param name="Blocked">Content types the group set (<c>Methods.GetContentType</c> names): true blocked, false allowed.</param>
    /// <param name="Action">kick, tempban or ban once <see cref="WarnLimit"/> is reached; null strikes and deletes only.</param>
    /// <param name="WarnLimit">Strikes before the action; null is <see cref="DefaultWarnLimit"/>.</param>
    internal sealed record MediaSettings(IReadOnlyDictionary<string, bool> Blocked, string Action, int? WarnLimit)
    {
        /// <summary>What the media check used when nothing was stored (new groups get 3).</summary>
        internal const int DefaultWarnLimit = 2;

        /// <summary>The types the media menu always shows, in its order; others show when set.</summary>
        internal static readonly string[] MenuTypes = { "image", "audio", "video", "sticker", "gif", "voice", "contact", "file", "link" };

        internal bool IsBlocked(string contentType) =>
            contentType != null && Blocked.TryGetValue(contentType, out var blocked) && blocked;

        internal int EffectiveWarnLimit => WarnLimit ?? DefaultWarnLimit;
    }

    /// <param name="Enabled">The check is on.</param>
    /// <param name="MaxCharacters">Null is the check's default.</param>
    /// <param name="MaxLines">Text only; null is the check's default.</param>
    /// <param name="Action">kick, warn, tempban or ban; null asks the admins to choose one.</param>
    internal sealed record LengthSettings(bool Enabled, int? MaxCharacters, int? MaxLines, string Action);

    /// <param name="MaxWarns">Warns that trigger <paramref name="Action"/>; always positive.</param>
    /// <param name="Action">kick or ban.</param>
    internal sealed record WarnSettings(int MaxWarns, string Action)
    {
        internal const string Kick = "kick";
        internal const string Ban = "ban";
    }

    /// <param name="Enabled">Posts made as a channel are removed.</param>
    /// <param name="Allowed">Channels that may post anyway.</param>
    internal sealed record ChannelPostSettings(bool Enabled, IReadOnlyList<AllowedChannel> Allowed)
    {
        internal bool IsAllowed(long channelId) => Allowed.Any(c => c.Id == channelId);
    }

    /// <summary>
    /// How the Detection engine treats one group (design sections 1 and 3).
    /// </summary>
    /// <param name="ObserveUntil">
    /// Until then the group is in observe mode: signs are recorded with what would have been done,
    /// nothing is done. Null for a group the engine has not seen yet; it starts then
    /// (<see cref="ObserveFor"/>). An admin going live sets it to now.
    /// </param>
    /// <param name="ReportAt">A member's score at which the admins get one report about them.</param>
    /// <param name="MuteAt">A member's score at which they are muted, when <paramref name="MuteEnabled"/>.</param>
    /// <param name="Signs">The group's own settings for a sign, by name; others use <see cref="SignCatalog"/>.</param>
    internal sealed record EngineSettings(DateTimeOffset? ObserveUntil, int ReportAt, int MuteAt, bool MuteEnabled,
        IReadOnlyDictionary<string, SignPolicy> Signs)
    {
        internal static readonly TimeSpan ObserveFor = TimeSpan.FromDays(14);

        internal static EngineSettings Default { get; } =
            new EngineSettings(null, 3, 6, false, new Dictionary<string, SignPolicy>());

        internal bool Observing(DateTimeOffset now) => ObserveUntil == null || now < ObserveUntil;

        /// <summary>What the group does with <paramref name="sign"/>: its own setting, else the catalog's.</summary>
        internal SignPolicy For(string sign) =>
            Signs.TryGetValue(sign, out var own) ? own : SignCatalog.Get(sign)?.Default ?? SignPolicy.Off;
    }

    /// <param name="Enabled">The sign is looked for at all.</param>
    /// <param name="Action">One of <see cref="DetectionPolicy.Actions"/>.</param>
    /// <param name="Weight">What it adds to the member's score.</param>
    /// <param name="Delete">The message goes too, whatever the action.</param>
    /// <param name="Report">The admins are told, whatever the score.</param>
    internal sealed record SignPolicy(bool Enabled, string Action, int Weight, bool Delete = false, bool Report = false)
    {
        internal static SignPolicy Off { get; } = new SignPolicy(false, "none", 0);
    }
}
