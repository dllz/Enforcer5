using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    internal static class TelegramCapabilityStore
    {
#if PREMIUM
        internal const string CurrentEdition = "premium";
#else
        internal const string CurrentEdition = "normal";
#endif

        internal static async Task UpdateAsync(ChatMemberUpdated update)
        {
            var member = update.NewChatMember;
            var active = member.Status == ChatMemberStatus.Member ||
                         member.Status == ChatMemberStatus.Administrator;
            var administrator = member.Status == ChatMemberStatus.Administrator;
            var key = Key(update.Chat.Id, CurrentEdition);
            await Redis.db.HashSetAsync(key, new[]
            {
                new HashEntry("active", active ? "1" : "0"),
                new HashEntry("administrator", administrator ? "1" : "0"),
                new HashEntry("observedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            });
            await Redis.db.KeyExpireAsync(key, active && administrator
                ? TimeSpan.FromHours(1)
                : TimeSpan.FromMinutes(5));
        }

        internal static async Task<bool> CanModerateAsync(long chatId, string edition)
        {
            var values = await Redis.db.HashGetAsync(Key(chatId, edition),
                new RedisValue[] { "active", "administrator" });
            if (values.Length == 2 && values[0].HasValue && values[1].HasValue)
                return values[0] == "1" && values[1] == "1";
            if (edition != CurrentEdition) return false;
            try
            {
                var me = Bot.Me ?? await Bot.Api.GetMe();
                var member = await Bot.Api.GetChatMember(chatId, me.Id);
                var active = member.Status == ChatMemberStatus.Member ||
                             member.Status == ChatMemberStatus.Administrator;
                var administrator = member.Status == ChatMemberStatus.Administrator;
                var key = Key(chatId, edition);
                await Redis.db.HashSetAsync(key, new[]
                {
                    new HashEntry("active", active ? "1" : "0"),
                    new HashEntry("administrator", administrator ? "1" : "0"),
                    new HashEntry("observedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                });
                await Redis.db.KeyExpireAsync(key, active && administrator
                    ? TimeSpan.FromHours(1)
                    : TimeSpan.FromMinutes(5));
                return active && administrator;
            }
            catch
            {
                return false;
            }
        }

        internal static Task SetCooldownAsync(string edition, long chatId, TimeSpan duration)
        {
            return Redis.db.StringSetAsync(CooldownKey(edition, chatId), "1", duration);
        }

        internal static async Task<bool> IsAvailableAsync(string edition, long chatId)
        {
            return !await Redis.db.KeyExistsAsync(CooldownKey(edition, chatId));
        }

        private static string Key(long chatId, string edition) =>
            $"enforcer:capability:{chatId}:{edition}";

        private static string CooldownKey(string edition, long chatId) =>
            $"enforcer:telegram:cooldown:{edition}:{chatId}";
    }
}
