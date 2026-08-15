using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// For tests that read Redis and write PostgreSQL in one operation - the keyspace inventory and
/// the loaders. Skips unless both are configured, so a partial environment reports honestly rather
/// than failing halfway through.
/// </summary>
internal sealed class RedisPostgresFactAttribute : FactAttribute
{
    public RedisPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable)))
            Skip = $"Set {RedisFactAttribute.ConnectionVariable} to run Redis integration tests.";
        else if (string.IsNullOrWhiteSpace(
                     Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)))
            Skip = $"Set {PostgresFactAttribute.ConnectionVariable} to run PostgreSQL integration tests.";
    }
}
