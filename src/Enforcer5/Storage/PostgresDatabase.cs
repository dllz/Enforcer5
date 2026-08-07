using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Enforcer5.Storage
{
    internal sealed class PostgresDatabase : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;

        internal PostgresDatabase(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
            _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        }

        internal static PostgresDatabase FromConfiguration()
        {
            return new PostgresDatabase(Helpers.RegHelper.GetRequired("PostgresConnection"));
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
                return false;
            }
        }

        internal ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            return _dataSource.OpenConnectionAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
    }
}
