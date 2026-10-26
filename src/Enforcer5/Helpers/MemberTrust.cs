using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Storage;
using StackExchange.Redis;

namespace Enforcer5.Helpers
{
    /// <summary>How far a member of a group is trusted. Only untrusted members ever count towards a raid.</summary>
    internal enum TrustLevel
    {
        /// <summary>An admin (or the creator) of the group.</summary>
        Admin,
        /// <summary>On one of the group's own lists: allowed to elevate (<c>chat:{id}:auth</c>) or exempt from the checks (<c>chat:{id}:watch</c>).</summary>
        Approved,
        /// <summary>Has been there and talked as much as the group's usual active member.</summary>
        Established,
        /// <summary>Everyone else. Raid detection calls a new member who also matches raid signs a suspect.</summary>
        New
    }

    /// <summary>
    /// Works out <see cref="TrustLevel"/>s, from Redis so it works without PostgreSQL:
    /// <c>{chat}:users:{user}</c> holds the message count (<c>msgs</c>), when they first talked there
    /// (<c>first</c>, only for members whose first message came after it was added) and when they last
    /// joined (<c>joined</c>). The thresholds scale with the group (<see cref="GroupNorms"/>, recomputed
    /// nightly in PostgreSQL): from 20 messages in a quiet group to 1000 in a busy one, 3 to 30 days.
    /// A member with no <c>first</c> but a count that high was there before any of this, and counts
    /// as established.
    /// </summary>
    internal static class MemberTrust
    {
        internal const int DefaultMessages = 20;
        internal const int DefaultDays = 7;

        private static readonly ConcurrentDictionary<long, GroupNorms> Norms = new();

        /// <summary>Messages and days an established member of <paramref name="chatId"/> has.</summary>
        internal static (int Messages, int Days) Thresholds(long chatId) =>
            Norms.TryGetValue(chatId, out var n) ? (n.EstablishedMessages, n.EstablishedDays) : (DefaultMessages, DefaultDays);

        /// <summary>Usual joins an hour in <paramref name="chatId"/>; null when not known yet.</summary>
        internal static double? JoinsPerHour(long chatId) =>
            Norms.TryGetValue(chatId, out var n) && n.ActiveMembers >= 10 ? n.JoinsPerHour : null;

        /// <summary>
        /// The member's level. <paramref name="isAdmin"/> is asked last and only when nothing else made
        /// them trusted, since it may cost a lookup.
        /// </summary>
        internal static async Task<TrustLevel> OfAsync(long chatId, long userId, Func<bool> isAdmin = null)
        {
            var db = Redis.db;
            var member = db.HashGetAsync($"{chatId}:users:{userId}", new RedisValue[] { "msgs", "first", "joined" });
            var auth = db.SetContainsAsync($"chat:{chatId}:auth", userId);
            var watch = db.SetContainsAsync($"chat:{chatId}:watch", userId);
            await Task.WhenAll(member, auth, watch);
            if (auth.Result || watch.Result) return TrustLevel.Approved;
            var values = member.Result ?? Array.Empty<RedisValue>();
            var level = Level(chatId, Long(values, 0), Long(values, 1), Long(values, 2), Clock.UtcNow);
            if (level == TrustLevel.New && isAdmin != null && isAdmin()) return TrustLevel.Admin;
            return level;
        }

        /// <summary>What /user shows: the level and what it is based on.</summary>
        internal sealed record Description(TrustLevel Level, long Messages, DateTime? SinceUtc, int NeedMessages, int NeedDays, bool InSlowLane);

        /// <summary>The member's level with the counts behind it, for /user. <paramref name="isAdmin"/> as in <see cref="OfAsync"/>.</summary>
        internal static async Task<Description> DescribeAsync(long chatId, long userId, Func<bool> isAdmin)
        {
            var db = Redis.db;
            var values = await db.HashGetAsync($"{chatId}:users:{userId}", new RedisValue[] { "msgs", "first", "joined" }) ?? Array.Empty<RedisValue>();
            var level = await OfAsync(chatId, userId, isAdmin);
            var first = Long(values, 1);
            var joined = Long(values, 2);
            var since = new[] { first, joined }.Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty().Max();
            var (needMessages, needDays) = Thresholds(chatId);
            var slow = false;
            try { slow = (await Repositories.SlowLane.GetAsync(userId))?.Active(Clock.UtcNow) == true; }
            catch (Exception e) { ErrorReporter.Report(e, $"Reading the slow lane of {userId}"); }
            return new Description(level, Long(values, 0) ?? 0,
                since > 0 ? DateTimeOffset.FromUnixTimeSeconds(since).UtcDateTime : null, needMessages, needDays, slow);
        }

        /// <summary>The rule itself, for tests: established or new, from the counts.</summary>
        internal static TrustLevel Level(long chatId, long? messages, long? firstUnix, long? joinedUnix, DateTime now)
        {
            var (needMessages, needDays) = Thresholds(chatId);
            if ((messages ?? 0) < needMessages) return TrustLevel.New;
            var days = TimeSpan.FromDays(needDays);
            if (joinedUnix is { } joined && now - DateTimeOffset.FromUnixTimeSeconds(joined).UtcDateTime < days) return TrustLevel.New;
            if (firstUnix is { } first && now - DateTimeOffset.FromUnixTimeSeconds(first).UtcDateTime < days) return TrustLevel.New;
            return TrustLevel.Established;
        }

        private static long? Long(RedisValue[] values, int i) =>
            values.Length > i && values[i].HasValue && long.TryParse(values[i].ToString(), out var v) ? v : null;

        /// <summary>Uses these norms, for tests.</summary>
        internal static void SetNormsForTests(GroupNorms norms)
        {
            if (norms == null) Norms.Clear();
            else Norms[norms.TelegramChatId] = norms;
        }

        // Taken by whichever edition recomputes the norms, so they do it once a night between them.
        private const long NormsLock = 0x454E464E4F524D53; // "ENFNORMS"

        /// <summary>
        /// Loads the norms hourly, and once a day recomputes them and drops old capture. Off without
        /// PostgreSQL.
        /// </summary>
        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                var nextRecompute = Clock.UtcNow.Date.AddDays(1).AddHours(3); // 03:00 UTC
                while (!ct.IsCancellationRequested)
                {
                    if (PostgresStorage.Ready)
                    {
                        try
                        {
                            if (Clock.UtcNow >= nextRecompute)
                            {
                                nextRecompute = nextRecompute.AddDays(1);
                                await RecomputeOnceAsync();
                            }
                            foreach (var n in await Repositories.RaidCapture.GetNormsAsync()) Norms[n.TelegramChatId] = n;
                        }
                        catch (Exception e)
                        {
                            ErrorReporter.Report(e, "Loading the group norms");
                        }
                    }
                    await Clock.WaitAsync(TimeSpan.FromHours(1), ct);
                }
            }, stop, "MemberTrustNorms");

        private static async Task RecomputeOnceAsync()
        {
            await using var connection = await PostgresStorage.Database.OpenConnectionAsync();
            if (!await connection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(@NormsLock)", new { NormsLock })) return;
            try
            {
                var now = DateTimeOffset.UtcNow;
                await Repositories.RaidCapture.RecomputeNormsAsync(now);
                await Repositories.RaidCapture.CleanUpAsync(now);
                await Repositories.RaidTelemetry.CleanUpAsync(now);
            }
            finally
            {
                await connection.ExecuteAsync("SELECT pg_advisory_unlock(@NormsLock)", new { NormsLock });
            }
        }
    }
}
