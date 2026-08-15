using Enforcer5.Storage.Cutover;

namespace Enforcer5.Storage
{
    /// <summary>
    /// Decides which store is authoritative for a given piece of data.
    /// <para>
    /// The migration has two tiers moving at different speeds, and conflating them is what made
    /// <see cref="StorageModeProvider"/> unusable on its own. The global tier - users, Telegram
    /// accounts, the username index, global sanctions and global roles - is migrated once, to
    /// completeness, before any group cuts over. Group-scoped data then moves one group at a time,
    /// so a mixed population is normal for the length of the migration.
    /// </para>
    /// <para>
    /// With no rows in <c>migration_group_state</c> this behaves exactly as the process-wide mode
    /// did, so enabling it changes nothing until groups are deliberately flipped.
    /// </para>
    /// </summary>
    internal static class StorageRouting
    {
        /// <summary>
        /// True when PostgreSQL is authoritative for global and user-tier data.
        /// <para>
        /// This stays false through the per-group phase. Redis keeps serving global reads until the
        /// final flip, which is safe because Stage B loads the global tier to completeness without
        /// removing anything from Redis.
        /// </para>
        /// </summary>
        internal static bool GlobalTierPostgresIsPrimary => StorageModeProvider.PostgresIsPrimary;

        /// <summary>
        /// True when PostgreSQL is authoritative for the given group.
        /// <para>
        /// The process-wide mode acts as a ceiling: once it is flipped, every group is on PostgreSQL
        /// regardless of per-group state. Before that, a group is on PostgreSQL only if its own
        /// state says so.
        /// </para>
        /// </summary>
        internal static bool GroupPostgresIsPrimary(long telegramChatId) =>
            StorageModeProvider.PostgresIsPrimary ||
            GroupMigrationStateStore.PostgresIsAuthoritative(telegramChatId);

        /// <summary>
        /// True when a mutation for the group must also be appended to its drain buffer, because a
        /// copy is in flight.
        /// </summary>
        internal static bool GroupRequiresDrainBuffer(long telegramChatId) =>
            GroupMigrationStateStore.Get(telegramChatId).RequiresDrainBuffer();

        /// <summary>
        /// The storage mode as it applies to one group.
        /// <para>
        /// Existing route tables take a <see cref="StorageMode"/>, so rather than duplicating them
        /// per group this projects the group's state onto the same enum. A group that has cut over
        /// behaves as <see cref="StorageMode.PostgresPrimaryRedisMirror"/> - PostgreSQL is
        /// authoritative and Redis keeps being written, which is what makes rollback a state flip
        /// rather than a restore.
        /// </para>
        /// </summary>
        internal static StorageMode EffectiveGroupMode(long telegramChatId)
        {
            var mode = StorageModeProvider.Current;
            // Once the process-wide mode is flipped it is the ceiling for every group.
            if (mode != StorageMode.RedisPrimaryPostgresShadow) return mode;

            return GroupMigrationStateStore.PostgresIsAuthoritative(telegramChatId)
                ? StorageMode.PostgresPrimaryRedisMirror
                : StorageMode.RedisPrimaryPostgresShadow;
        }

        /// <summary>
        /// True when PostgreSQL writes must be mirrored back into Redis for rollback. This is a
        /// mode-level decision and has no per-group form.
        /// </summary>
        internal static bool RedisMirrorEnabled => StorageModeProvider.RedisMirrorEnabled;

        /// <summary>
        /// True when writes for this group must be mirrored back into Redis.
        /// <para>
        /// A group that has cut over during the per-group phase still needs its PostgreSQL writes
        /// projected into Redis, or rolling it back would mean restoring data rather than flipping
        /// a state. The process-wide <see cref="RedisMirrorEnabled"/> is false throughout that
        /// phase, so group-scoped writers must ask this instead.
        /// </para>
        /// </summary>
        internal static bool GroupRedisMirrorEnabled(long telegramChatId) =>
            EffectiveGroupMode(telegramChatId) == StorageMode.PostgresPrimaryRedisMirror;
    }
}
