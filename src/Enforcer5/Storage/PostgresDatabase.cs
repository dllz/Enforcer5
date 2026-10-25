using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Enforcer5.Storage
{
    internal sealed class PostgresDatabase : IAsyncDisposable
    {
        /// <summary>
        /// Shared data sources keyed by connection string. An <see cref="NpgsqlDataSource"/> owns the
        /// connection pool, so building one per call - as <see cref="FromConfiguration"/> used to -
        /// gave every operation its own pool and paid pool construction on the message hot path.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Lazy<PostgresDatabase>> SharedSources =
            new(StringComparer.Ordinal);

        private readonly NpgsqlDataSource _dataSource;

        /// <summary>
        /// Shared instances are owned by the process and outlive their callers, so their
        /// <see cref="DisposeAsync"/> is a no-op. Callers keep their <c>await using</c> blocks.
        /// </summary>
        private readonly bool _shared;

        static PostgresDatabase()
        {
            SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
            SqlMapper.AddTypeHandler(new DateOnlyHandler());
        }

        /// <summary>
        /// Creates a caller-owned database with its own pool. Disposing it disposes the pool.
        /// Tests use this to stay isolated from the shared instances.
        /// </summary>
        internal PostgresDatabase(string connectionString) : this(connectionString, false)
        {
        }

        private PostgresDatabase(string connectionString, bool shared)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
            _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
            _shared = shared;
        }

        /// <summary>
        /// Returns the process-wide database for the configured connection string. The returned
        /// instance is shared and its pool is reused; disposing it does nothing.
        /// </summary>
        internal static PostgresDatabase FromConfiguration() =>
            Shared(Helpers.RegHelper.GetRequired("PostgresConnection"));

        /// <summary>
        /// Returns the process-wide database for an explicit connection string. Callers that must
        /// record which connection a long-lived component was bound to should resolve the string
        /// themselves and use this, so the recorded value and the pool cannot disagree.
        /// </summary>
        internal static PostgresDatabase Shared(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
            // Lazy keeps a losing GetOrAdd race from building a data source that nothing disposes.
            return SharedSources.GetOrAdd(connectionString, key => new Lazy<PostgresDatabase>(
                () => new PostgresDatabase(key, true),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        /// <summary>
        /// Disposes every shared data source. Called once during shutdown.
        /// </summary>
        internal static async Task DisposeSharedAsync()
        {
            foreach (var key in SharedSources.Keys.ToArray())
            {
                if (!SharedSources.TryRemove(key, out var shared) || !shared.IsValueCreated) continue;
                try
                {
                    await shared.Value._dataSource.DisposeAsync();
                }
                catch (Exception e)
                {
                    Helpers.ErrorReporter.Report(e, "Disposing shared PostgreSQL data source");
                }
            }
        }

        internal async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                return await connection.ExecuteScalarAsync<bool>(
                    new CommandDefinition("SELECT TRUE", cancellationToken: cancellationToken));
            }
            catch
            {
                // The answer to the question asked: callers report an unhealthy database.
                return false;
            }
        }

        internal ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            return _dataSource.OpenConnectionAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => _shared ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

        private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
        {
            public override DateTimeOffset Parse(object value) => value switch
            {
                DateTimeOffset offset => offset,
                DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
                _ => DateTimeOffset.Parse(value.ToString())
            };

            public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value) =>
                parameter.Value = value.UtcDateTime;
        }

        private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
        {
            public override DateOnly Parse(object value) => value switch
            {
                DateOnly date => date,
                DateTime dateTime => DateOnly.FromDateTime(dateTime),
                _ => DateOnly.Parse(value.ToString())
            };

            public override void SetValue(System.Data.IDbDataParameter parameter, DateOnly value) =>
                parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }
    }
}
