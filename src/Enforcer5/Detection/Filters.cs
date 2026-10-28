using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Enforcer5.Helpers;

namespace Enforcer5.Detection
{
    /// <summary>
    /// A group's filters (design section 5): the prepared rules it switched on and its own regex
    /// rules, with the two choices that apply to all of them.
    /// </summary>
    /// <param name="Rules">Every rule, on or off, live or on trial.</param>
    /// <param name="IncludeEstablished">Established members are checked too (they always were).</param>
    /// <param name="ExemptGroupBots">
    /// The text of a message sent via an inline bot that is a member of the group is not checked: some
    /// bots answer in the member's own language, and the script presets used to kick them for it. Names
    /// are still checked. A bot outside the group is never exempt.
    /// </param>
    internal sealed record FilterSettings(IReadOnlyList<FilterRule> Rules, bool IncludeEstablished, bool ExemptGroupBots)
    {
        internal static FilterSettings Empty { get; } = new FilterSettings(Array.Empty<FilterRule>(), true, false);

        /// <summary>The action of <paramref name="preset"/>; null when it is off.</summary>
        internal string PresetAction(string preset) =>
            Rules.FirstOrDefault(r => r.Kind == FilterRule.PresetKind && r.Preset == preset) is { Enabled: true } rule ? rule.Action : null;

        /// <summary>The same settings with <paramref name="preset"/> doing <paramref name="action"/>; null switches it off.</summary>
        internal FilterSettings WithPreset(string preset, string action)
        {
            var rules = Rules.ToList();
            var at = rules.FindIndex(r => r.Kind == FilterRule.PresetKind && r.Preset == preset);
            var rule = at >= 0 ? rules[at] : FilterRule.NewPreset(preset, null);
            rule = rule with { Enabled = action != null, Action = action };
            if (at >= 0) rules[at] = rule;
            else rules.Add(rule);
            return this with { Rules = rules };
        }

        /// <summary>The group's own regex rules, oldest first.</summary>
        internal IEnumerable<FilterRule> OwnRules => Rules.Where(r => r.Kind == FilterRule.RegexKind);
    }

    /// <summary>
    /// One filter, as stored in <c>group_filters</c>: a preset (<see cref="Preset"/>, targets fixed by
    /// <see cref="FilterPresets"/>) or an admin's regex on one <see cref="Target"/>.
    /// </summary>
    /// <param name="Id">The row's <c>filter_id</c>; 0 until saved.</param>
    /// <param name="Mode">live, or trial: recorded in <c>detections</c> and never acted on.</param>
    /// <param name="Abuse">An admin's own rule marked as being about spam or abuse (/filter spam); by default it is one of the group's own rules.</param>
    internal sealed record FilterRule(long Id, string Kind, string Preset, string Target, string Pattern, string Action,
        bool Enabled, string Mode, DateTimeOffset? TrialUntil, long? CreatedBy, bool Abuse = false)
    {
        /// <summary><see cref="SignCategory"/> of what it finds.</summary>
        internal string Category => Kind == PresetKind
            ? FilterPresets.Get(Preset)?.Category ?? SignCategory.GroupRule
            : Abuse ? SignCategory.Abuse : SignCategory.GroupRule;

        internal const string PresetKind = "preset";
        internal const string RegexKind = "regex";
        internal const string Live = "live";
        internal const string Trial = "trial";

        /// <summary>The targets an admin's rule can have. Bios and channels come with the profile lookups (step 11).</summary>
        internal static readonly string[] Targets = { "name", "username", "text" };

        internal static FilterRule NewPreset(string preset, string action) =>
            new FilterRule(0, PresetKind, preset, null, null, action, action != null, Live, null, null);

        /// <summary>The sign it is recorded under: <c>filter:arabic</c>, <c>filter:17</c>.</summary>
        internal string Sign => Kind == PresetKind ? $"filter:{Preset}" : $"filter:{Id}";

        internal bool OnTrial => Mode == Trial;

        internal bool AppliesTo(FilterTarget target) =>
            Kind == PresetKind
                ? FilterPresets.Get(Preset) is { } preset && preset.Targets.HasFlag(target)
                : Target switch { "name" => target == FilterTarget.Name, "username" => target == FilterTarget.Username, "text" => target == FilterTarget.Text, _ => false };

        /// <summary>Whether it matches <paramref name="value"/> on <paramref name="target"/>; false for a rule that cannot run.</summary>
        internal bool Matches(FilterTarget target, string value)
        {
            if (string.IsNullOrEmpty(value) || !AppliesTo(target)) return false;
            if (Kind == PresetKind) return FilterPresets.Get(Preset)?.Matches(value) == true;
            return FilterRegex.Matches(Pattern, value);
        }
    }

    [Flags]
    internal enum FilterTarget
    {
        Text = 1,
        Name = 2,
        Username = 4
    }

    /// <summary>A prepared rule an admin switches on from the menu.</summary>
    /// <param name="Category">A script is a group's choice of language; links, numbers, wallets and disguised names are abuse.</param>
    internal sealed record FilterPreset(string Name, FilterTarget Targets, Func<string, bool> Matches,
        string Category = SignCategory.GroupRule);

    /// <summary>
    /// The prepared rules (design section 5), in menu order. Script presets match any letter of the
    /// script; the others look for the thing itself. None of them stores or reports the text.
    /// </summary>
    internal static class FilterPresets
    {
        internal const string Arabic = "arabic";
        internal const string Rtl = "rtl";

        private static readonly FilterTarget TextAndName = FilterTarget.Text | FilterTarget.Name;

        internal static readonly IReadOnlyList<FilterPreset> All = new[]
        {
            new FilterPreset(Arabic, TextAndName, t => AnyRune(t, IsArabic)),
            new FilterPreset(Rtl, TextAndName, t => AnyRune(t, r => r.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)),
            new FilterPreset("cyrillic", TextAndName, t => AnyRune(t, IsCyrillic)),
            new FilterPreset("cjk", TextAndName, t => AnyRune(t, IsCjk)),
            new FilterPreset("hebrew", TextAndName, t => AnyRune(t, r => r.Value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F)),
            new FilterPreset("devanagari", TextAndName, t => AnyRune(t, r => r.Value is >= 0x0900 and <= 0x097F or >= 0xA8E0 and <= 0xA8FF)),
            new FilterPreset("lookalikes", TextAndName, MixesLookalikes, SignCategory.Abuse),
            new FilterPreset("zero_width", TextAndName, HasInvisible, SignCategory.Abuse),
            new FilterPreset("invite_link", FilterTarget.Text, HasInviteLink, SignCategory.Abuse),
            new FilterPreset("phone", FilterTarget.Text, HasPhoneNumber, SignCategory.Abuse),
            new FilterPreset("crypto", FilterTarget.Text, HasWallet, SignCategory.Abuse),
            new FilterPreset("staff_words", FilterTarget.Name | FilterTarget.Username, HasStaffWord, SignCategory.Abuse)
        };

        private static readonly Dictionary<string, FilterPreset> ByName = All.ToDictionary(p => p.Name);

        internal static FilterPreset Get(string name) => name != null && ByName.TryGetValue(name, out var p) ? p : null;

        private static bool AnyRune(string text, Func<Rune, bool> test)
        {
            foreach (var rune in text.EnumerateRunes())
                if (test(rune)) return true;
            return false;
        }

        // The whole Arabic script: Arabic, its supplement and extended blocks, and the presentation forms.
        internal static bool IsArabic(Rune r) => r.Value is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F
            or >= 0x0870 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF && r.Value != 0xFEFF;

        private static bool IsCyrillic(Rune r) => r.Value is >= 0x0400 and <= 0x052F or >= 0x1C80 and <= 0x1C8F
            or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F;

        private static bool IsCjk(Rune r) => r.Value is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF
            or >= 0x20000 and <= 0x3134F;

        private static bool IsGreek(char c) => c is >= '\u0370' and <= '\u03FF';

        /// <summary>A word with Latin letters and Cyrillic or Greek ones: "Аdmin" with a Cyrillic А.</summary>
        internal static bool MixesLookalikes(string text)
        {
            bool latin = false, other = false;
            foreach (var c in text)
            {
                if (!char.IsLetter(c))
                {
                    latin = other = false;
                    continue;
                }
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin = true;
                else if (c is >= '\u0400' and <= '\u04FF' || IsGreek(c)) other = true;
                if (latin && other) return true;
            }
            return false;
        }

        /// <summary>
        /// Characters that show as nothing: zero-width space, word joiners, the Hangul fillers used for
        /// blank names. A zero-width joiner or non-joiner only counts between two Latin letters or digits:
        /// emoji and Persian or Indic text use them as they should.
        /// </summary>
        internal static bool HasInvisible(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '\u200B' or '\u2060' or '\u2061' or '\u2062' or '\u2063' or '\u2064' or '\u180E' or '\u115F'
                    or '\u1160' or '\u3164' or '\uFFA0') return true;
                if (c == '\uFEFF' && i > 0) return true; // a byte order mark at the start is harmless
                if (c is '\u200C' or '\u200D' && i > 0 && i < text.Length - 1 && IsAsciiLetterOrDigit(text[i - 1]) &&
                    IsAsciiLetterOrDigit(text[i + 1])) return true;
            }
            return false;
        }

        private static bool IsAsciiLetterOrDigit(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

        private static readonly Regex InviteLink = new Regex(
            @"(t\.me|telegram\.me|telegram\.dog)/(\+|joinchat/)[a-z0-9_\-]+|tg://join\?invite=",
            RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool HasInviteLink(string text) => InviteLink.IsMatch(text);

        private static readonly Regex PhoneLike = new Regex(@"\+?[0-9][0-9 \-().]{6,22}[0-9]",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

        /// <summary>
        /// An international number (+ and 8 to 15 digits, with any spaces, dashes, dots or brackets), or a
        /// local one: a 0 and 9 to 14 more digits, with spaces at most. Dates, times and prices do not count.
        /// </summary>
        internal static bool HasPhoneNumber(string text)
        {
            foreach (Match match in PhoneLike.Matches(text))
            {
                var value = match.Value;
                var digits = value.Count(char.IsAsciiDigit);
                if (value[0] == '+' && digits is >= 8 and <= 15) return true;
                if (value[0] == '0' && digits is >= 10 and <= 15 && value.All(c => char.IsAsciiDigit(c) || c == ' ')) return true;
            }
            return false;
        }

        private static readonly Regex Wallet = new Regex(
            @"\b(0x[0-9a-fA-F]{40}|bc1[02-9ac-hj-np-z]{25,62}|[13][1-9A-HJ-NP-Za-km-z]{25,34}|T[1-9A-HJ-NP-Za-km-z]{33}|(EQ|UQ)[A-Za-z0-9_\-]{46})\b",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

        /// <summary>A Bitcoin, Ethereum (and its kin), Tron or TON address.</summary>
        internal static bool HasWallet(string text) => Wallet.IsMatch(text);

        private static readonly Regex StaffWord = new Regex(
            @"(^|[^a-z])(admin|support|official|moderator|helpdesk|customer ?(care|service))",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

        /// <summary>
        /// A word starting with admin, support, official, moderator, helpdesk or "customer care/service",
        /// with lookalike letters made Latin first ("Аdmin" with a Cyrillic А). "Badminton" is not one.
        /// </summary>
        internal static bool HasStaffWord(string text) => StaffWord.IsMatch(Fold(text));

        /// <summary>Lower case, accents off, lookalike letters made Latin; spaces and punctuation kept.</summary>
        internal static string Fold(string text)
        {
            var folded = new StringBuilder(text.Length);
            foreach (var c in text.Normalize(NormalizationForm.FormKD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                var lower = char.ToLowerInvariant(c);
                folded.Append(MessageFingerprint.Lookalikes.TryGetValue(lower, out var latin) ? latin : lower);
            }
            return folded.ToString();
        }
    }

    /// <summary>
    /// Admins' own regex rules: <c>NonBacktracking | IgnoreCase | CultureInvariant</c> (no catastrophic
    /// backtracking, whatever the pattern), a 50 ms match timeout, at most <see cref="MaxLength"/>
    /// characters. Matched against the text as it is and with lookalike letters made Latin.
    /// </summary>
    internal static class FilterRegex
    {
        internal const int MaxLength = 200;
        internal const int MaxRulesPerGroup = 50;
        internal static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
        private const int CacheLimit = 20_000;

        private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

        /// <summary>Builds <paramref name="pattern"/>; on failure <paramref name="error"/> says why (in English, from .NET).</summary>
        internal static bool TryCompile(string pattern, out Regex regex, out string error)
        {
            regex = null;
            error = null;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                error = "empty";
                return false;
            }
            if (pattern.Length > MaxLength)
            {
                error = $"longer than {MaxLength} characters";
                return false;
            }
            if (Compiled.TryGetValue(pattern, out regex)) return true;
            try
            {
                regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or OutOfMemoryException)
            {
                // Lookarounds, backreferences, or a count too large for the automaton.
                error = e.Message;
                return false;
            }
            if (Compiled.Count >= CacheLimit) Compiled.Clear();
            Compiled[pattern] = regex;
            return true;
        }

        /// <summary>Whether <paramref name="pattern"/> matches <paramref name="text"/> as it is or folded; false when it cannot run.</summary>
        internal static bool Matches(string pattern, string text)
        {
            if (string.IsNullOrEmpty(text) || !TryCompile(pattern, out var regex, out _)) return false;
            try
            {
                if (regex.IsMatch(text)) return true;
                var folded = FilterPresets.Fold(text);
                return folded != text && regex.IsMatch(folded);
            }
            catch (RegexMatchTimeoutException)
            {
                // An admin's pattern too slow for this text: it does not match. Not reported, as that
                // would be one report per message.
                return false;
            }
        }
    }
}
