using Enforcer5.Storage;
using Enforcer5.Storage.Operations;
using Xunit;

namespace Enforcer5.Tests;

public sealed class OperationsReportStoreTests
{
    [Theory]
    [InlineData(StorageMode.RedisPrimaryPostgresShadow, false, true, false, true)]
    [InlineData(StorageMode.RedisPrimaryPostgresShadow, true, true, true, true)]
    [InlineData(StorageMode.PostgresPrimaryRedisMirror, false, false, true, true)]
    [InlineData(StorageMode.PostgresOnly, false, false, true, false)]
    internal void RoutesCanonicalReportsByStorageMode(StorageMode mode, bool shadowEnabled,
        bool readRedis, bool writePostgres, bool writeRedis)
    {
        Assert.Equal(new ReportStoreRoute(readRedis, writePostgres, writeRedis),
            ReportStore.GetRoute(mode, shadowEnabled));
    }
}
