using System;

namespace Enforcer5.Storage.Cutover
{
    /// <summary>
    /// Where a single group sits in the Redis to PostgreSQL migration.
    /// <para>
    /// The migration unit is the group, so this - not the process-wide <c>StorageMode</c> - decides
    /// which store is authoritative for a given chat. A mixed population is expected and normal for
    /// the length of the migration.
    /// </para>
    /// </summary>
    internal enum GroupMigrationState
    {
        /// <summary>Redis is authoritative. The default for every group until it is migrated.</summary>
        Redis = 0,

        /// <summary>
        /// Being copied. Redis is still authoritative and still serves reads, but every mutation is
        /// additionally appended to the group's drain buffer so the copy can catch up.
        /// </summary>
        Draining = 1,

        /// <summary>
        /// Copy finished and the buffer drained; reconciliation is running. Redis remains
        /// authoritative so a failure here costs nothing.
        /// </summary>
        Verifying = 2,

        /// <summary>PostgreSQL is authoritative for this group.</summary>
        Postgres = 3,

        /// <summary>
        /// A cutover attempt failed reconciliation. Reads go back to Redis; the PostgreSQL rows are
        /// left as garbage for inspection rather than deleted.
        /// </summary>
        Failed = 4
    }

    internal static class GroupMigrationStateExtensions
    {
        /// <summary>
        /// True when PostgreSQL is authoritative for the group. Only <see cref="GroupMigrationState.Postgres"/>
        /// qualifies - <c>Draining</c> and <c>Verifying</c> are still served from Redis, which is what
        /// makes a failed cutover free.
        /// </summary>
        internal static bool PostgresIsAuthoritative(this GroupMigrationState state) =>
            state == GroupMigrationState.Postgres;

        /// <summary>
        /// True when a mutation must also be appended to the group's drain buffer.
        /// </summary>
        internal static bool RequiresDrainBuffer(this GroupMigrationState state) =>
            state == GroupMigrationState.Draining || state == GroupMigrationState.Verifying;

        internal static GroupMigrationState Parse(string value) =>
            Enum.TryParse<GroupMigrationState>(value, true, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"Unsupported group migration state '{value}'.");
    }
}
