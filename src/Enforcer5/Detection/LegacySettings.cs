using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The settings of the automatic checks as they were kept in Redis before Detection v2, read
    /// once per group and imported into PostgreSQL (the lazy import, design section 2). This is the
    /// only code that knows the old encodings: <c>Flood = "yes"</c> turned flood <b>off</b>,
    /// <c>MuteOnJoin = "no"</c> turned it <b>on</b>, length actions were <c>Warn</c> with a capital W,
    /// a missing <c>mediamax</c> meant 2, and so on. Nothing here writes to Redis; the old hashes are
    /// left in place for a binary rollback, but are no longer kept up to date.
    /// </summary>
    internal static class LegacySettings
    {
        /// <summary>The old hashes and sets of one group, as read.</summary>
        internal sealed record Stored(
            HashEntry[] Settings,
            HashEntry[] Flood,
            HashEntry[] FloodExceptions,
            HashEntry[] Media,
            HashEntry[] Char,
            HashEntry[] NameLength,
            HashEntry[] TextLength,
            HashEntry[] WarnSettings,
            HashEntry[] OtherSettings,
            IReadOnlyList<InlineBotBlock> InlineBots,
            IReadOnlyList<string> StickerSets,
            bool ChannelPostsEnabled,
            IReadOnlyList<AllowedChannel> AllowedChannels);

        /// <summary>Reads everything one group had in Redis, pipelined. Throws if a read fails.</summary>
        internal static async Task<Stored> ReadAsync(IDatabaseAsync db, long chatId)
        {
            Task<HashEntry[]> Hash(string name) => db.HashGetAllAsync($"chat:{chatId}:{name}");
            var settings = Hash("settings");
            var flood = Hash("flood");
            var floodExceptions = Hash("floodexceptions");
            var media = Hash("media");
            var characters = Hash("char");
            var nameLength = Hash("antinamelengthsettings");
            var textLength = Hash("antitextlengthsettings");
            var warnSettings = Hash("warnsettings");
            var otherSettings = Hash("otherSettings");
            var inlineBots = new RedisInlineBotBlockRepository(() => db).GetAsync(chatId);
            var stickerSets = new RedisStickerSetBlockRepository(() => db).GetAsync(chatId);
            var channelPosts = new RedisChannelPostRepository(() => db);
            var channelPostsEnabled = channelPosts.IsEnabledAsync(chatId);
            var allowedChannels = channelPosts.GetAllowedAsync(chatId);
            await Task.WhenAll(settings, flood, floodExceptions, media, characters, nameLength, textLength,
                warnSettings, otherSettings, inlineBots, stickerSets, channelPostsEnabled, allowedChannels);
            return new Stored(settings.Result ?? Array.Empty<HashEntry>(), flood.Result ?? Array.Empty<HashEntry>(),
                floodExceptions.Result ?? Array.Empty<HashEntry>(), media.Result ?? Array.Empty<HashEntry>(),
                characters.Result ?? Array.Empty<HashEntry>(), nameLength.Result ?? Array.Empty<HashEntry>(),
                textLength.Result ?? Array.Empty<HashEntry>(), warnSettings.Result ?? Array.Empty<HashEntry>(),
                otherSettings.Result ?? Array.Empty<HashEntry>(), inlineBots.Result ?? Array.Empty<InlineBotBlock>(),
                stickerSets.Result ?? Array.Empty<string>(), channelPostsEnabled.Result,
                allowedChannels.Result ?? Array.Empty<AllowedChannel>());
        }

        /// <summary>What the checks made of the old values. Pure.</summary>
        internal static DetectionPolicy Map(Stored stored)
        {
            static string Field(HashEntry[] entries, string name) => MessageContext.Field(entries, name) is var value && value.HasValue
                ? value.ToString()
                : null;
            static int? Int(string value) => int.TryParse(value, out var number) ? number : null;
            static int? Positive(string value) => int.TryParse(value, out var number) && number > 0 ? number : null;
            static string OneOf(string value, params string[] allowed) => allowed.Contains(value) ? value : null;

            var counted = new Dictionary<string, bool>();
            foreach (var entry in stored.FloodExceptions)
                counted[entry.Name.ToString()] = !entry.Value.Equals("no");

            var blocked = new Dictionary<string, bool>();
            foreach (var entry in stored.Media)
                if (!entry.Name.Equals("action"))
                    blocked[entry.Name.ToString()] = entry.Value.Equals("blocked");

            // The length checks switch on "kick", "ban", "Warn" and "tempban": anything else, a
            // lowercase "warn" too, falls to "no action chosen".
            static string LengthAction(string value) => value == "Warn" ? "warn" : OneOf(value, "kick", "ban", "tempban");
            static LengthSettings Length(HashEntry[] entries) => new LengthSettings(
                Field(entries, "enabled") == "yes",
                Positive(Field(entries, "maxlength")),
                Positive(Field(entries, "maxlines")),
                LengthAction(Field(entries, "action")));

            return new DetectionPolicy(
                Version: 0,
                Flood: new FloodSettings(
                    Enabled: Field(stored.Settings, "Flood") != "yes",
                    MaxMessages: Int(Field(stored.Flood, "MaxFlood")),
                    Action: OneOf(Field(stored.Flood, "ActionFlood"), "kick", "warn", "tempban", "ban"),
                    Counted: counted),
                Media: new MediaSettings(blocked,
                    OneOf(Field(stored.Media, "action"), "kick", "tempban", "ban"),
                    Int(Field(stored.WarnSettings, "mediamax"))),
                MuteOnJoin: Field(stored.Settings, "MuteOnJoin") == "no",
                Scripts: new ScriptSettings(
                    OneOf(Field(stored.Char, "Arab"), "kick", "ban", "tempban"),
                    OneOf(Field(stored.Char, "Rtl"), "kick", "ban", "tempban")),
                TextLength: Length(stored.TextLength),
                NameLength: Length(stored.NameLength) with { MaxLines = null },
                Warns: new WarnSettings(
                    Methods.MaxWarns(MessageContext.Field(stored.WarnSettings, "max")),
                    Field(stored.WarnSettings, "type") == "ban" ? WarnSettings.Ban : WarnSettings.Kick),
                TempbanMinutes: Duration.StoredMinutesOrDefault(Field(stored.OtherSettings, "tempbanTime")),
                TempmuteMinutes: Duration.StoredMinutesOrDefault(Field(stored.OtherSettings, "tempMuteTime"), Duration.MaximumMinutes),
                InlineBots: stored.InlineBots.Distinct().ToList(),
                StickerSets: stored.StickerSets.Distinct(StringComparer.Ordinal).ToList(),
                ChannelPosts: new ChannelPostSettings(stored.ChannelPostsEnabled,
                    stored.AllowedChannels.GroupBy(c => c.Id).Select(g => g.First()).ToList()),
                Engine: EngineSettings.Default);
        }
    }
}
