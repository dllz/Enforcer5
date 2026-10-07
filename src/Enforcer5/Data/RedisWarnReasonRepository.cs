using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Redis layout:
    /// <list type="bullet">
    /// <item><c>chat:{chatId}:warnreasonmode</c>: the <see cref="WarnReasonMode"/> as a number.</item>
    /// <item><c>chat:{chatId}:warnreasons</c>: saved reasons as a JSON array. A string rather than a
    /// list so that "edited to empty" (<c>[]</c>) differs from "never edited" (no key).</item>
    /// <item><c>chat:{chatId}:recentwarnreasons</c>: list of recent reasons, newest first.</item>
    /// <item><c>chat:{chatId}:warnreasonprompt:{messageId}</c>: a <see cref="PendingWarnRemoval"/> as
    /// JSON, expiring with the question.</item>
    /// </list>
    /// </summary>
    internal sealed class RedisWarnReasonRepository : IWarnReasonRepository
    {
        // Resolved per call: the connection is only established by Redis.Start(), after this
        // repository has been constructed.
        private readonly Func<IDatabaseAsync> _database;

        public RedisWarnReasonRepository(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public async Task<WarnReasonMode> GetModeAsync(long chatId)
        {
            var value = await _database().StringGetAsync(ModeKey(chatId), CommandFlags.None);
            return value.TryParse(out int mode) && Enum.IsDefined(typeof(WarnReasonMode), mode)
                ? (WarnReasonMode)mode
                : WarnReasonMode.Off;
        }

        public Task SetModeAsync(long chatId, WarnReasonMode mode) =>
            _database().StringSetAsync(ModeKey(chatId), (int)mode, null, When.Always, CommandFlags.None);

        public async Task<IReadOnlyList<string>> GetSavedAsync(long chatId)
        {
            var value = await _database().StringGetAsync(SavedKey(chatId), CommandFlags.None);
            if (value.IsNullOrEmpty) return null;
            try
            {
                return JsonSerializer.Deserialize<string[]>(value.ToString()) ?? Array.Empty<string>();
            }
            catch (JsonException e)
            {
                LogHelper.Error($"Unreadable warn reasons in {chatId}, using the defaults: {e.Message}");
                return null;
            }
        }

        public Task SetSavedAsync(long chatId, IReadOnlyList<string> reasons) =>
            _database().StringSetAsync(SavedKey(chatId), JsonSerializer.Serialize(reasons ?? Array.Empty<string>()),
                null, When.Always, CommandFlags.None);

        public async Task<IReadOnlyList<string>> GetRecentAsync(long chatId)
        {
            var values = await _database().ListRangeAsync(RecentKey(chatId), 0, WarnReasons.MaxRecent - 1, CommandFlags.None);
            return values.Where(v => !v.IsNullOrEmpty).Select(v => v.ToString()).ToList();
        }

        public async Task AddRecentAsync(long chatId, string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            var db = _database();
            var key = RecentKey(chatId);
            // Not atomic, which at worst leaves a duplicate until it is pushed out; the choice
            // list removes duplicates anyway.
            await db.ListRemoveAsync(key, reason, 0, CommandFlags.None);
            await db.ListLeftPushAsync(key, reason, When.Always, CommandFlags.None);
            await db.ListTrimAsync(key, 0, WarnReasons.MaxRecent - 1, CommandFlags.None);
        }

        public Task SavePendingAsync(long chatId, int promptMessageId, PendingWarnRemoval pending, TimeSpan lifetime)
        {
            if (pending == null) throw new ArgumentNullException(nameof(pending));
            return _database().StringSetAsync(PendingKey(chatId, promptMessageId), JsonSerializer.Serialize(pending),
                lifetime, When.Always, CommandFlags.None);
        }

        public async Task<PendingWarnRemoval> GetPendingAsync(long chatId, int promptMessageId)
        {
            var value = await _database().StringGetAsync(PendingKey(chatId, promptMessageId), CommandFlags.None);
            if (value.IsNullOrEmpty) return null;
            try
            {
                return JsonSerializer.Deserialize<PendingWarnRemoval>(value.ToString());
            }
            catch (JsonException e)
            {
                LogHelper.Error($"Unreadable pending warn removal {promptMessageId} in {chatId}: {e.Message}");
                return null;
            }
        }

        public Task<bool> ClaimPendingAsync(long chatId, int promptMessageId) =>
            _database().KeyDeleteAsync(PendingKey(chatId, promptMessageId), CommandFlags.None);

        private static RedisKey ModeKey(long chatId) => $"chat:{chatId}:warnreasonmode";
        private static RedisKey SavedKey(long chatId) => $"chat:{chatId}:warnreasons";
        private static RedisKey RecentKey(long chatId) => $"chat:{chatId}:recentwarnreasons";
        private static RedisKey PendingKey(long chatId, int messageId) => $"chat:{chatId}:warnreasonprompt:{messageId}";
    }
}
