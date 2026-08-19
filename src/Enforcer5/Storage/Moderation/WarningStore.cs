using System;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Moderation
{
    internal static class WarningStore
    {
        internal static async Task<WarningIssueResult> IssuePrewarningAsync(long groupId, long userId)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId))
            {
                var balance = await Redis.db.HashIncrementAsync($"chat:{groupId}:prewarns", userId, 1);
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, $"chat:{groupId}:prewarns");
                var result = new WarningIssueResult(balance, 1, balance);
                await ShadowBalanceAsync(groupId, userId, WarningTypes.Prewarning, balance, balance);
                return result;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new WarningRepository(database).IssuePrewarningAsync(groupId, userId);
        }

        internal static async Task<WarningIssueResult> IssueWarningAsync(long groupId, long userId)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId))
            {
                long.TryParse((await Redis.db.HashGetAsync($"chat:{groupId}:prewarns", userId)).ToString(),
                    out var prewarnings);
                if (prewarnings != 0)
                    await Redis.db.HashSetAsync($"chat:{groupId}:prewarns", userId, 0);
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, $"chat:{groupId}:prewarns");
                var quantity = 1 + Math.Max(0, prewarnings);
                var balance = await Redis.db.HashIncrementAsync($"chat:{groupId}:warns", userId, quantity);
                var lifetime = await Redis.db.HashIncrementAsync($"chat:{groupId}:totalWarns", userId, quantity);
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, $"chat:{groupId}:warns");
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, $"chat:{groupId}:totalWarns");
                await ShadowBalanceAsync(groupId, userId, WarningTypes.Prewarning, 0, Math.Max(0, prewarnings));
                await ShadowBalanceAsync(groupId, userId, WarningTypes.Standard, balance, lifetime);
                return new WarningIssueResult(balance, quantity, lifetime);
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new WarningRepository(database).IssueWarningAsync(groupId, userId);
        }

        internal static async Task<WarningIssueResult> IssueAsync(long groupId, long userId,
            string warningType)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId))
            {
                var balance = await Redis.db.HashIncrementAsync(Key(groupId, warningType), userId, 1);
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, Key(groupId, warningType));
                if (balance < 0)
                {
                    balance = 0;
                    await Redis.db.HashSetAsync(Key(groupId, warningType), userId, balance);
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, Key(groupId, warningType));
                }
                var lifetime = balance;
                if (warningType == WarningTypes.Standard)
                    lifetime = await Redis.db.HashIncrementAsync($"chat:{groupId}:totalWarns", userId, 1);
                    await Cutover.GroupDrainBuffer.AppendAsync(groupId, $"chat:{groupId}:totalWarns");
                var result = new WarningIssueResult(Math.Max(0, balance), 1, Math.Max(0, lifetime));
                await ShadowBalanceAsync(groupId, userId, warningType, result.Balance,
                    result.LifetimeQuantity);
                return result;
            }

            await using var database = PostgresDatabase.FromConfiguration();
            return await new WarningRepository(database).IssueAsync(groupId, userId, warningType);
        }

        internal static async Task<WarningIssueResult> ResetAsync(long groupId, long userId,
            string warningType)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId))
            {
                var current = await GetBalanceAsync(groupId, userId, warningType);
                await Redis.db.HashSetAsync(Key(groupId, warningType), userId, 0);
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, Key(groupId, warningType));
                await ShadowBalanceAsync(groupId, userId, warningType, 0, current.LifetimeQuantity);
                return new WarningIssueResult(0, 0, current.LifetimeQuantity);
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new WarningRepository(database).ResetAsync(groupId, userId, warningType);
        }

        internal static async Task<WarningIssueResult> DecrementAsync(long groupId, long userId,
            string warningType)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId))
            {
                long.TryParse((await Redis.db.HashGetAsync(Key(groupId, warningType), userId)).ToString(),
                    out var current);
                var balance = WarningDecision.Decrement(current);
                await Redis.db.HashSetAsync(Key(groupId, warningType), userId, balance);
                await Cutover.GroupDrainBuffer.AppendAsync(groupId, Key(groupId, warningType));
                var existing = await GetBalanceAsync(groupId, userId, warningType);
                await ShadowBalanceAsync(groupId, userId, warningType, balance,
                    existing.LifetimeQuantity);
                return new WarningIssueResult(balance, 1, existing.LifetimeQuantity);
            }

            await using var database = PostgresDatabase.FromConfiguration();
            return await new WarningRepository(database).DecrementAsync(groupId, userId, warningType);
        }

        internal static async Task<WarningBalance> GetBalanceAsync(long groupId, long userId,
            string warningType)
        {
            if (!StorageRouting.GroupPostgresIsPrimary(groupId))
            {
                long.TryParse((await Redis.db.HashGetAsync(Key(groupId, warningType), userId)).ToString(),
                    out var active);
                var lifetime = active;
                if (warningType == WarningTypes.Standard)
                    long.TryParse((await Redis.db.HashGetAsync($"chat:{groupId}:totalWarns", userId)).ToString(),
                        out lifetime);
                return new WarningBalance(groupId, userId, warningType, Math.Max(0, active),
                    Math.Max(0, lifetime), DateTimeOffset.UtcNow);
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new WarningRepository(database).GetBalanceAsync(groupId, userId, warningType)
                   ?? new WarningBalance(groupId, userId, warningType, 0, 0, DateTimeOffset.UtcNow);
        }

        private static async Task ShadowBalanceAsync(long groupId, long userId, string warningType,
            long active, long lifetime)
        {
            if (!string.Equals(RegHelper.GetRegValue("ModerationShadowEnabled"), "true",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection"))) return;
            await using var database = PostgresDatabase.FromConfiguration();
            await new WarningRepository(database).UpsertBackfillAsync(new WarningBalance(groupId, userId,
                warningType, Math.Max(0, active), Math.Max(0, lifetime), DateTimeOffset.UtcNow));
        }

        private static string Key(long groupId, string warningType) => warningType switch
        {
            WarningTypes.Standard => $"chat:{groupId}:warns",
            WarningTypes.Prewarning => $"chat:{groupId}:prewarns",
            WarningTypes.Media => $"chat:{groupId}:mediawarn",
            _ => throw new ArgumentOutOfRangeException(nameof(warningType))
        };
    }
}
