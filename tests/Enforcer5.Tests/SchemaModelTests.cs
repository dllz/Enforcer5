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

    [Fact]
    public void KeyspaceInventoryDeduplicatesOnTheNaturalKeyAndIsScopedToAGeneration()
    {
        using var context = CreateContext();
        var inventory = context.Model.FindEntityType(typeof(MigrationKeyInventoryEntity))!;

        Assert.Equal("migration_key_inventory", inventory.GetTableName());
        // SCAN can return the same key more than once within a single pass, so the natural key is
        // what makes an insert idempotent. A surrogate key would have let duplicates through.
        Assert.Equal(["GenerationId", "RedisKey"],
            inventory.FindPrimaryKey()!.Properties.Select(x => x.Name));
        // Every row belongs to a generation, and dropping a generation must take its keys with it -
        // a stale inventory is worse than none, because Stage C's freshness rule would trust it.
        var generationLink = Assert.Single(inventory.GetForeignKeys());
        Assert.Equal(typeof(MigrationScanGenerationEntity), generationLink.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, generationLink.DeleteBehavior);
        // The per-group lookup exists to replace a full SCAN MATCH per group, which walks the whole
        // keyspace and is quadratic under a crawler.
        Assert.Contains(inventory.GetIndexes(),
            index => index.Name == "migration_key_inventory_group_idx");
    }

    [Fact]
    public void AScanGenerationCannotClaimCompletionWithoutATimestamp()
    {
        using var context = CreateContext();
        // Check constraints are not carried in the read-optimized runtime model.
        var generation = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(MigrationScanGenerationEntity))!;

        Assert.Equal("migration_scan_generations", generation.GetTableName());
        Assert.Equal(["GenerationId"], generation.FindPrimaryKey()!.Properties.Select(x => x.Name));
        // Stage C only cuts a group over if the inventory is newer than the moment it started
        // draining, and completed_at is the value that rule reads. A generation that reported
        // completion without one would authorise a cutover against a partial inventory.
        Assert.Contains(generation.GetCheckConstraints(),
            check => check.Name == "migration_scan_generations_completed_check");
        Assert.Equal("running", generation.FindProperty("Status")!.GetDefaultValue());
    }

    [Fact]
    public void InventoryAndUserTierCommandsAreExplicit()
    {
        Assert.True(EnforcerMigrationCommand.IsInventoryScan(["migrate", "inventory"], out var resumeFresh));
        Assert.False(resumeFresh);
        // --fresh forces a new generation, which is what Stage C needs when it requires an
        // inventory strictly newer than a group's drain start.
        Assert.True(EnforcerMigrationCommand.IsInventoryScan(["migrate", "inventory", "--fresh"], out var fresh));
        Assert.True(fresh);
        Assert.False(EnforcerMigrationCommand.IsInventoryScan(["migrate", "inventory", "--wipe"], out _));
        Assert.False(EnforcerMigrationCommand.IsInventoryScan(["migrate", "schema"], out _));

        Assert.True(EnforcerMigrationCommand.IsUserTierMigration(
            ["migrate", "user-tier", "backfill"], out var backfillReconcile));
        Assert.False(backfillReconcile);
        Assert.True(EnforcerMigrationCommand.IsUserTierMigration(
            ["migrate", "user-tier", "reconcile"], out var reconcile));
        Assert.True(reconcile);
        Assert.False(EnforcerMigrationCommand.IsUserTierMigration(["migrate", "user-tier", "load"], out _));
    }

    [Fact]
    public void GroupCutoverAndTempBanIndexCommandsAreExplicit()
    {
        Assert.True(EnforcerMigrationCommand.IsGroupCutover(
            ["migrate", "group", "-1001030085238"], out var chatId, out var rollback));
        Assert.Equal(-1001030085238L, chatId);
        Assert.False(rollback);
        Assert.True(EnforcerMigrationCommand.IsGroupCutover(
            ["migrate", "group", "-1001030085238", "--rollback"], out _, out var back));
        Assert.True(back);
        // A typo must not silently become a cutover.
        Assert.False(EnforcerMigrationCommand.IsGroupCutover(
            ["migrate", "group", "-1001030085238", "--revert"], out _, out _));
        Assert.False(EnforcerMigrationCommand.IsGroupCutover(["migrate", "group", "nan"], out _, out _));
        Assert.False(EnforcerMigrationCommand.IsGroupCutover(["migrate", "group", "0"], out _, out _));

        Assert.True(EnforcerMigrationCommand.IsCrawl(["migrate", "crawl", "100"], out var budget, out var floor));
        Assert.Equal(100, budget);
        Assert.Equal(0, floor);
        // Smallest-first means the head of the queue is thousands of near-empty chat ids, so a
        // floor is how the outliers get scheduled deliberately rather than met by accident.
        Assert.True(EnforcerMigrationCommand.IsCrawl(
            ["migrate", "crawl", "100", "--min-keys", "10000"], out _, out var big));
        Assert.Equal(10000, big);
        Assert.False(EnforcerMigrationCommand.IsCrawl(["migrate", "crawl", "0"], out _, out _));
        Assert.False(EnforcerMigrationCommand.IsCrawl(["migrate", "crawl", "-5"], out _, out _));
        Assert.False(EnforcerMigrationCommand.IsCrawl(["migrate", "crawl", "10", "--min", "5"], out _, out _));

        Assert.True(EnforcerMigrationCommand.IsTempBanIndex(["migrate", "tempban-index"], out var dry));
        Assert.False(dry);
        Assert.True(EnforcerMigrationCommand.IsTempBanIndex(
            ["migrate", "tempban-index", "--apply"], out var apply));
        Assert.True(apply);
        // Writing to Redis has to be asked for explicitly.
        Assert.False(EnforcerMigrationCommand.IsTempBanIndex(
            ["migrate", "tempban-index", "--force"], out _));
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
