using System.Threading.Tasks;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5.Storage.PrincipalContent
{
    internal static class PrincipalContentStore
    {
        internal static async Task ObserveUserAsync(User user)
        {
            if (user == null) return;
            var principal = new UserPrincipal(user.Id, user.FirstName,
                user.Username == null ? null : $"@{user.Username.ToLowerInvariant()}");
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                await Redis.db.HashSetAsync($"user:{user.Id}", "name", user.FirstName);
                if (principal.Username != null)
                {
                    await Redis.db.HashSetAsync("bot:usernames", principal.Username, user.Id);
                    await Redis.db.HashSetAsync($"user:{user.Id}", "username", principal.Username);
                }
                if (!ShadowEnabled) return;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            await new UserRepository(database).UpsertAsync(principal);
        }

        internal static async Task ObserveGroupAsync(Chat chat)
        {
            if (chat == null || chat.Type == Telegram.Bot.Types.Enums.ChatType.Private) return;
            var principal = new GroupPrincipal(chat.Id, chat.Title);
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                await Redis.db.HashSetAsync($"chat:{chat.Id}:details", "name", chat.Title);
                if (!ShadowEnabled) return;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            await new GroupRepository(database).UpsertAsync(principal);
        }

        internal static async Task<bool> IsPremiumAsync(long chatId)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
                return await Redis.db.SetContainsAsync("premiumBot", chatId);
            await using var database = PostgresDatabase.FromConfiguration();
            var rows = await new GroupEntitlementRepository(database).GetAsync(chatId);
            foreach (var row in rows)
                if (row.Entitlement == "premium" && row.Enabled) return true;
            return false;
        }

        internal static async Task<UserPrincipal> GetUserAsync(long userId)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                var values = await Redis.db.HashGetAsync($"user:{userId}",
                    new StackExchange.Redis.RedisValue[] { "name", "username" });
                return new UserPrincipal(userId, values[0].ToString(), values[1].ToString());
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return await new UserRepository(database).GetAsync(userId);
        }

        internal static async Task<long?> ResolveUsernameAsync(string username)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                var value = await Redis.db.HashGetAsync("bot:usernames", username);
                return long.TryParse(value.ToString(), out var id) ? id : null;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            return (await new UserRepository(database).GetByUsernameAsync(username))?.UserId;
        }

        internal static async Task<bool> GroupExistsAsync(long chatId)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
                return await Redis.db.SetContainsAsync("bot:groupsid", chatId);
            await using var database = PostgresDatabase.FromConfiguration();
            return await new GroupRepository(database).GetAsync(chatId) != null;
        }

        internal static async Task SetPremiumAsync(long chatId, bool enabled)
        {
            if (!StorageModeProvider.PostgresIsPrimary)
            {
                if (enabled) await Redis.db.SetAddAsync("premiumBot", chatId);
                else await Redis.db.SetRemoveAsync("premiumBot", chatId);
                if (!ShadowEnabled) return;
            }
            await using var database = PostgresDatabase.FromConfiguration();
            await new GroupRepository(database).UpsertAsync(new GroupPrincipal(chatId, null));
            await new GroupEntitlementRepository(database).UpsertAsync(
                new GroupEntitlement(chatId, "premium", enabled));
        }

        private static bool ShadowEnabled =>
            string.Equals(RegHelper.GetRegValue("PrincipalContentShadowEnabled"), "true",
                System.StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection"));
    }
}
