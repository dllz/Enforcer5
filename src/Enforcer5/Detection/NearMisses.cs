using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Comparisons that came close to a threshold and stayed under it (design section 11, build step 13), buffered and
    /// written to <c>detection_near_misses</c> with the detections once a minute. Once per member, sign and day: enough to
    /// see what a lower threshold would catch, without a row for every message. Best effort, like the detections.
    /// </summary>
    internal static class NearMisses
    {
        internal const int Capacity = 20_000;
        private const int RememberedLimit = 100_000;
        internal static readonly TimeSpan OncePer = TimeSpan.FromHours(24);

        private static readonly ConcurrentQueue<NearMissRecord> Buffer = new();
        private static readonly ConcurrentDictionary<(long Chat, long User, string Sign), DateTime> Seen = new();
        private static int _count;

        /// <summary>Whether a near miss of this member and sign would be recorded now (none in the last day); does not mark it.</summary>
        internal static bool Due(long chatId, long userId, string sign) =>
            PostgresStorage.Ready && (!Seen.TryGetValue((chatId, userId, sign), out var at) || Clock.UtcNow - at >= OncePer);

        /// <summary>Not recorded, and not looked at again for a day (the member is not new).</summary>
        internal static void Skip(long chatId, long userId, string sign) => Seen[(chatId, userId, sign)] = Clock.UtcNow;

        /// <summary>
        /// <paramref name="userId"/>'s <paramref name="sign"/> came to <paramref name="similarity"/> against
        /// <paramref name="threshold"/>. <paramref name="evidence"/> is ids and counts only, serialised to JSON.
        /// </summary>
        internal static void Add(long chatId, long userId, string sign, double similarity, double threshold, object evidence = null)
        {
            if (!PostgresStorage.Ready || userId == 0) return;
            var now = Clock.UtcNow;
            var fresh = false;
            Seen.AddOrUpdate((chatId, userId, sign), _ => { fresh = true; return now; },
                (_, at) => { if (now - at < OncePer) return at; fresh = true; return now; });
            if (!fresh) return;
            if (Seen.Count > RememberedLimit)
            {
                foreach (var old in Seen.Where(s => now - s.Value >= OncePer).Select(s => s.Key).ToList()) Seen.TryRemove(old, out _);
                if (Seen.Count > RememberedLimit) Seen.Clear();
            }
            Buffer.Enqueue(new NearMissRecord(chatId, userId, sign, Math.Round(similarity, 3), threshold,
                evidence == null ? null : System.Text.Json.JsonSerializer.Serialize(evidence), BanHistory.Edition,
                new DateTimeOffset(now, TimeSpan.Zero)));
            if (System.Threading.Interlocked.Increment(ref _count) > Capacity && Buffer.TryDequeue(out _))
                System.Threading.Interlocked.Decrement(ref _count);
        }

        /// <summary>Writes what is buffered. Never throws; a failed batch is reported and dropped (it is only for tuning).</summary>
        internal static async Task FlushAsync()
        {
            if (!PostgresStorage.Ready) return;
            var batch = new List<NearMissRecord>();
            while (Buffer.TryDequeue(out var record))
            {
                System.Threading.Interlocked.Decrement(ref _count);
                batch.Add(record);
            }
            if (batch.Count == 0) return;
            try
            {
                await Repositories.DetectionLog.WriteNearMissesAsync(batch);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing {batch.Count} near misses");
            }
        }

        internal static void ResetForTests()
        {
            while (Buffer.TryDequeue(out _)) { }
            Seen.Clear();
            _count = 0;
        }
    }
}
