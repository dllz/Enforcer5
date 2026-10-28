using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using StackExchange.Redis;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Channel and website reputation (design section 8, build step 14), across groups. Every channel and website a new
    /// member points at is recorded (<see cref="MemberData.Pointer"/>); each night it is counted again how those members
    /// turned out. A channel or website is bad when at least <see cref="MinBad"/> members in at least
    /// <see cref="MinGroups"/> groups who pointed at it were removed or muted for spam or abuse (or globally banned), and
    /// few were found fine (at most one fine for every <see cref="FinePerBad"/> bad). A new member pointing at a bad one
    /// gets the <c>channel_reputation</c> sign at once, in any group. The bad ones are kept in memory, so the check is a
    /// set lookup.
    /// </summary>
    internal static class Reputation
    {
        internal const int MinBad = 2;
        internal const int MinGroups = 2;
        internal const int FinePerBad = 3;

        private static volatile HashSet<string> _bad = new();
        private static DateTime _loadedAt = DateTime.MinValue;

        internal static bool Loaded => _loadedAt > DateTime.MinValue;

        internal static bool IsBad(string kind, string value) => value != null && _bad.Contains($"{kind}:{value}");

        /// <summary>Reads the bad channels and websites again.</summary>
        internal static async Task ReloadAsync()
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                var bad = await Repositories.DetectionData.BadAsync(MinBad, MinGroups, FinePerBad);
                _bad = bad.Select(b => $"{b.Kind}:{b.Value}").ToHashSet();
                _loadedAt = Clock.UtcNow;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Reading the channel and website reputation");
            }
        }

        /// <summary>
        /// A new member pointed at a channel (<paramref name="kind"/> channel, its id) or a website (domain, its host): kept,
        /// and a sign when it is a bad one.
        /// </summary>
        internal static async Task PointedAsync(long chatId, long userId, Message message, string kind, string value)
        {
            if (value == null) return;
            MemberData.Pointer(chatId, userId, kind, value);
            if (!IsBad(kind, value)) return;
            await DetectionEngine.HitAsync(chatId, userId, message, new SignHit(SignCatalog.ChannelReputation.Name,
                kind == "channel"
                    ? JsonSerializer.Serialize(new { channel = long.Parse(value) })
                    : JsonSerializer.Serialize(new { domain = value })));
        }

        /// <summary>The host of a web address, lower case without www; null for Telegram's own addresses (channels) and anything that is not one.</summary>
        internal static string DomainOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var host = url.Trim().ToLowerInvariant();
            var scheme = host.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) host = host[(scheme + 3)..];
            var end = host.IndexOfAny(new[] { '/', '?', '#' });
            if (end >= 0) host = host[..end];
            var at = host.LastIndexOf('@');
            if (at >= 0) host = host[(at + 1)..];
            var port = host.IndexOf(':');
            if (port >= 0) host = host[..port];
            host = host.TrimEnd('.');
            if (host.StartsWith("www.")) host = host[4..];
            if (host.Length < 4 || !host.Contains('.') || host.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-'))) return null;
            return host is "t.me" or "telegram.me" or "telegram.dog" or "telegram.org" ? null : host;
        }

        internal static void SetForTests(IEnumerable<string> bad)
        {
            _bad = bad.ToHashSet();
            _loadedAt = bad.Any() ? Clock.UtcNow : DateTime.MinValue;
        }
    }

    /// <summary>
    /// Detection's nightly work on its data (design section 12, build step 14), from 03:00 UTC, once a night for both
    /// editions (the first to claim the night in Redis does it):
    /// <list type="number">
    /// <item>implicit labels: a bad label for members a global ban came after; a fine label for members first seen 30 to
    /// 120 days ago with nothing against them who are established now;</item>
    /// <item>channel and website reputation counted again (<see cref="Reputation"/>);</item>
    /// <item>retention: rows older than each group's choice (90 days, 1 year, 2 years) lose their account id and stay as
    /// anonymous features and labels; profile flags not looked at for 90 days are deleted.</item>
    /// </list>
    /// Each edition reads the bad channels and websites again every hour.
    /// </summary>
    internal static class DetectionData
    {
        internal const int NightlyHour = 3;
        internal static readonly TimeSpan FineAfter = TimeSpan.FromDays(30);
        internal static readonly TimeSpan FineWithin = TimeSpan.FromDays(120);
        internal const int FinesPerNight = 2000;
        internal const int ProfileFlagDays = 90;

        internal static string ClaimKey(DateTime utc) => $"detect:data:{utc:yyyyMMdd}";

        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Reputation.ReloadAsync();
                    await NightlyAsync();
                    await Clock.WaitAsync(Reputation.Loaded ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5), ct);
                }
            }, stop, "DetectionData");

        /// <summary>The night's work, if it is due and this edition claims it (<paramref name="force"/>: now, unclaimed, for tests).</summary>
        internal static async Task NightlyAsync(bool force = false)
        {
            if (!PostgresStorage.Ready) return;
            var now = Clock.UtcNow;
            if (!force)
            {
                if (now.Hour < NightlyHour) return;
                try
                {
                    if (!await Redis.db.StringSetAsync(ClaimKey(now), BanHistory.Edition, TimeSpan.FromDays(2), When.NotExists)) return;
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Claiming Detection's nightly work");
                    return;
                }
            }
            var at = new DateTimeOffset(now, TimeSpan.Zero);
            await Step("labelling global bans", () => Repositories.DetectionData.LabelGlobalBansAsync(at - TimeSpan.FromDays(7), BanHistory.Edition));
            await Step("labelling established members fine", () => LabelEstablishedAsync(at));
            await Step("counting the channel and website reputation", () => Repositories.DetectionData.RecomputeReputationAsync(at));
            await Step("applying the groups' retention", () => Repositories.DetectionData.ApplyRetentionAsync(at, EngineSettings.DefaultRetentionDays, ProfileFlagDays));
            await Reputation.ReloadAsync();
        }

        private static async Task Step<T>(string what, Func<Task<T>> work)
        {
            try
            {
                var result = await work();
                LogHelper.Info($"Detection nightly: {what}: {result}");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Detection's nightly work: {what}");
            }
        }

        /// <summary>New members of a month or more ago with nothing against them who are established now: fine.</summary>
        internal static async Task<int> LabelEstablishedAsync(DateTimeOffset now)
        {
            var candidates = await Repositories.DetectionData.UnlabelledNewcomersAsync(now - FineWithin, now - FineAfter, FinesPerNight);
            var labelled = 0;
            foreach (var (chatId, userId) in candidates)
            {
                if (await MemberTrust.OfAsync(chatId, userId) != TrustLevel.Established) continue;
                var recorded = await Repositories.DetectionLog.RecordLabelAsync(new LabelEvent(chatId, userId, "fine", "fine", "established",
                    null, null, null, null, null, null, $"established:{chatId}:{userId}", BanHistory.Edition, now));
                if (recorded != null) labelled++;
            }
            return labelled;
        }
    }
}
