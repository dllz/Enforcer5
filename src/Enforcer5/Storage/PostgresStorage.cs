using System;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Storage
{
    /// <summary>
    /// Process-wide PostgreSQL state. PostgreSQL is optional and additive while the rollout is in
    /// progress: Redis stays the store everything runs on, and every PostgreSQL-backed feature is
    /// off until <see cref="Ready"/>. An empty <c>PostgresConnection</c> means it never is.
    /// </summary>
    internal static class PostgresStorage
    {
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
        private static volatile PostgresDatabase _database;

        /// <summary>The shared database once the schema is up to date; null until then.</summary>
        internal static PostgresDatabase Database => _database;

        internal static bool Ready => _database != null;

        /// <summary>Sets (or with null clears) the database, for the test harness.</summary>
        internal static void UseForTests(PostgresDatabase database) => _database = database;

        /// <summary>
        /// Brings the schema up to date in the background, retrying every minute until it succeeds,
        /// so a database that is down at startup does not need a restart once it is back. Never
        /// throws and never holds up startup.
        /// </summary>
        internal static void Start(CancellationToken cancellationToken)
        {
            var connectionString = RegHelper.GetRegValue("PostgresConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                LogHelper.Info("PostgresConnection is not set; PostgreSQL features are off");
                return;
            }

            Bot.Dispatch(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await SchemaMigrationRunner.ApplyAsync(connectionString, cancellationToken);
                        _database = PostgresDatabase.Shared(connectionString);
                        LogHelper.Info("PostgreSQL schema is up to date");
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return; // shutting down
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "Applying the PostgreSQL schema; PostgreSQL features stay off, retrying in a minute");
                    }
                    try { await Task.Delay(RetryInterval, cancellationToken); }
                    catch (OperationCanceledException) { return; } // shutting down
                }
            }, "PostgresStorage.Start");
        }
    }
}
