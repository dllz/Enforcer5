using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// A group's config as Redis held it until rollout step 15, read for the import on the group's first use and,
    /// until that has run, to serve the group; never written. The only code that knows the old encodings:
    /// <list type="bullet">
    /// <item><c>chat:{id}:settings</c>: <c>Welcome</c> "yes" is off; <c>DeleteLastWelcome</c> "no" is on; <c>Rules</c>,
    /// <c>About</c>, <c>Modlist</c>, <c>Extra</c>, <c>Help</c> "yes" is in private. A missing field is what the old code
    /// did with it: welcome on, the last welcome kept, answers in the group (Help in private: the last one-time
    /// migration set it on the group's next message). <c>Report</c>/<c>Flagged</c>: reports on
    /// (Daniel, 2026-11-06). <c>logchat</c>, counted only while the group is in <c>logChatGroups</c>.</item>
    /// <item><c>chat:{id}:welcome</c>: <c>type</c>, <c>content</c>, <c>media</c>, <c>hasmedia</c>; a custom welcome has its
    /// GIF only with <c>hasmedia</c> "true"; a media one keeps its GIF in <c>media</c> (before 2017 in <c>content</c>).</item>
    /// <item><c>chat:{id}:rules</c>, <c>chat:{id}:about</c>, <c>chat:{id}:language</c> (strings), <c>chat:{id}links</c>
    /// (field <c>link</c>; "no" is none).</item>
    /// <item><c>chat:{id}:extra</c> (trigger to value) and <c>chat:{id}:extra:{trigger}</c> (<c>mediaid</c>, <c>caption</c>),
    /// see <see cref="ExtraConfig.FromLegacy"/>.</item>
    /// </list>
    /// </summary>
    internal static class LegacyGroupConfig
    {
        internal static async Task<GroupConfig> ReadAsync(IDatabaseAsync db, long chatId)
        {
            var settingsTask = db.HashGetAllAsync($"chat:{chatId}:settings");
            var welcomeTask = db.HashGetAllAsync($"chat:{chatId}:welcome");
            var logTask = db.SetContainsAsync("logChatGroups", chatId);
            var rulesTask = db.StringGetAsync($"chat:{chatId}:rules");
            var aboutTask = db.StringGetAsync($"chat:{chatId}:about");
            var languageTask = db.StringGetAsync($"chat:{chatId}:language");
            var linkTask = db.HashGetAsync($"chat:{chatId}links", "link");
            var extrasTask = db.HashGetAllAsync($"chat:{chatId}:extra");
            await Task.WhenAll(settingsTask, welcomeTask, logTask, rulesTask, aboutTask, languageTask, linkTask, extrasTask);

            var settings = (settingsTask.Result ?? Array.Empty<HashEntry>()).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            string Field(string name) => settings.TryGetValue(name, out var value) ? value : null;
            var flags = new Dictionary<ConfigFlag, bool>
            {
                [ConfigFlag.Welcome] = Field("Welcome") != "yes",
                [ConfigFlag.DeleteLastWelcome] = Field("DeleteLastWelcome") == "no",
                [ConfigFlag.Reports] = true,
                [ConfigFlag.RulesPrivate] = Field("Rules") == "yes",
                [ConfigFlag.AboutPrivate] = Field("About") == "yes",
                [ConfigFlag.ModlistPrivate] = Field("Modlist") == "yes",
                [ConfigFlag.ExtraPrivate] = Field("Extra") == "yes",
                // NewSetting3 set a missing Help to "yes" on the group's next message, until rollout step 15.
                [ConfigFlag.HelpPrivate] = Field("Help") is null or "yes"
            };
            long? logChat = logTask.Result && long.TryParse(Field("logchat"), out var log) ? log : null;

            var content = new Dictionary<ContentKind, string>();
            if (rulesTask.Result.HasValue) content[ContentKind.Rules] = rulesTask.Result.ToString();
            if (aboutTask.Result.HasValue) content[ContentKind.About] = aboutTask.Result.ToString();
            if (!languageTask.Result.IsNullOrEmpty) content[ContentKind.Language] = languageTask.Result.ToString();
            if (!linkTask.Result.IsNullOrEmpty && linkTask.Result != "no") content[ContentKind.Link] = linkTask.Result.ToString();

            var extras = ImmutableSortedDictionary<string, ExtraConfig>.Empty.WithComparers(StringComparer.Ordinal).ToBuilder();
            var entries = extrasTask.Result ?? Array.Empty<HashEntry>();
            var sides = await Task.WhenAll(entries.Select(e => db.HashGetAllAsync($"chat:{chatId}:extra:{e.Name}")));
            for (var i = 0; i < entries.Length; i++)
            {
                var side = (sides[i] ?? Array.Empty<HashEntry>()).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
                var extra = ExtraConfig.FromLegacy(entries[i].Value.ToString(), side.GetValueOrDefault("mediaid"), side.GetValueOrDefault("caption"));
                if (extra != null && entries[i].Name.ToString().Length > 0) extras[entries[i].Name.ToString()] = extra;
            }

            return new GroupConfig
            {
                Flags = flags.ToImmutableDictionary(),
                Welcome = Welcome(welcomeTask.Result),
                LogChat = logChat,
                Content = content.ToImmutableDictionary(),
                Extras = extras.ToImmutable()
            };
        }

        internal static WelcomeConfig Welcome(HashEntry[] hash)
        {
            var fields = (hash ?? Array.Empty<HashEntry>()).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
            string Field(string name) => fields.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
            switch (Field("type"))
            {
                case "media":
                    var gif = Field("media") ?? Field("content");
                    return gif == null ? WelcomeConfig.Default with { Type = "media", Content = null }
                        : new WelcomeConfig("media", null, gif, "legacy_unknown");
                case "custom":
                    var withGif = Field("hasmedia") == "true" ? Field("media") : null;
                    return new WelcomeConfig("custom", Field("content") ?? "", withGif, withGif != null ? "legacy_unknown" : null);
                case "composed":
                    return new WelcomeConfig("composed", Field("content") ?? "no", null, null);
                default:
                    // Never set: no welcome text, as before (the greeting alone was never sent without a type).
                    return new WelcomeConfig("composed", null, null, null);
            }
        }
    }
}
