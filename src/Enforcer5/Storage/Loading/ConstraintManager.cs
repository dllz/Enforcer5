using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Enforcer5.Storage.Loading
{
    internal sealed record DroppedConstraint(string Table, string Name, string Definition, bool IsForeignKey);

    /// <summary>
    /// Drops foreign keys and secondary indexes for a bulk load and puts them back afterwards.
    /// <para>
    /// Measured at 2.5-2.8x on the user tier - 88.7-108.7s against 244.6-268.1s - which at 25M rows
    /// is not an optimisation but the difference between a viable pause window and an unviable one.
    /// </para>
    /// <para>
    /// Definitions are read from <c>pg_catalog</c> rather than hardcoded. The prototype listed
    /// EF-generated names as string literals and dropped them with <c>IF EXISTS</c>, so a future EF
    /// rename would match nothing, the load would silently run against live constraints at 2.5x the
    /// cost, and the rebuild would then create an index the schema no longer expected. Reading the
    /// live catalog cannot drift.
    /// </para>
    /// <para>
    /// Primary keys and check constraints are deliberately left alone. Primary keys back the
    /// <c>ON CONFLICT</c> clauses the merge depends on, and check constraints are cheap to evaluate
    /// and are the last line of defence against loading data that violates an invariant.
    /// </para>
    /// </summary>
    internal static class ConstraintManager
    {
        /// <summary>
        /// Captures and drops the droppable constraints for the given tables.
        /// <para>
        /// Must be called inside the same transaction as the load and the rebuild. PostgreSQL DDL is
        /// transactional, so a crash mid-load rolls the drops back with everything else. Dropping in
        /// its own transaction would leave a live database missing unique indexes with no record of
        /// what they were - a silent correctness hazard rather than a failed run.
        /// </para>
        /// </summary>
        internal static async Task<IReadOnlyList<DroppedConstraint>> DropAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, IReadOnlyCollection<string> tables,
            CancellationToken cancellationToken = default)
        {
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction),
                    "Constraints must be dropped inside the load transaction so a failure rolls them back.");

            var names = tables.ToArray();

            var foreignKeys = (await connection.QueryAsync<(string Table, string Name, string Definition)>(
                new CommandDefinition(@"
SELECT rel.relname AS Table, con.conname AS Name, pg_get_constraintdef(con.oid) AS Definition
  FROM pg_constraint con
  JOIN pg_class rel ON rel.oid = con.conrelid
  JOIN pg_namespace ns ON ns.oid = rel.relnamespace
 WHERE ns.nspname = current_schema() AND rel.relname = ANY(@Names) AND con.contype = 'f'",
                    new { Names = names }, transaction, cancellationToken: cancellationToken)))
                .Select(x => new DroppedConstraint(x.Table, x.Name, x.Definition, true))
                .ToArray();

            // Indexes that back a constraint are dropped with the constraint, never on their own.
            var indexes = (await connection.QueryAsync<(string Table, string Name, string Definition)>(
                new CommandDefinition(@"
SELECT rel.relname AS Table, idx.relname AS Name, pg_get_indexdef(ix.indexrelid) AS Definition
  FROM pg_index ix
  JOIN pg_class idx ON idx.oid = ix.indexrelid
  JOIN pg_class rel ON rel.oid = ix.indrelid
  JOIN pg_namespace ns ON ns.oid = rel.relnamespace
 WHERE ns.nspname = current_schema() AND rel.relname = ANY(@Names)
   AND NOT ix.indisprimary
   AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = ix.indexrelid)",
                    new { Names = names }, transaction, cancellationToken: cancellationToken)))
                .Select(x => new DroppedConstraint(x.Table, x.Name, x.Definition, false))
                .ToArray();

            // Foreign keys first: an index cannot be dropped while a constraint depends on it.
            foreach (var constraint in foreignKeys)
                await ExecuteAsync(connection, transaction,
                    $"ALTER TABLE {Quote(constraint.Table)} DROP CONSTRAINT {Quote(constraint.Name)}",
                    cancellationToken);
            foreach (var index in indexes)
                await ExecuteAsync(connection, transaction, $"DROP INDEX {Quote(index.Name)}", cancellationToken);

            return foreignKeys.Concat(indexes).ToArray();
        }

        /// <summary>
        /// Recreates everything <see cref="DropAsync"/> removed, from the captured definitions.
        /// <para>
        /// Indexes first, then foreign keys, because a foreign key needs a unique index on the
        /// referenced column to exist before it can be validated.
        /// </para>
        /// </summary>
        internal static async Task RebuildAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            IReadOnlyList<DroppedConstraint> dropped, CancellationToken cancellationToken = default)
        {
            foreach (var index in dropped.Where(x => !x.IsForeignKey))
                await ExecuteAsync(connection, transaction, index.Definition, cancellationToken);

            foreach (var constraint in dropped.Where(x => x.IsForeignKey))
                await ExecuteAsync(connection, transaction,
                    $"ALTER TABLE {Quote(constraint.Table)} ADD CONSTRAINT {Quote(constraint.Name)} " +
                    constraint.Definition, cancellationToken);
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            string sql, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            // Index builds over 25M rows outrun any default timeout, and this runs inside a pause.
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}
