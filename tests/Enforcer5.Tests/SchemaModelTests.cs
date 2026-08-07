using Enforcer5.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;
using EnforcerMigrationCommand = Enforcer5.Storage.MigrationCommand;

namespace Enforcer5.Tests;

public sealed class SchemaModelTests
{
    [Fact]
    public void SchemaCommandIsExplicit()
    {
        Assert.True(EnforcerMigrationCommand.IsSchemaMigration(["migrate", "schema"]));
        Assert.True(EnforcerMigrationCommand.IsMigrationCommand(["migrate", "group-settings", "backfill"]));
        Assert.True(EnforcerMigrationCommand.IsMigrationCommand(["migrate", "group-settings", "invalid"]));
        Assert.False(EnforcerMigrationCommand.IsMigrationCommand(["run"]));
    }

    [Fact]
    public void GroupSettingsModelUsesCanonicalJsonbAndCompositeKey()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(GroupSettingsEntity));

        Assert.NotNull(entity);
        Assert.Equal("group_settings", entity!.GetTableName());
        Assert.Equal(["ChatId", "SettingGroup"], entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal("jsonb", entity.FindProperty("Values")!.GetColumnType());
        Assert.True(entity.FindProperty("Version")!.IsConcurrencyToken);
    }

    [Fact]
    public void RedisSyncMetadataIsSeparateFromCanonicalSettings()
    {
        using var context = CreateContext();
        var canonical = context.Model.FindEntityType(typeof(GroupSettingsEntity))!;
        var sync = context.Model.FindEntityType(typeof(GroupSettingsRedisSync))!;

        Assert.Null(canonical.FindProperty("SourceRevision"));
        Assert.NotNull(sync.FindProperty("SourceRevision"));
        Assert.NotNull(sync.FindProperty("SourceChecksum"));
    }

    [Fact]
    public void RuntimeModelMatchesLatestMigrationSnapshot()
    {
        using var context = CreateContext();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var assembly = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;
        var snapshot = assembly.ModelSnapshot;

        Assert.NotNull(snapshot);
        var snapshotModel = initializer.Initialize(snapshot!.Model);
        Assert.False(differ.HasDifferences(snapshotModel.GetRelationalModel(),
            designTimeModel.GetRelationalModel()));
    }

    [Fact]
    public void GroupSettingsCommandsAreExplicit()
    {
        Assert.True(EnforcerMigrationCommand.IsGroupSettingsMigration(
            ["migrate", "group-settings", "backfill"], out var backfillReconcile));
        Assert.False(backfillReconcile);
        Assert.True(EnforcerMigrationCommand.IsGroupSettingsMigration(
            ["migrate", "group-settings", "reconcile"], out var reconcile));
        Assert.True(reconcile);
        Assert.False(EnforcerMigrationCommand.IsGroupSettingsMigration(
            ["migrate", "group-settings", "delete"], out _));
    }

    [Fact]
    public void PrincipalContentCommandsAreExplicit()
    {
        Assert.True(EnforcerMigrationCommand.IsPrincipalContentMigration(
            ["migrate", "principal-content", "backfill"], out var backfillReconcile));
        Assert.False(backfillReconcile);
        Assert.True(EnforcerMigrationCommand.IsPrincipalContentMigration(
            ["migrate", "principal-content", "reconcile"], out var reconcile));
        Assert.True(reconcile);
        Assert.False(EnforcerMigrationCommand.IsPrincipalContentMigration(
            ["migrate", "principal-content", "delete"], out _));
    }

    [Fact]
    public void ModerationCommandsAreExplicit()
    {
        Assert.True(EnforcerMigrationCommand.IsModerationMigration(
            ["migrate", "moderation", "backfill"], out var backfillReconcile));
        Assert.False(backfillReconcile);
        Assert.True(EnforcerMigrationCommand.IsModerationMigration(
            ["migrate", "moderation", "reconcile"], out var reconcile));
        Assert.True(reconcile);
    }

    [Fact]
    public void OperationsCommandsAreExplicit()
    {
        Assert.True(EnforcerMigrationCommand.IsOperationsMigration(
            ["migrate", "operations", "backfill"], out var backfillReconcile));
        Assert.False(backfillReconcile);
        Assert.True(EnforcerMigrationCommand.IsOperationsMigration(
            ["migrate", "operations", "reconcile"], out var reconcile));
        Assert.True(reconcile);
    }

    [Theory]
    [InlineData("RedisPrimaryPostgresShadow")]
    [InlineData("PostgresPrimaryRedisMirror")]
    [InlineData("PostgresOnly")]
    public void StorageModesAreStableConfigurationValues(string value)
    {
        Assert.True(Enum.TryParse<StorageMode>(value, true, out var parsed));
        Assert.Equal(value, parsed.ToString());
    }

    private static EnforcerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<EnforcerDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only;Password=model_only")
            .Options;
        return new EnforcerDbContext(options);
    }
}
