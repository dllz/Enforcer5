using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage.Loading;

namespace Enforcer5.Storage.Cutover
{
    /// <summary>
    /// Moves groups across in the background, and lets live traffic jump the queue.
    /// <para>
    /// Two producers meeting in the middle, as the rollout describes: the crawler eats the cold
    /// tail while traffic pulls the hot groups across. Cold groups are the <em>easiest</em> to
    /// migrate - no contention and nothing to drain - so the crawler makes steady progress while
    /// the groups that would be hardest to catch at a quiet moment are taken exactly when they are
    /// active and their state is already in cache.
    /// </para>
    /// <para>
    /// This is what stops the migration being "on modification only", which never terminates and
    /// would leave the Redis read path permanently undeletable.
    /// </para>
    /// </summary>
    internal sealed class GroupCutoverCrawler
    {
        private static readonly DurableWriter Writer =
            QuiesceBarrier.Register("group-cutover-crawler", WriterResumeClass.Cursor);

        /// <summary>
        /// Groups seen in traffic that have not cut over yet. Bounded, because an unbounded queue
        /// on the message hot path is a memory leak with extra steps - once it is full the crawler
        /// is already saturated and the group will be reached by scan order anyway.
        /// </summary>
        private static readonly ConcurrentQueue<long> FastTrack = new();
        private static int _fastTrackDepth;
        private const int FastTrackCapacity = 1_000;

        private readonly PostgresDatabase _database;
        private readonly GroupCutoverService _cutover;
        private readonly string _owner;

        internal GroupCutoverCrawler(PostgresDatabase database, string owner)
        {
            _database = database;
            _owner = owner;
            _cutover = new GroupCutoverService(database, owner);
        }

        /// <summary>
        /// Offers a group for priority cutover because it was just seen in traffic.
        /// <para>
        /// Called from the message path, so it does no I/O and never blocks: a dictionary lookup
        /// and a bounded enqueue. A group already migrated, already in flight, or not enabled for
        /// cutover costs one comparison.
        /// </para>
        /// </summary>
        internal static void Observe(long telegramChatId)
        {
            if (!TrafficCutoverEnabled) return;
            if (GroupMigrationStateStore.Get(telegramChatId) != GroupMigrationState.Redis) return;
            if (Volatile.Read(ref _fastTrackDepth) >= FastTrackCapacity) return;
            Interlocked.Increment(ref _fastTrackDepth);
            FastTrack.Enqueue(telegramChatId);
        }

        internal static void ResetForTests()
        {
            while (FastTrack.TryDequeue(out _)) { }
            Volatile.Write(ref _fastTrackDepth, 0);
        }

        /// <summary>
        /// One pass: fast-tracked groups first, then the coldest groups the scan knows about.
        /// </summary>
        internal async Task<int> RunOnceAsync(int budget, CancellationToken cancellationToken = default) =>
            await RunOnceAsync(budget, 0, cancellationToken);

        /// <summary>
        /// One pass, optionally skipping groups below a key count.
        /// <para>
        /// <paramref name="minimumKeys"/> exists because smallest-first means the head of the queue
        /// is thousands of near-empty chat ids - useful for burn-down, useless for proving the
        /// machinery handles a real group. It is also how an operator would schedule the hundred or
        /// so outliers into a quiet window rather than meeting them by accident.
        /// </para>
        /// </summary>
        internal async Task<int> RunOnceAsync(int budget, int minimumKeys,
            CancellationToken cancellationToken = default)
        {
            if (QuiesceBarrier.IsQuiescing) return 0;

            var migrated = 0;
            foreach (var telegramChatId in await NextBatchAsync(budget, minimumKeys, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Gated at the top of each group rather than around the batch, so a quiesce waits
                // for one group rather than for the whole budget.
                await Writer.GateAsync(cancellationToken);
                using var work = await Writer.BeginWorkAsync(cancellationToken);

                var result = await _cutover.CutOverAsync(telegramChatId, cancellationToken);
                if (result.Succeeded) migrated++;
            }
            return migrated;
        }

        /// <summary>
        /// Fast-tracked groups first, then smallest-first from the inventory.
        /// <para>
        /// Smallest-first because a small group cuts over in milliseconds, so burn-down moves
        /// early and any systematic defect surfaces on cheap groups rather than on the 528,056-key
        /// outlier.
        /// </para>
        /// </summary>
        private async Task<IReadOnlyList<long>> NextBatchAsync(int budget, int minimumKeys,
            CancellationToken cancellationToken)
        {
            var batch = new List<long>(budget);
            while (batch.Count < budget && FastTrack.TryDequeue(out var observed))
            {
                Interlocked.Decrement(ref _fastTrackDepth);
                if (GroupMigrationStateStore.Get(observed) == GroupMigrationState.Redis)
                    batch.Add(observed);
            }
            if (batch.Count >= budget) return batch;

            var generation = await MigrationInventory.LatestAsync(_database, cancellationToken);
            if (generation == null) return batch;

            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            var candidates = await connection.QueryAsync<long>(new CommandDefinition(@"
SELECT i.telegram_chat_id
  FROM migration_key_inventory i
  LEFT JOIN migration_group_state s ON s.telegram_chat_id = i.telegram_chat_id
 WHERE i.generation_id = @Generation AND i.telegram_chat_id IS NOT NULL
   AND (s.state IS NULL OR s.state = 'Redis')
   AND (s.lease_expires_at IS NULL OR s.lease_expires_at < now())
 GROUP BY i.telegram_chat_id
HAVING count(*) >= @MinimumKeys
 ORDER BY count(*), i.telegram_chat_id
 LIMIT @Limit",
                new { Generation = generation.Value.GenerationId, Limit = budget - batch.Count,
                    MinimumKeys = minimumKeys },
                cancellationToken: cancellationToken));

            batch.AddRange(candidates.Where(x => !batch.Contains(x)));
            return batch;
        }

        /// <summary>
        /// Burn-down, for the deploy chat: how many groups sit in each state against the population
        /// the scan found.
        /// </summary>
        internal async Task<string> DescribeProgressAsync(CancellationToken cancellationToken = default)
        {
            var states = await GroupMigrationStateStore.CountByStateAsync(_database, cancellationToken);
            var generation = await MigrationInventory.LatestAsync(_database, cancellationToken);
            var total = generation == null
                ? 0
                : (await MigrationInventory.GroupIdsAsync(_database, generation.Value.GenerationId,
                    cancellationToken)).Count;
            var migrated = states.TryGetValue(GroupMigrationState.Postgres, out var done) ? done : 0;
            return $"groups migrated {migrated:N0}/{total:N0}" +
                   $" ({(total == 0 ? 0 : 100.0 * migrated / total):N1}%)," +
                   $" in flight {states.Where(x => x.Key is GroupMigrationState.Draining or GroupMigrationState.Verifying).Sum(x => x.Value)}," +
                   $" fast-track queued {Volatile.Read(ref _fastTrackDepth)}";
        }

        /// <summary>
        /// Traffic-triggered cutover stays off until the crawler has proven itself over several
        /// thousand clean groups. Enabling both at once would put any systematic defect straight
        /// onto the busiest groups in the estate.
        /// </summary>
        private static bool TrafficCutoverEnabled =>
            string.Equals(RegHelper.GetRegValue("GroupTrafficCutoverEnabled"), "true",
                StringComparison.OrdinalIgnoreCase);
    }
}
