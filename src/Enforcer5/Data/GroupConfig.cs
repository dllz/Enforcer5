using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Enforcer5.Data
{
    /// <summary>Which of a group's texts a <see cref="GroupConfig"/> content item is.</summary>
    internal enum ContentKind
    {
        Rules,
        About,
        Language,
        Link
    }

    /// <summary>The group's on/off settings (the old <c>chat:{id}:settings</c> hash, apart from the detection ones).</summary>
    internal enum ConfigFlag
    {
        /// <summary>New members are welcomed (old field <c>Welcome</c>, where "yes" meant off).</summary>
        Welcome,
        /// <summary>The previous welcome is deleted when a new one goes out (old <c>DeleteLastWelcome</c>, "no" meant on).</summary>
        DeleteLastWelcome,
        /// <summary>
        /// Members may report (/report, @admin) and links during a raid are reported. The menu's toggle used to write
        /// <c>Report</c>, which nothing read, while reports read <c>Flagged</c>, which nothing wrote (Daniel, 2026-11-06:
        /// imported as on, and the toggle works).
        /// </summary>
        Reports,
        /// <summary>/rules answers in private (old <c>Rules</c> = "yes"); likewise the four below.</summary>
        RulesPrivate,
        AboutPrivate,
        ModlistPrivate,
        ExtraPrivate,
        HelpPrivate
    }

    /// <summary>
    /// A group's welcome: <c>composed</c> (the greeting plus <see cref="Content"/>'s parts: <c>a</c>bout, <c>r</c>ules,
    /// <c>m</c>ods, or <c>no</c>), <c>custom</c> (<see cref="Content"/> is the text, sent with the GIF in
    /// <see cref="MediaFileId"/> when there is one) or <c>media</c> (the GIF alone). A file id belongs to the edition
    /// that saved it (<see cref="MediaFileEdition"/>; <c>legacy_unknown</c> for one from Redis).
    /// </summary>
    internal sealed record WelcomeConfig(string Type, string Content, string MediaFileId, string MediaFileEdition)
    {
        internal static readonly WelcomeConfig Default = new("composed", "no", null, null);
    }

    /// <summary>
    /// A group's extra (#trigger): a text, optionally sent with a GIF (<see cref="MediaType"/> <c>animation</c>), or a
    /// file of <see cref="MediaType"/> with an optional <see cref="Caption"/>. <see cref="MediaType"/> uses the names
    /// of <see cref="Helpers.Methods.GetMediaType"/> (<c>gif</c>, <c>file</c>, <c>photo</c>, ...), as the bot always did.
    /// </summary>
    internal sealed record ExtraConfig(string Text, string MediaType, string FileId, string FileEdition, string Caption)
    {
        internal bool IsFile => Text == null && FileId != null;

        /// <summary>A text, with a GIF (from /extra replying to one) or without.</summary>
        internal static ExtraConfig OfText(string text, string gifFileId = null, string edition = null) =>
            new(text, gifFileId != null ? "gif" : null, gifFileId, gifFileId != null ? edition : null, null);

        internal static ExtraConfig OfFile(string type, string fileId, string edition, string caption = null) =>
            new(null, type, fileId, edition, caption);

        private const string FilePrefix = "###file_id!";

        /// <summary>The value as Redis held it (<c>###file_id!{type}###:{id}</c> for a file), for the data recovery.</summary>
        internal string Raw => IsFile ? $"{FilePrefix}{MediaType}###:{FileId}" : Text;

        /// <summary>
        /// An extra from its old Redis value and side hash (<c>chat:{id}:extra:#trigger</c>): the side hash's
        /// <c>mediaid</c> belongs to a text, its <c>caption</c> to a file; either left over from an earlier version of
        /// the extra is dropped. Null when the value cannot be read.
        /// </summary>
        internal static ExtraConfig FromLegacy(string raw, string mediaId, string caption, string edition = "legacy_unknown")
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (!raw.StartsWith(FilePrefix, StringComparison.Ordinal))
                return OfText(raw, string.IsNullOrEmpty(mediaId) ? null : mediaId, edition);
            var parts = raw.Substring(FilePrefix.Length).Split(new[] { "###:" }, 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            return OfFile(parts[0], parts[1], edition, string.IsNullOrEmpty(caption) ? null : caption);
        }
    }

    /// <summary>
    /// A group's settings other than the automatic checks', its welcome, its texts and its extras (rollout step 15).
    /// Immutable; <see cref="Version"/> is <c>group_settings.config_version</c>.
    /// </summary>
    internal sealed record GroupConfig
    {
        public ImmutableDictionary<ConfigFlag, bool> Flags { get; init; } = Defaults;
        public WelcomeConfig Welcome { get; init; } = WelcomeConfig.Default;
        public long? LogChat { get; init; }
        public ImmutableDictionary<ContentKind, string> Content { get; init; } = ImmutableDictionary<ContentKind, string>.Empty;
        public ImmutableSortedDictionary<string, ExtraConfig> Extras { get; init; } =
            ImmutableSortedDictionary<string, ExtraConfig>.Empty.WithComparers(StringComparer.Ordinal);
        public long Version { get; init; }

        /// <summary>What a new group gets (as <c>Service.GenerateSettings</c> and <c>NewSetting3</c> wrote to Redis).</summary>
        internal static readonly ImmutableDictionary<ConfigFlag, bool> Defaults = new Dictionary<ConfigFlag, bool>
        {
            [ConfigFlag.Welcome] = false,
            [ConfigFlag.DeleteLastWelcome] = true,
            [ConfigFlag.Reports] = true,
            [ConfigFlag.RulesPrivate] = true,
            [ConfigFlag.AboutPrivate] = true,
            [ConfigFlag.ModlistPrivate] = true,
            [ConfigFlag.ExtraPrivate] = true,
            [ConfigFlag.HelpPrivate] = true
        }.ToImmutableDictionary();

        internal static readonly GroupConfig NewGroup = new();

        internal bool Flag(ConfigFlag flag) => Flags.TryGetValue(flag, out var on) ? on : Defaults[flag];

        internal string Text(ContentKind kind) => Content.TryGetValue(kind, out var text) ? text : null;

        internal ExtraConfig Extra(string trigger) => trigger != null && Extras.TryGetValue(trigger, out var extra) ? extra : null;

        /// <summary>The same settings, whatever the version.</summary>
        internal bool SameAs(GroupConfig other) =>
            other != null && Welcome == other.Welcome && LogChat == other.LogChat &&
            Enum.GetValues<ConfigFlag>().All(f => Flag(f) == other.Flag(f)) &&
            Content.Count == other.Content.Count && Content.All(c => other.Text(c.Key) == c.Value) &&
            Extras.Count == other.Extras.Count && Extras.All(e => other.Extra(e.Key) == e.Value);
    }

    /// <summary>One change to a group's config, applied in memory and written to PostgreSQL (or queued). Idempotent.</summary>
    internal sealed record ConfigChange
    {
        public string Kind { get; init; }
        public ConfigFlag Flag { get; init; }
        public bool On { get; init; }
        public ContentKind Content { get; init; }
        /// <summary>Content or extra trigger's new text; null deletes.</summary>
        public string Text { get; init; }
        public string Trigger { get; init; }
        public ExtraConfig Extra { get; init; }
        public WelcomeConfig Welcome { get; init; }
        public long? LogChat { get; init; }

        internal static ConfigChange SetFlag(ConfigFlag flag, bool on) => new() { Kind = "flag", Flag = flag, On = on };
        internal static ConfigChange SetContent(ContentKind kind, string text) => new() { Kind = "content", Content = kind, Text = text };
        internal static ConfigChange SetExtra(string trigger, ExtraConfig extra) => new() { Kind = "extra", Trigger = trigger, Extra = extra };
        internal static ConfigChange SetWelcome(WelcomeConfig welcome) => new() { Kind = "welcome", Welcome = welcome };
        internal static ConfigChange SetLogChat(long? chat) => new() { Kind = "logchat", LogChat = chat };
        /// <summary>The settings and welcome back to a new group's (/resetSettings, the bot added); texts and extras stay.</summary>
        internal static ConfigChange Reset() => new() { Kind = "reset" };

        internal GroupConfig ApplyTo(GroupConfig config) => Kind switch
        {
            "flag" => config with { Flags = config.Flags.SetItem(Flag, On) },
            "content" => config with { Content = Text == null ? config.Content.Remove(Content) : config.Content.SetItem(Content, Text) },
            "extra" => config with { Extras = Extra == null ? config.Extras.Remove(Trigger) : config.Extras.SetItem(Trigger, Extra) },
            "welcome" => config with { Welcome = Welcome ?? WelcomeConfig.Default },
            "logchat" => config with { LogChat = LogChat },
            "reset" => config with { Flags = GroupConfig.Defaults, Welcome = WelcomeConfig.Default },
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
        };
    }
}
