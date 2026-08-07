using Xunit;

namespace Enforcer5.Tests;

internal sealed class PostgresFactAttribute : FactAttribute
{
    internal const string ConnectionVariable = "ENFORCER_TEST_POSTGRES";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            Skip = $"Set {ConnectionVariable} to run PostgreSQL integration tests.";
    }
}
