using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Members an admin set back to new with /untrust (build step 7b): new whatever their counts, until an
    /// admin restores them. PostgreSQL holds them (<c>member_trust_overrides</c>); memory answers
    /// <see cref="MemberTrust"/>, which runs on every message and in raids, without a lookup. Reloaded every
    /// <see cref="ReloadEvery"/>, which is how the other edition's changes arrive.
    /// </summary>
    internal static class TrustOverrides
    {
        internal static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(5);

        private static volatile ConcurrentDictionary<(long Chat, long User), TrustOverride> _all = new();

        internal static bool IsSet(long chatId, long userId) => _all.ContainsKey((chatId, userId));

        internal static TrustOverride Get(long chatId, long userId) => _all.TryGetValue((chatId, userId), out var o) ? o : null;

        internal static int CountIn(long chatId) => _all.Keys.Count(k => k.Chat == chatId);

        internal static System.Collections.Generic.IReadOnlyList<TrustOverride> ForChat(long chatId) =>
            _all.Values.Where(o => o.ChatId == chatId).ToList();

        /// <summary>The admins were asked whether to restore them.</summary>
        internal static void Prompted(long chatId, long userId, DateTimeOffset at)
        {
            if (_all.TryGetValue((chatId, userId), out var o)) _all[(chatId, userId)] = o with { RestorePromptedAt = at };
        }

        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await ReloadAsync();
                    await Clock.WaitAsync(ReloadEvery, ct);
                }
            }, stop, "TrustOverrides");

        /// <summary>Reads them all again; keeps what memory has when PostgreSQL does not answer.</summary>
        internal static async Task ReloadAsync()
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                var all = await Repositories.DetectionLearning.AllOverridesAsync();
                _all = new ConcurrentDictionary<(long, long), TrustOverride>(all.ToDictionary(o => (o.ChatId, o.UserId)));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Loading the members set back to new");
            }
        }

        /// <summary>Sets them back to new. Null when it could not be saved; false when they already were.</summary>
        internal static async Task<bool?> SetAsync(long chatId, long userId, long by)
        {
            if (!PostgresStorage.Ready) return null;
            try
            {
                var (messages, _) = await MemberTrust.CountsAsync(chatId, userId);
                var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
                var added = await Repositories.DetectionLearning.SetOverrideAsync(chatId, userId, by, messages, now);
                _all.TryAdd((chatId, userId), new TrustOverride(chatId, userId, by, now, messages, null));
                return added;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Setting {userId} back to new in {chatId}");
                return null;
            }
        }

        /// <summary>Restores them. Null when it could not be saved; false when they were not set back to new.</summary>
        internal static async Task<bool?> RemoveAsync(long chatId, long userId)
        {
            if (!PostgresStorage.Ready) return null;
            try
            {
                var removed = await Repositories.DetectionLearning.RemoveOverrideAsync(chatId, userId);
                _all.TryRemove((chatId, userId), out _);
                return removed;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Restoring {userId} in {chatId}");
                return null;
            }
        }

        internal static void ResetForTests() => _all = new ConcurrentDictionary<(long, long), TrustOverride>();
    }
}
