using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Warn removal reasons. The group's mode, saved reasons and recent reasons are part of its config since rollout
    /// step 17 (PostgreSQL through <see cref="GroupConfigs"/>: in memory, queued while it is down); the removals waiting
    /// for a reason stay in Redis, expiring with their question:
    /// <c>chat:{chatId}:warnreasonprompt:{messageId}</c>, a <see cref="PendingWarnRemoval"/> as JSON.
    /// The old keys (<c>warnreasonmode</c>, <c>warnreasons</c>, <c>recentwarnreasons</c>) are read once, by the group's
    /// config import (<see cref="LegacyGroupConfig"/>), and never written.
    /// </summary>
    internal sealed class WarnReasonRepository : IWarnReasonRepository
    {
        // Resolved per call: the connection is only established by Redis.Start(), after this repository was constructed.
        private readonly Func<IDatabaseAsync> _database;
        private readonly Func<long, Task<GroupConfig>> _config;
        private readonly Func<long, ConfigChange, Task> _change;

        /// <param name="config">The group's config (default: <see cref="GroupConfigs.GetAsync"/>).</param>
        /// <param name="change">Changes it (default: <see cref="GroupConfigs.ChangeAsync"/>).</param>
        public WarnReasonRepository(Func<IDatabaseAsync> database, Func<long, Task<GroupConfig>> config = null,
            Func<long, ConfigChange, Task> change = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _config = config ?? GroupConfigs.GetAsync;
            _change = change ?? GroupConfigs.ChangeAsync;
        }

        public async Task<WarnReasonMode> GetModeAsync(long chatId) => (await _config(chatId)).WarnReasonMode;

        public Task SetModeAsync(long chatId, WarnReasonMode mode) => _change(chatId, ConfigChange.SetWarnReasonMode(mode));

        public async Task<IReadOnlyList<string>> GetSavedAsync(long chatId) => (await _config(chatId)).WarnReasons;

        public Task SetSavedAsync(long chatId, IReadOnlyList<string> reasons) =>
            _change(chatId, ConfigChange.SetWarnReasons(reasons ?? Array.Empty<string>()));

        public async Task<IReadOnlyList<string>> GetRecentAsync(long chatId) => (await _config(chatId)).RecentWarnReasons;

        public Task AddRecentAsync(long chatId, string reason) =>
            string.IsNullOrEmpty(reason) ? Task.CompletedTask : _change(chatId, ConfigChange.UsedWarnReason(reason));

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
                ErrorReporter.Report(e, $"Unreadable pending warn removal {promptMessageId} in {chatId}");
                return null;
            }
        }

        public Task<bool> ClaimPendingAsync(long chatId, int promptMessageId) =>
            _database().KeyDeleteAsync(PendingKey(chatId, promptMessageId), CommandFlags.None);

        private static RedisKey PendingKey(long chatId, int messageId) => $"chat:{chatId}:warnreasonprompt:{messageId}";
    }
}
