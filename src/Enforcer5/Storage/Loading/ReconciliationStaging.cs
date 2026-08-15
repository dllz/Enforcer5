using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Enforcer5.Storage.Loading
{
    /// <summary>
    /// The outcome of comparing one Redis snapshot against PostgreSQL.
    /// </summary>
    /// <param name="PostgresOrphans">
    /// Present in PostgreSQL, absent from Redis. These are acted on - revoked, dismissed or
    /// disabled - because Redis is authoritative until cutover.
    /// </param>
    /// <param name="MissingFromPostgres">
    /// Present in Redis, absent from PostgreSQL. Reported only. This direction was previously
    /// undetectable in every domain, which meant a backfill that silently skipped records
    /// reconciled clean.
    /// </param>
    /// <param name="ValueMismatches">Present in both, values disagree.</param>
    /// <param name="RemovalsSkipped">
    /// True when the Redis snapshot was incomplete, so orphan removal was withheld. The comparison
    /// still ran - the count is meaningful, it just cannot authorise deletions.
    /// </param>
    internal readonly record struct ReconciliationOutcome(int PostgresOrphans, int MissingFromPostgres,
        int ValueMismatches, bool RemovalsSkipped)
    {
        internal int Total => PostgresOrphans + MissingFromPostgres + ValueMismatches;

        internal ReconciliationOutcome Add(ReconciliationOutcome other) => new(
            PostgresOrphans + other.PostgresOrphans,
            MissingFromPostgres + other.MissingFromPostgres,
            ValueMismatches + other.ValueMismatches,
            RemovalsSkipped || other.RemovalsSkipped);

        public override string ToString() =>
            $"orphans={PostgresOrphans}, missing={MissingFromPostgres}, " +
            $"value-mismatches={ValueMismatches}{(RemovalsSkipped ? ", removals withheld" : string.Empty)}";
    }

    /// <summary>
    /// Stages a Redis-side key set into PostgreSQL so a comparison can be one join rather than one
    /// query per record.
    /// <para>
    /// The shipped reconcilers issued a query per row - 227,696 for warning balances, 322,721 for
    /// audit events. The handoff's own prescription was to "reconcile with set operations rather
    /// than one query per record"; this is that.
    /// </para>
    /// </summary>
    internal static class ReconciliationStaging
    {
        /// <summary>Stages a single-column set of text keys and returns the temp table name.</summary>
        internal static async Task<string> StageKeysAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, string name, IEnumerable<string> keys,
            CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(connection, transaction,
                $"CREATE TEMP TABLE {name} (source_key text) ON COMMIT DROP", cancellationToken);

            await using (var writer = await connection.BeginBinaryImportAsync(
                             $"COPY {name} (source_key) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var key in keys)
                {
                    if (key == null) continue;
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(key, NpgsqlDbType.Text, cancellationToken);
                }
                await writer.CompleteAsync(cancellationToken);
            }

            // Indexed after loading rather than before: the compare reads it repeatedly, but the
            // COPY should not pay for maintenance on the way in.
            await ExecuteAsync(connection, transaction,
                $"CREATE INDEX ON {name} (source_key)", cancellationToken);
            return name;
        }

        /// <summary>Stages a set of (long, long) pairs - group/user, group/message and the like.</summary>
        internal static async Task<string> StagePairsAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, string name, IEnumerable<(long Left, long Right)> pairs,
            CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(connection, transaction,
                $"CREATE TEMP TABLE {name} (left_id bigint, right_id bigint) ON COMMIT DROP",
                cancellationToken);

            await using (var writer = await connection.BeginBinaryImportAsync(
                             $"COPY {name} (left_id, right_id) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var (left, right) in pairs)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(left, NpgsqlDbType.Bigint, cancellationToken);
                    await writer.WriteAsync(right, NpgsqlDbType.Bigint, cancellationToken);
                }
                await writer.CompleteAsync(cancellationToken);
            }

            await ExecuteAsync(connection, transaction,
                $"CREATE INDEX ON {name} (left_id, right_id)", cancellationToken);
            return name;
        }

        /// <summary>Stages keyed values, for comparing a stored number against its Redis original.</summary>
        internal static async Task<string> StageKeyedValuesAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, string name,
            IEnumerable<(long Id, string Kind, long Value)> values,
            CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(connection, transaction,
                $"CREATE TEMP TABLE {name} (id bigint, kind text, value bigint) ON COMMIT DROP",
                cancellationToken);

            await using (var writer = await connection.BeginBinaryImportAsync(
                             $"COPY {name} (id, kind, value) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var (id, kind, value) in values)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(id, NpgsqlDbType.Bigint, cancellationToken);
                    await writer.WriteAsync(kind, NpgsqlDbType.Text, cancellationToken);
                    await writer.WriteAsync(value, NpgsqlDbType.Bigint, cancellationToken);
                }
                await writer.CompleteAsync(cancellationToken);
            }

            await ExecuteAsync(connection, transaction,
                $"CREATE INDEX ON {name} (id, kind)", cancellationToken);
            return name;
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            string sql, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
