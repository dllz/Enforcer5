using Xunit;

namespace Enforcer5.Tests;

internal sealed class RedisFactAttribute : FactAttribute
{
    internal const string ConnectionVariable = "ENFORCER_TEST_REDIS";

    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            Skip = $"Set {ConnectionVariable} to run Redis integration tests.";
    }
}
