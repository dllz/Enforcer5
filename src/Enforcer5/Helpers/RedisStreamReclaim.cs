using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Reclaims stream entries abandoned by a dead consumer, using only commands available in
    /// Redis 5.0.
    /// <para>
    /// This replaces <c>XAUTOCLAIM</c>, which was introduced in Redis 6.2. Production runs
    /// <b>6.0.9</b>, where that command does not exist - it fails with
    /// <c>ERR unknown command 'XAUTOCLAIM'</c>. Neither stream consumer has shipped yet, so this
    /// was never a live fault; it would have become one on the first deploy, with both reclaim
    /// loops catching the error, logging it and retrying forever while nothing was ever reclaimed.
    /// </para>
    /// <para>
    /// <c>XPENDING</c> plus <c>XCLAIM</c> costs one extra round trip per pass but works on every
    /// version in play, so it is used unconditionally rather than behind a version probe.
    /// </para>
    /// </summary>
    internal static class RedisStreamReclaim
    {
        /// <summary>
        /// Claims up to <paramref name="count"/> entries idle for at least
        /// <paramref name="minIdleMilliseconds"/> and returns them for reprocessing.
        /// Returns an empty array when there is nothing to reclaim.
        /// </summary>
        internal static async Task<StreamEntry[]> ClaimStaleAsync(IDatabase database, RedisKey key,
            RedisValue group, RedisValue consumer, long minIdleMilliseconds, int count,
            CancellationToken cancellationToken = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            cancellationToken.ThrowIfCancellationRequested();

            RedisValue[] stale;
            try
            {
                stale = await ReadStalePendingIdsAsync(database, key, group, minIdleMilliseconds, count);
            }
            catch (RedisServerException e) when (e.Message.Contains("NOGROUP", StringComparison.Ordinal))
            {
                // The group is created lazily elsewhere; nothing to reclaim until it exists.
                return Array.Empty<StreamEntry>();
            }

            if (stale.Length == 0) return Array.Empty<StreamEntry>();

            cancellationToken.ThrowIfCancellationRequested();

            // XCLAIM re-checks the idle time server-side, so a consumer that woke up between the
            // XPENDING and here keeps its entry rather than having it stolen.
            var claimed = await database.StreamClaimAsync(key, group, consumer,
                minIdleMilliseconds, stale);

            // An entry deleted while pending comes back from XCLAIM with no fields *and a null id*,
            // so tombstones are identified by subtracting what came back usable from what was
            // requested. XAUTOCLAIM evicted those from the pending list itself; here they have to
            // be acknowledged explicitly or the reclaimer retries the same tombstone forever.
            var usable = claimed
                .Where(entry => !entry.Id.IsNull && entry.Values != null && entry.Values.Length > 0)
                .ToArray();

            var usableIds = usable.Select(entry => entry.Id).ToHashSet();
            var tombstones = stale.Where(id => !usableIds.Contains(id)).ToArray();
            if (tombstones.Length > 0)
                await database.StreamAcknowledgeAsync(key, group, tombstones);

            return usable;
        }

        /// <summary>
        /// Issues the extended <c>XPENDING key group - + count</c> form directly and filters by
        /// idle time client-side.
        /// <para>
        /// StackExchange.Redis requires a consumer name on its typed helper, whereas a reclaimer
        /// wants every consumer's pending entries. <c>XPENDING</c>'s own <c>IDLE</c> filter is also
        /// 6.2+, so the idle time is filtered here from what this form already returns.
        /// </para>
        /// </summary>
        private static async Task<RedisValue[]> ReadStalePendingIdsAsync(IDatabase database,
            RedisKey key, RedisValue group, long minIdleMilliseconds, int count)
        {
            var raw = await database.ExecuteAsync("XPENDING", key, group, "-", "+", count);
            if (raw == null || raw.IsNull) return Array.Empty<RedisValue>();

            var rows = (RedisResult[])raw;
            if (rows == null || rows.Length == 0) return Array.Empty<RedisValue>();

            var stale = new List<RedisValue>(rows.Length);
            foreach (var row in rows)
            {
                // Each row is: id, consumer, idle-time-ms, delivery-count.
                var fields = (RedisResult[])row;
                if (fields == null || fields.Length < 3) continue;
                if (!long.TryParse(fields[2].ToString(), out var idleMilliseconds)) continue;
                if (idleMilliseconds < minIdleMilliseconds) continue;
                stale.Add(fields[0].ToString());
            }

            return stale.ToArray();
        }
    }
}
