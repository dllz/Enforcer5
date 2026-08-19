using System;
using System.Globalization;
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

        internal static bool IsUserTierMigration(string[] args, out bool reconcile)
        {
            var parsed = TryParseDomain(args, "user-tier", out var options);
            reconcile = options?.Reconcile == true;
            return parsed;
        }

        /// <summary>
        /// <c>migrate group &lt;chatId&gt; [--rollback]</c> - cut one group over, or put it back.
        /// <para>
        /// Runs alongside the live bots rather than requiring them stopped. That is the whole point
        /// of the per-group lease: the group is copied without a lock, and only the final flip is
        /// exclusive. Other processes learn the new state over pub/sub.
        /// </para>
        /// </summary>
        internal static bool IsGroupCutover(string[] args, out long chatId, out bool rollback)
        {
            chatId = 0;
            rollback = false;
            if (args == null || args.Length is not (3 or 4) ||
                !string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(args[1], "group", StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId) ||
                chatId == 0) return false;
            if (args.Length == 3) return true;
            rollback = string.Equals(args[3], "--rollback", StringComparison.OrdinalIgnoreCase);
            return rollback;
        }

        /// <summary>
        /// <c>migrate tempban-index [--apply]</c>. Dry run by default, because it writes to Redis.
        /// </summary>
        internal static bool IsTempBanIndex(string[] args, out bool apply)
        {
            apply = false;
            if (args == null || args.Length is not (2 or 3) ||
                !string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(args[1], "tempban-index", StringComparison.OrdinalIgnoreCase)) return false;
            if (args.Length == 2) return true;
            apply = string.Equals(args[2], "--apply", StringComparison.OrdinalIgnoreCase);
            return apply;
        }

        /// <summary>
        /// <c>migrate inventory [--fresh]</c>. A bare run resumes an unfinished pass; <c>--fresh</c>
        /// forces a new generation, which is what Stage C needs when it wants an inventory strictly
        /// newer than a group's drain start.
        /// </summary>
        internal static bool IsInventoryScan(string[] args, out bool fresh)
        {
            fresh = false;
            if (args == null || args.Length is not (2 or 3) ||
                !string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(args[1], "inventory", StringComparison.OrdinalIgnoreCase)) return false;
            if (args.Length == 2) return true;
            fresh = string.Equals(args[2], "--fresh", StringComparison.OrdinalIgnoreCase);
            return fresh;
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

        /// <summary>
        /// One full keyspace pass, recorded in PostgreSQL. Everything downstream reads this rather
        /// than re-scanning, because <c>SCAN MATCH</c> walks the whole keyspace regardless of the
        /// pattern and N prefixes therefore cost N passes.
        /// </summary>
        private static async Task<int> RunInventoryAsync(bool fresh)
        {
            if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
            await using var database = PostgresDatabase.FromConfiguration();
            var tracker = await MigrationRunTracker.StartAsync(database,
                fresh ? "inventory-scan-fresh" : "inventory-scan");
            try
            {
                var result = await new Loading.KeyspaceInventoryScanner(database, Redis.db)
                    .ScanAsync(resume: !fresh);
                await tracker.FinishAsync(result.Completed, result);
                Console.WriteLine(
                    $"Keyspace inventory: generation={result.GenerationId}, keys={result.KeyCount:N0}, " +
                    $"unclassified={result.UnclassifiedCount:N0}, " +
                    $"{(result.Completed ? "complete" : "interrupted - re-run to resume")}");
                return result.Completed ? 0 : 1;
            }
            catch (Exception e)
            {
                await tracker.FailAsync(e);
                throw;
            }
        }

        /// <summary>
        /// Cuts a single group over, or rolls one back.
        /// <para>
        /// Safe to run while the bots are up. The copy holds no lock, the flip is guarded by the
        /// group's lease, and the resulting state is announced so every other process stops treating
        /// the group as Redis-owned.
        /// </para>
        /// </summary>
        private static async Task<int> RunGroupCutoverAsync(long telegramChatId, bool rollback)
        {
            if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
            await using var database = PostgresDatabase.FromConfiguration();
            await Cutover.GroupMigrationStateStore.PrimeAsync(database);

            var owner = $"{Environment.MachineName}-cli-{Environment.ProcessId}";

            if (rollback)
            {
                // Flush the mirror first. Any entry still pending is a PostgreSQL write that Redis
                // has not received, and flipping before it drains loses exactly those writes.
                var applied = await new CompatibilityOutboxWorker(database).DrainAsync();
                var rolled = await Cutover.GroupMigrationStateStore.RollBackAsync(database,
                    telegramChatId, owner, "manual rollback");
                if (!rolled)
                {
                    Console.Error.WriteLine($"Group {telegramChatId} NOT rolled back: another " +
                                            "migrator holds its lease. Nothing changed.");
                    return 1;
                }
                Console.WriteLine($"Group {telegramChatId} rolled back to Redis " +
                                  $"({applied} mirror entr{(applied == 1 ? "y" : "ies")} flushed first).");
                return 0;
            }

            var result = await new Cutover.GroupCutoverService(database, owner)
                .CutOverAsync(telegramChatId);

            Console.WriteLine(result.Succeeded
                ? $"Group {telegramChatId} cut over in {result.Elapsed.TotalSeconds:N1}s " +
                  $"({result.DrainedKeys} drained key(s))."
                : $"Group {telegramChatId} NOT cut over: {result.Failure}. " +
                  "It remains on Redis and is unaffected.");
            return result.Succeeded ? 0 : 1;
        }

        /// <summary>
        /// Materialises per-group tempban keys for anything held only in the process-wide hash, so
        /// that per-group work stops needing to read the whole estate.
        /// </summary>
        private static async Task<int> RunTempBanIndexAsync(bool apply)
        {
            if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
            var result = await Loading.TempBanIndexMaterialiser.RunAsync(apply);
            Console.WriteLine(
                $"Tempban index{(apply ? string.Empty : " (dry run, pass --apply to write)")}: " +
                $"global={result.GlobalEntries}, already-per-group={result.AlreadyPerGroup}, " +
                $"materialised={result.Materialised}, lapsed={result.Lapsed}, " +
                $"malformed={result.Malformed}, orphaned-duplicates={result.OrphanedDuplicates}");
            return 0;
        }

        /// <summary>
        /// The Stage B user and global tier load. Requires a completed inventory generation - a
        /// partial one would silently migrate a subset and report success.
        /// </summary>
        private static async Task<int> RunUserTierAsync(bool reconcile)
        {
            if (reconcile)
            {
                Console.Error.WriteLine("User-tier reconciliation is not implemented yet.");
                return 2;
            }
            if (!Redis.Start()) throw new InvalidOperationException("Redis is unavailable.");
            await using var database = PostgresDatabase.FromConfiguration();

            var generation = await Loading.KeyspaceInventoryScanner.LatestCompletedAsync(database);
            if (generation == null)
            {
                Console.Error.WriteLine(
                    "No completed keyspace inventory. Run 'migrate inventory' first.");
                return 2;
            }

            var tracker = await MigrationRunTracker.StartAsync(database, "user-tier-backfill");
            try
            {
                var result = await new Loading.UserTierLoader(database, Redis.db)
                    .LoadAsync(generation.Value.GenerationId, tracker);
                await tracker.FinishAsync(true, result);
                Console.WriteLine(
                    $"User tier backfill (generation {generation.Value.GenerationId}): " +
                    $"users={result.UsersStaged:N0}, group-counters={result.GroupCountersStaged:N0}, " +
                    $"accounts-created={result.AccountsCreated:N0}, groups-created={result.GroupsCreated:N0}, " +
                    $"missing-names={result.MissingNames:N0}");
                return 0;
            }
            catch (Exception e)
            {
                await tracker.FailAsync(e);
                throw;
            }
        }

        internal static async Task<int> RunAsync(string[] args)
        {
            if (IsSchemaMigration(args)) return await RunSchemaAsync();
            if (IsInventoryScan(args, out var fresh)) return await RunInventoryAsync(fresh);
            if (IsTempBanIndex(args, out var applyIndex)) return await RunTempBanIndexAsync(applyIndex);
            if (IsGroupCutover(args, out var cutoverChatId, out var rollback))
                return await RunGroupCutoverAsync(cutoverChatId, rollback);
            if (IsUserTierMigration(args, out var userTierReconcile))
                return await RunUserTierAsync(userTierReconcile);
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
                // Populations come from the recorded inventory, not from live scans. This block used
                // to run three full SCANs - user:*, globalBan:* and flagged:* - each walking all
                // 19.4M keys, because MATCH filters after the walk rather than before it.
                var groupIds = new System.Collections.Generic.HashSet<long>();
                var userIds = new System.Collections.Generic.HashSet<long>();
                System.Collections.Generic.IReadOnlyDictionary<long,
                    System.Collections.Generic.IReadOnlyCollection<long>> reportMessageIds;

                if (groupScope != null)
                {
                    groupIds.Add(groupScope.GroupId);
                    userIds.UnionWith(groupScope.UserIds);
                    reportMessageIds = new System.Collections.Generic.Dictionary<long,
                        System.Collections.Generic.IReadOnlyCollection<long>>
                    {
                        [groupScope.GroupId] = groupScope.ReportMessageIds
                    };
                }
                else
                {
                    var generation = await Loading.MigrationInventory.LatestAsync(operationsDatabase);
                    if (generation == null)
                    {
                        Console.Error.WriteLine(
                            "No completed keyspace inventory. Run 'migrate inventory' first.");
                        await operationsTracker.FinishAsync(false, new { error = "no inventory" });
                        return 2;
                    }
                    var generationId = generation.Value.GenerationId;
                    groupIds.UnionWith(await Loading.MigrationInventory.GroupIdsAsync(
                        operationsDatabase, generationId));
                    userIds.UnionWith(await Loading.MigrationInventory.UserIdsAsync(
                        operationsDatabase, generationId));
                    reportMessageIds = await Loading.MigrationInventory.ReportMessageIdsAsync(
                        operationsDatabase, generationId);
                }
                var operationsService = new OperationsBackfillService(operationsDatabase);
                var operationsResult = await operationsService.RunRedisPrimaryAsync(
                    groupIds, userIds, reportMessageIds, operationsReconcile,
                    operationsLease?.Token ?? default, includeGlobalData: groupScope == null,
                    tracker: operationsTracker);
                foreach (var error in operationsResult.Errors)
                    await operationsTracker.AddExceptionAsync("operations", error.Message, error.ToString());
                // Migrated, but the source was defective or undecidable. Recorded as warnings so a
                // noisy run is distinguishable from a failed one.
                foreach (var issue in operationsResult.DataQualityIssues)
                    await operationsTracker.AddExceptionAsync("operations", issue.Message,
                        issue.ToString(), MigrationSeverities.Warning);
                await operationsTracker.FinishAsync(operationsResult.Errors.Count == 0, new
                {
                    operationsResult.SanctionsWritten,
                    operationsResult.GlobalSanctionsWritten,
                    operationsResult.ReportsWritten,
                    operationsService.ReconciliationMismatches,
                    Reconciliation = operationsService.ReconciliationOutcome.ToString(),
                    Errors = operationsResult.Errors.Count,
                    Warnings = operationsResult.DataQualityIssues.Count
                });
                Console.WriteLine(
                    $"Operations {(operationsReconcile ? "reconciliation" : "backfill")}: " +
                    $"sanctions={operationsResult.SanctionsWritten}, global={operationsResult.GlobalSanctionsWritten}, " +
                    $"reports={operationsResult.ReportsWritten}, last-seen={operationsResult.LastSeenApplied}, " +
                    $"counters={operationsResult.ModerationCountersWritten}, " +
                    $"audit={operationsResult.AuditEventsWritten}, mismatches={operationsService.ReconciliationMismatches}, " +
                    $"errors={operationsResult.Errors.Count}, warnings={operationsResult.DataQualityIssues.Count}");
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
                // Not bot:groupsid: it misses the 7,951 groups that hold data but were never
                // registered, every one of which has roles, policies and warnings to migrate.
                else groupIds.UnionWith(await Loading.MigrationInventory.GroupIdsOrDiscoverAsync(
                    moderationDatabase));
                var moderationService = new ModerationBackfillService(moderationDatabase);
                var moderationResult = await moderationService.RunRedisPrimaryAsync(groupIds,
                    moderationReconcile, includeGlobalRoles: groupScope == null,
                    moderationLease?.Token ?? default);
                // Was hardcoded true: a reconciliation that found mismatches still reported a
                // completed run, so the status column could not be used as a gate.
                await moderationTracker.FinishAsync(moderationService.ReconciliationMismatches == 0, new
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
                    groupIds.UnionWith(await Loading.MigrationInventory.GroupIdsOrDiscoverAsync(
                        principalDatabase));
                }
                var principalService = new PrincipalContentBackfillService(principalDatabase);
                var principalResult = await principalService.RunRedisPrimaryAsync(userIds, groupIds,
                    principalReconcile, principalLease?.Token ?? default);
                await principalTracker.FinishAsync(principalService.ReconciliationMismatches == 0, new
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
