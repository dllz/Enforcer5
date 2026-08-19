using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Storage.Operations;
using StackExchange.Redis;

namespace Enforcer5.Storage.Loading
{
    /// <param name="OrphanedDuplicates">
    /// Extra global entries for a pair already handled. These are leaked rows: the global hash is
    /// keyed by expiry second, so a re-ban lands in a new slot and the old row is only removed if
    /// the per-group key still exists to point at it.
    /// </param>
    internal sealed record TempBanIndexResult(int GlobalEntries, int AlreadyPerGroup, int Materialised,
        int Lapsed, int Malformed, int OrphanedDuplicates);

    /// <summary>
    /// Gives every tempban a per-group home, once, so nothing afterwards has to read the global hash.
    /// <para>
    /// The process-wide <c>tempbanned</c> hash is keyed by expiry <em>second</em> with the group
    /// buried in its value, which makes it useless for asking "what are this group's tempbans?"
    /// without reading all of it. That is blocker B1 for correctness and section 9.2 H7 for
    /// throughput: under a per-group crawler it turns an O(group) question into an O(estate) one,
    /// once per group.
    /// </para>
    /// <para>
    /// The per-group keys already exist and carry the same expiry - <c>chat:{g}:tempbanned:{u}</c>
    /// stores exactly the value the global hash uses as its field. They are simply not complete,
    /// because a global entry can be lost to a collision and a per-group key can outlive its global
    /// counterpart. Measured on the production copy: 102 global pairs, 89 per-group, 56 shared.
    /// </para>
    /// <para>
    /// This copies the missing direction so the per-group keys become the complete set. It is
    /// additive and idempotent: an existing key is never rewritten, so a re-run converges and a
    /// live tempban is never given a new expiry.
    /// </para>
    /// </summary>
    internal static class TempBanIndexMaterialiser
    {
        internal static async Task<TempBanIndexResult> RunAsync(bool apply,
            CancellationToken cancellationToken = default)
        {
            var global = 0;
            var already = 0;
            var materialised = 0;
            var lapsed = 0;
            var malformed = 0;
            var duplicates = 0;
            var now = DateTimeOffset.UtcNow;
            // The global hash leaks: a per-group key that lapses leaves RemoveTempBan unable to find
            // the entry to delete, so it is orphaned, and every re-ban of that user adds another.
            // One user in the production copy holds 30 entries, and 109 entries cover 76 distinct
            // pairs. Tracking what has been handled keeps a dry run honest - without it the count
            // reports entries rather than the keys it would actually write.
            var handled = new HashSet<(long GroupId, long UserId)>();

            foreach (var (hash, setSuffix) in new[]
                     {
                         ("tempbanned", "tempbanned"),
                         ("tempbannedPremium", "tempbannedPremium")
                     })
            {
                foreach (var entry in await Redis.db.HashGetAllAsync(hash))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    global++;

                    var parsed = LegacyOperationParsers.ParseTempBan(entry.Name.ToString(),
                        entry.Value.ToString(), Editions.Normal);
                    if (!parsed.IsSuccess || parsed.Value == null) { malformed++; continue; }

                    var groupId = parsed.Value.GroupId;
                    var userId = parsed.Value.UserId;
                    var expiryKey = $"chat:{groupId}:tempbanned:{userId}";

                    if (!handled.Add((groupId, userId))) { duplicates++; continue; }
                    if (await Redis.db.KeyExistsAsync(expiryKey)) { already++; continue; }

                    // A lapsed ban has nothing to materialise - the per-group key is absent because
                    // it correctly expired, and recreating it would resurrect a finished sanction.
                    var remaining = parsed.Value.ExpiresAt - now;
                    if (remaining <= TimeSpan.Zero) { lapsed++; continue; }

                    materialised++;
                    if (!apply) continue;

                    // The value is the global field verbatim, because RemoveTempBan reads this key
                    // and uses it to find the global entry to delete. Writing anything else would
                    // orphan the global row on the next unban.
                    await Redis.db.StringSetAsync(expiryKey, entry.Name.ToString(), remaining);
                    await Redis.db.SetAddAsync($"chat:{groupId}:{setSuffix}", userId);
                }
            }

            LogHelper.Info($"Tempban index{(apply ? string.Empty : " (dry run)")}: global={global}, " +
                           $"already-per-group={already}, materialised={materialised}, " +
                           $"lapsed={lapsed}, malformed={malformed}, orphaned-duplicates={duplicates}.");
            return new TempBanIndexResult(global, already, materialised, lapsed, malformed, duplicates);
        }
    }
}
