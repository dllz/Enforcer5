using Enforcer5.Storage;
using Enforcer5.Storage.Moderation;
using Xunit;

namespace Enforcer5.Tests;

public sealed class ModerationRuntimeRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RedisPrimaryPoliciesUseRedisAndOptionallyShadowPostgres(bool shadowEnabled,
        bool writePostgres)
    {
        var route = PolicyStore.GetRoute(StorageMode.RedisPrimaryPostgresShadow, shadowEnabled);

        Assert.True(route.ReadRedis);
        Assert.True(route.WriteRedis);
        Assert.Equal(writePostgres, route.WritePostgres);
    }

    [Fact]
    public void PostgresPrimaryPoliciesMirrorRedisForRollback()
    {
        var route = PolicyStore.GetRoute(StorageMode.PostgresPrimaryRedisMirror, false);

        Assert.False(route.ReadRedis);
        Assert.True(route.WritePostgres);
        Assert.True(route.WriteRedis);
    }

    [Fact]
    public void PostgresOnlyPoliciesDoNotUseRedis()
    {
        var route = PolicyStore.GetRoute(StorageMode.PostgresOnly, true);

        Assert.False(route.ReadRedis);
        Assert.True(route.WritePostgres);
        Assert.False(route.WriteRedis);
    }
}
