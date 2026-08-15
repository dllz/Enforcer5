using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Storage.PrincipalContent;
using Enforcer5.Storage.Moderation;
using Enforcer5.Storage.Operations;

namespace Enforcer5.Storage
{
    internal static class MigrationCommand
    {
        internal static bool IsSchemaMigration(string[] args)
        {
            return args != null && args.Length == 2 &&
                   string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(args[1], "schema", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsMigrationCommand(string[] args)
        {
            return args != null && args.Length > 0 &&
                   string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsGroupSettingsMigration(string[] args, out bool reconcile)
        {
            var parsed = TryParseDomain(args, "group-settings", out var options);
            reconcile = options?.Reconcile == true;
            return parsed;
        }

        internal static bool IsPrincipalContentMigration(string[] args, out bool reconcile)
        {
            var parsed = TryParseDomain(args, "principal-content", out var options);
            reconcile = options?.Reconcile == true;
            return parsed;
        }

        internal static bool IsModerationMigration(string[] args, out bool reconcile)
        {
            var parsed = TryParseDomain(args, "moderation", out var options);
            reconcile = options?.Reconcile == true;
            return parsed;
        }

        internal static bool IsOperationsMigration(string[] args, out bool reconcile)
        {
            var parsed = TryParseDomain(args, "operations", out var options);
            reconcile = options?.Reconcile == true;
            return parsed;
        }

        private static bool TryParseDomain(string[] args, string domain,
            out MigrationCommandOptions options)
        {
            if (!MigrationCommandOptionsParser.TryParse(args, out options)) return false;
            return string.Equals(options.Domain, domain, StringComparison.Ordinal);
        }

        internal static async Task<int> RunSchemaAsync()
        {
            await SchemaMigrationRunner.ApplyAsync(RegHelper.GetRequired("PostgresConnection"));
            Console.WriteLine("PostgreSQL schema is current.");
            return 0;
        }

        internal static async Task<int> RunAsync(string[] args)
        {
            if (IsSchemaMigration(args)) return await RunSchemaAsync();
            MigrationCommandOptionsParser.TryParse(args, out var options);
            LegacyGroupScope groupScope = null;
            if (options?.GroupId != null)
            {
                if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
                groupScope = await new LegacyGroupScopeReader(Redis.db).ReadAsync(options.GroupId.Value,
                    includeReports: string.Equals(options.Domain, "operations", StringComparison.Ordinal));
            }
            if (IsOperationsMigration(args, out var operationsReconcile))
            {
                if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
                await using var operationsLease = operationsReconcile
                    ? await MigrationMaintenanceGuard.AcquireAsync()
                    : null;
                await using var operationsDatabase = PostgresDatabase.FromConfiguration();
                var operationsTracker = await MigrationRunTracker.StartAsync(operationsDatabase,
                    $"operations-{(operationsReconcile ? "reconcile" : "backfill")}");
                try
                {
                var groupIds = new System.Collections.Generic.HashSet<long>();
                var userIds = new System.Collections.Generic.HashSet<long>();
                if (groupScope != null)
                {
                    groupIds.Add(groupScope.GroupId);
                    userIds.UnionWith(groupScope.UserIds);
                }
                else
                {
                    var groupValues = await Redis.db.SetMembersAsync("bot:groupsid");
                    foreach (var value in groupValues)
                        if (long.TryParse(value.ToString(), out var id)) groupIds.Add(id);
                    var usernameIds = await Redis.db.HashValuesAsync("bot:usernames");
                    foreach (var value in usernameIds)
                        if (long.TryParse(value.ToString(), out var id)) userIds.Add(id);
                    await foreach (var principalKey in ScanKeysAsync("user:*"))
                    {
                        var parts = principalKey.ToString().Split(':');
                        if (parts.Length == 2 && long.TryParse(parts[1], out var id)) userIds.Add(id);
                    }
                    await foreach (var globalBanKey in ScanKeysAsync("globalBan:*"))
                    {
                        var parts = globalBanKey.ToString().Split(':');
                        if (parts.Length == 2 && long.TryParse(parts[1], out var id)) userIds.Add(id);
                    }
                }
                var reportMessageIds = new System.Collections.Generic.Dictionary<long,
                    System.Collections.Generic.IReadOnlyCollection<long>>();
                var reportsByGroup = new System.Collections.Generic.Dictionary<long,
                    System.Collections.Generic.HashSet<long>>();
                if (groupScope != null)
                    reportsByGroup[groupScope.GroupId] = new System.Collections.Generic.HashSet<long>(
                        groupScope.ReportMessageIds);
                else
                {
                    await foreach (var reportKey in ScanKeysAsync("flagged:*"))
                    {
                        var parts = reportKey.ToString().Split(':');
                        if (parts.Length != 3 || !long.TryParse(parts[1], out var groupId) ||
                            !long.TryParse(parts[2], out var messageId)) continue;
                        if (!reportsByGroup.TryGetValue(groupId, out var ids))
                            reportsByGroup[groupId] = ids = new System.Collections.Generic.HashSet<long>();
                        ids.Add(messageId);
                    }
                }
                foreach (var pair in reportsByGroup) reportMessageIds[pair.Key] = pair.Value;
                var operationsService = new OperationsBackfillService(operationsDatabase);
                var operationsResult = await operationsService.RunRedisPrimaryAsync(
                    groupIds, userIds, reportMessageIds, operationsReconcile,
                    operationsLease?.Token ?? default, includeGlobalData: groupScope == null);
                foreach (var error in operationsResult.Errors)
                    await operationsTracker.AddExceptionAsync("operations", error.Message, error.ToString());
                await operationsTracker.FinishAsync(operationsResult.Errors.Count == 0, new
                {
                    operationsResult.SanctionsWritten,
                    operationsResult.GlobalSanctionsWritten,
                    operationsResult.ReportsWritten,
                    operationsService.ReconciliationMismatches,
                    Errors = operationsResult.Errors.Count
                });
                Console.WriteLine(
                    $"Operations {(operationsReconcile ? "reconciliation" : "backfill")}: " +
                    $"sanctions={operationsResult.SanctionsWritten}, global={operationsResult.GlobalSanctionsWritten}, " +
                    $"reports={operationsResult.ReportsWritten}, stats={operationsResult.UserStatisticsWritten + operationsResult.GroupUserStatisticsWritten}, " +
                    $"audit={operationsResult.AuditEventsWritten}, mismatches={operationsService.ReconciliationMismatches}, " +
                    $"errors={operationsResult.Errors.Count}");
                return operationsResult.Errors.Count == 0 ? 0 : 1;
                }
                catch (Exception e)
                {
                    await operationsTracker.FailAsync(e);
                    throw;
                }
            }
            if (IsModerationMigration(args, out var moderationReconcile))
            {
                if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
                await using var moderationLease = moderationReconcile
                    ? await MigrationMaintenanceGuard.AcquireAsync()
                    : null;
                await using var moderationDatabase = PostgresDatabase.FromConfiguration();
                var moderationTracker = await MigrationRunTracker.StartAsync(moderationDatabase,
                    $"moderation-{(moderationReconcile ? "reconcile" : "backfill")}");
                try
                {
                var groupIds = new System.Collections.Generic.HashSet<long>();
                if (groupScope != null) groupIds.Add(groupScope.GroupId);
                else
                {
                    var groupValues = await Redis.db.SetMembersAsync("bot:groupsid");
                    foreach (var value in groupValues)
                        if (long.TryParse(value.ToString(), out var id)) groupIds.Add(id);
                }
                var moderationService = new ModerationBackfillService(moderationDatabase);
                var moderationResult = await moderationService.RunRedisPrimaryAsync(groupIds,
                    moderationReconcile, includeGlobalRoles: groupScope == null,
                    moderationLease?.Token ?? default);
                await moderationTracker.FinishAsync(true, new
                {
                    moderationResult.RolesWritten, moderationResult.ElevationBlocksWritten,
                    moderationResult.PoliciesWritten, moderationResult.WarningBalancesWritten,
                    moderationService.ReconciliationMismatches
                });
                Console.WriteLine(
                    $"Moderation {(moderationReconcile ? "reconciliation" : "backfill")}: " +
                    $"roles={moderationResult.RolesWritten}, blocks={moderationResult.ElevationBlocksWritten}, " +
                    $"policies={moderationResult.PoliciesWritten}, warnings={moderationResult.WarningBalancesWritten}, " +
                    $"mismatches={moderationService.ReconciliationMismatches}");
                return 0;
                }
                catch (Exception e)
                {
                    await moderationTracker.FailAsync(e);
                    throw;
                }
            }
            if (IsPrincipalContentMigration(args, out var principalReconcile))
            {
                if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
                await using var principalLease = principalReconcile
                    ? await MigrationMaintenanceGuard.AcquireAsync()
                    : null;
                await using var principalDatabase = PostgresDatabase.FromConfiguration();
                var principalTracker = await MigrationRunTracker.StartAsync(principalDatabase,
                    $"principal-content-{(principalReconcile ? "reconcile" : "backfill")}");
                try
                {
                var userIds = new System.Collections.Generic.HashSet<long>();
                var groupIds = new System.Collections.Generic.HashSet<long>();
                if (groupScope != null)
                {
                    userIds.UnionWith(groupScope.UserIds);
                    groupIds.Add(groupScope.GroupId);
                }
                else
                {
                    var usernameIds = await Redis.db.HashValuesAsync("bot:usernames");
                    foreach (var value in usernameIds)
                        if (long.TryParse(value.ToString(), out var id)) userIds.Add(id);
                    await foreach (var principalKey in ScanKeysAsync("user:*"))
                    {
                        var parts = principalKey.ToString().Split(':');
                        if (parts.Length == 2 && long.TryParse(parts[1], out var id)) userIds.Add(id);
                    }
                    var groupValues = await Redis.db.SetMembersAsync("bot:groupsid");
                    foreach (var value in groupValues)
                        if (long.TryParse(value.ToString(), out var id)) groupIds.Add(id);
                }
                var principalService = new PrincipalContentBackfillService(principalDatabase);
                var principalResult = await principalService.RunRedisPrimaryAsync(userIds, groupIds,
                    principalReconcile, principalLease?.Token ?? default);
                await principalTracker.FinishAsync(true, new
                {
                    principalResult.UsersWritten, principalResult.GroupsWritten,
                    principalResult.EntitlementsWritten, principalResult.ContentItemsWritten,
                    principalResult.CustomCommandsWritten, principalService.ReconciliationMismatches
                });
                Console.WriteLine(
                    $"Principal/content {(principalReconcile ? "reconciliation" : "backfill")}: " +
                    $"users={principalResult.UsersWritten}, groups={principalResult.GroupsWritten}, " +
                    $"entitlements={principalResult.EntitlementsWritten}, content={principalResult.ContentItemsWritten}, " +
                    $"commands={principalResult.CustomCommandsWritten}, " +
                    $"mismatches={principalService.ReconciliationMismatches}");
                return 0;
                }
                catch (Exception e)
                {
                    await principalTracker.FailAsync(e);
                    throw;
                }
            }
            if (!IsGroupSettingsMigration(args, out var reconcile)) return 2;

            if (!Redis.Start())
                throw new InvalidOperationException("Redis is unavailable.");
            await using var settingsLease = reconcile
                ? await MigrationMaintenanceGuard.AcquireAsync()
                : null;
            await using var database = PostgresDatabase.FromConfiguration();
            var settingsTracker = await MigrationRunTracker.StartAsync(database,
                $"group-settings-{(reconcile ? "reconcile" : "backfill")}");
            try
            {
            var settingsService = new GroupSettingsMigrationService(database);
            var result = groupScope == null
                ? await settingsService.RunAsync(reconcile, settingsLease?.Token ?? default)
                : await settingsService.RunAsync(new[] { groupScope.GroupId }, reconcile,
                    settingsLease?.Token ?? default);
            var settingsSucceeded = result.Failures == 0 && result.InvalidGroups == 0;
            if (!settingsSucceeded)
                await settingsTracker.AddExceptionAsync("group-settings",
                    $"failures={result.Failures}, invalid-groups={result.InvalidGroups}");
            await settingsTracker.FinishAsync(settingsSucceeded, result);
            Console.WriteLine(
                $"Group settings {(reconcile ? "reconciliation" : "backfill")}: " +
                $"processed={result.Processed}/{result.Expected}, mismatches={result.Mismatches}, " +
                $"failures={result.Failures}, invalid-groups={result.InvalidGroups}");
            return result.Failures == 0 && result.InvalidGroups == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                await settingsTracker.FailAsync(e);
                throw;
            }
        }

        private static async IAsyncEnumerable<string> ScanKeysAsync(string pattern,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var cursor = "0";
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = (StackExchange.Redis.RedisResult[])await Redis.db.ExecuteAsync(
                    "SCAN", cursor, "MATCH", pattern, "COUNT", 5000);
                if (result.Length != 2)
                    throw new InvalidOperationException("Redis returned a malformed SCAN response.");
                cursor = result[0].ToString();
                foreach (var key in (StackExchange.Redis.RedisResult[])result[1])
                    yield return key.ToString();
            } while (!string.Equals(cursor, "0", StringComparison.Ordinal));
        }
    }
}
