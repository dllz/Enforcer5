using Enforcer5.Storage;
using Enforcer5.Storage.Moderation;
using Xunit;

namespace Enforcer5.Tests;

public sealed class ModerationRoleRoutingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RedisPrimaryAlwaysUsesRedisAndOptionallyShadowsPostgres(bool shadowEnabled,
        bool writePostgres)
    {
        var route = RoleStore.GetRoute(StorageMode.RedisPrimaryPostgresShadow, shadowEnabled);

        Assert.True(route.ReadRedis);
        Assert.True(route.WriteRedis);
        Assert.Equal(writePostgres, route.WritePostgres);
    }

    [Fact]
    public void PostgresPrimaryWithMirrorReadsCanonicalAndMirrorsRedis()
    {
        var route = RoleStore.GetRoute(StorageMode.PostgresPrimaryRedisMirror, false);

        Assert.False(route.ReadRedis);
        Assert.True(route.WritePostgres);
        Assert.True(route.WriteRedis);
    }

    [Fact]
    public void PostgresOnlyNeverUsesRedis()
    {
        var route = RoleStore.GetRoute(StorageMode.PostgresOnly, true);

        Assert.False(route.ReadRedis);
        Assert.True(route.WritePostgres);
        Assert.False(route.WriteRedis);
    }
}
