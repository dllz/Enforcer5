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
    public void GroupSettingsModelUsesTypedColumnsAndGroupForeignKey()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(GroupSettingsEntity));

        Assert.NotNull(entity);
        Assert.Equal("group_settings", entity!.GetTableName());
        Assert.Equal(["GroupId"], entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.NotNull(entity.FindProperty("ReportsEnabled"));
        Assert.NotNull(entity.FindProperty("MaximumMessages"));
        Assert.NotNull(entity.FindProperty("LogChatId"));
        Assert.DoesNotContain(entity.GetProperties(), property => property.GetColumnType() == "jsonb");
        Assert.Contains(entity.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(GroupEntity));
        Assert.True(entity.FindProperty("Version")!.IsConcurrencyToken);
    }

    [Fact]
    public void ExternalTelegramIdsAreSeparateFromGeneratedPrincipals()
    {
        using var context = CreateContext();
        var user = context.Model.FindEntityType(typeof(UserEntity))!;
        var account = context.Model.FindEntityType(typeof(TelegramAccountEntity))!;
        var group = context.Model.FindEntityType(typeof(GroupEntity))!;
        var chat = context.Model.FindEntityType(typeof(TelegramChatEntity))!;

        Assert.Equal(ValueGenerated.OnAdd, user.FindProperty("UserId")!.ValueGenerated);
        Assert.NotNull(account.FindProperty("TelegramUserId"));
        Assert.Equal(ValueGenerated.OnAdd, group.FindProperty("GroupId")!.ValueGenerated);
        Assert.NotNull(chat.FindProperty("TelegramChatId"));
    }

    [Fact]
    public void UserPreferencesAreTyped()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(GroupUserPreferenceEntity))!;

        Assert.NotNull(entity.FindProperty("AdminNotificationsDisabled"));
        Assert.NotNull(entity.FindProperty("ReportsBlocked"));
        Assert.NotNull(entity.FindProperty("WatchDisabled"));
        Assert.NotNull(entity.FindProperty("TagAllSubscribed"));
        Assert.Null(entity.FindProperty("Policy"));
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
        Assert.True(EnforcerMigrationCommand.IsGroupSettingsMigration(
            ["migrate", "group-settings", "backfill", "--group", "-1001030085238"], out _));
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
