using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Storage.Cutover
{
    /// <summary>
    /// Per-group buffer of keys mutated while a copy is in flight.
    /// <para>
    /// A group's copy runs outside any lock, so live traffic keeps mutating Redis underneath it.
    /// The buffer records <em>which keys changed</em>, and the migrator re-reads them at drain time.
    /// Coordinates rather than values, for the same reason the group-settings outbox carries
    /// coordinates: re-reading converges on current truth, whereas replaying a delta after a crash
    /// double-counts.
    /// </para>
    /// <para>
    /// A list, not a stream. The group-settings outbox needs a consumer group because two writer
    /// tasks share it; a drain buffer is read only by the holder of the group's lease, so there is
    /// no concurrency to coordinate and <c>RPUSH</c>/<c>LRANGE</c>/<c>LTRIM</c> is the whole
    /// protocol. It also means <c>XAUTOCLAIM</c>, absent on 6.0.9, never enters the picture.
    /// </para>
    /// </summary>
    internal static class GroupDrainBuffer
    {
        /// <summary>
        /// Appends only if the buffer is below capacity, and reports which happened.
        /// <para>
        /// The capacity check precedes the append inside one script, so the buffer can never be
        /// left holding a partial record of a mutation it did not capture.
        /// </para>
        /// </summary>
        private const string AppendScript = @"
if redis.call('LLEN', KEYS[1]) >= tonumber(ARGV[2]) then
  return 0
end
redis.call('RPUSH', KEYS[1], ARGV[1])
return 1";

        internal static string KeyFor(long telegramChatId) => $"migbuf:{telegramChatId}";

        /// <summary>
        /// Records that a key belonging to this group was mutated.
        /// <para>
        /// Cheap by design for the common case: a group not being copied costs one dictionary
        /// lookup and returns, so the overwhelming majority of traffic pays nothing.
        /// </para>
        /// <para>
        /// <b>Overflow aborts the group, never the user's write.</b> This is a deliberate
        /// divergence from <c>GroupSettingsStore</c>, where outbox backpressure surfaces as a failed
        /// mutation. That is defensible for a shadow write; here the caller is a live moderation
        /// action and failing it would make the migration visible to users. Losing a cutover
        /// attempt costs nothing - the group stays on Redis and the PostgreSQL rows become garbage
        /// - so the cheap failure is the right one.
        /// </para>
        /// </summary>
        internal static async Task AppendAsync(long telegramChatId, string mutatedKey)
        {
            if (!StorageRouting.GroupRequiresDrainBuffer(telegramChatId)) return;

            try
            {
                var appended = (long)await Redis.db.ScriptEvaluateAsync(AppendScript,
                    new RedisKey[] { KeyFor(telegramChatId) },
                    new RedisValue[] { mutatedKey, Capacity });
                if (appended == 1) return;

                await AbandonAsync(telegramChatId,
                    $"drain buffer reached its {Capacity} entry capacity");
            }
            catch (Exception e)
            {
                // The mutation itself already succeeded. An unrecorded mutation means the copy is
                // no longer trustworthy, so abandon the cutover rather than let it complete on a
                // buffer with a hole in it.
                await AbandonAsync(telegramChatId, $"drain buffer append failed: {e.Message}");
            }
        }

        /// <summary>
        /// Fire-and-forget form for synchronous call sites, of which the moderation paths are
        /// almost entirely composed. Failures abandon the group rather than propagating.
        /// </summary>
        internal static void Append(long telegramChatId, string mutatedKey)
        {
            if (!StorageRouting.GroupRequiresDrainBuffer(telegramChatId)) return;
            _ = AppendAsync(telegramChatId, mutatedKey);
        }

        /// <summary>
        /// Reads the buffered keys without removing them, deduplicated.
        /// <para>
        /// Deduplicated because a hot key is mutated repeatedly and the migrator only needs to
        /// re-read it once. Entries are removed by <see cref="TrimAsync"/> only after the copy has
        /// used them, so a crash mid-drain re-reads rather than loses.
        /// </para>
        /// </summary>
        internal static async Task<IReadOnlyList<string>> PeekAsync(long telegramChatId, int limit)
        {
            var entries = await Redis.db.ListRangeAsync(KeyFor(telegramChatId), 0, limit - 1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>(entries.Length);
            foreach (var entry in entries)
                if (seen.Add(entry.ToString())) result.Add(entry.ToString());
            return result;
        }

        /// <summary>Removes the entries just consumed, leaving anything appended since.</summary>
        internal static Task TrimAsync(long telegramChatId, int consumed) =>
            Redis.db.ListTrimAsync(KeyFor(telegramChatId), consumed, -1);

        internal static Task<long> LengthAsync(long telegramChatId) =>
            Redis.db.ListLengthAsync(KeyFor(telegramChatId));

        /// <summary>Drops the buffer once the group has cut over or been abandoned.</summary>
        internal static Task DeleteAsync(long telegramChatId) =>
            Redis.db.KeyDeleteAsync(KeyFor(telegramChatId));

        /// <summary>
        /// Takes the group out of the migration. The in-memory state is corrected immediately so
        /// the very next mutation stops buffering, even if PostgreSQL is unreachable - continuing
        /// to buffer into a doomed cutover would only consume memory Redis does not have.
        /// </summary>
        private static async Task AbandonAsync(long telegramChatId, string reason)
        {
            GroupMigrationStateStore.ApplyLocal(telegramChatId, GroupMigrationState.Redis);
            LogHelper.Error($"Group {telegramChatId} cutover abandoned: {reason}.");
            try
            {
                await GroupMigrationStateStore.AbandonAsync(telegramChatId, reason);
                await DeleteAsync(telegramChatId);
            }
            catch (Exception e)
            {
                LogHelper.Error($"Recording abandonment for group {telegramChatId} failed: {e.Message}");
            }
        }

        /// <summary>
        /// Entries a buffer may hold before the cutover is abandoned.
        /// <para>
        /// Redis holds 4.18 GB of 5.78 GB in production with <c>maxmemory 0</c>, so an unbounded
        /// buffer risks the OOM killer taking the process rather than a rejected write. The cap is
        /// what keeps a busy group's cutover from becoming an outage.
        /// </para>
        /// </summary>
        internal static int Capacity =>
            Math.Clamp(RegHelper.GetInt("GroupDrainBufferCapacity", 50_000), 1_000, 500_000);
    }
}
