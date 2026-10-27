using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using StackExchange.Redis;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Each group's <see cref="DetectionPolicy"/>, for the checks on every message and for the menus
    /// that change it (Detection v2, step 2).
    /// <para>
    /// PostgreSQL is where the settings are kept. Redis only caches them
    /// (<c>detect:policy:{chat}</c> with <c>detect:policy:{chat}:version</c>, for
    /// <see cref="CacheFor"/>), so a restart, or a minute without PostgreSQL, does not leave the
    /// checks without settings. Memory holds the policy of each group in use; it is compared with
    /// the cached version at most every <see cref="RecheckAfter"/> (one small read), which is how a
    /// change made by the other edition arrives, and read again from PostgreSQL every
    /// <see cref="ReloadAfter"/> in case a cache write was lost.
    /// </para>
    /// <para>
    /// Handling a message waits on PostgreSQL only for a group in neither memory nor the cache, and
    /// at most <see cref="ColdWait"/>: the group is loaded, or imported on its first use. Past that,
    /// or with PostgreSQL down, it is served what its old Redis hashes say
    /// (<see cref="LegacySettings"/>) until the load finishes in the background. Changes go to
    /// PostgreSQL first and fail when it is not ready; nothing is written to the old hashes any more.
    /// </para>
    /// </summary>
    internal static class DetectionSettings
    {
        internal static TimeSpan RecheckAfter = TimeSpan.FromSeconds(10);
        internal static TimeSpan ReloadAfter = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
        /// <summary>How long a message waits for PostgreSQL when a group is in neither memory nor the cache.</summary>
        internal static TimeSpan ColdWait = TimeSpan.FromSeconds(2);
        private const int MaxGroups = 50_000;
        private const int SaveAttempts = 5;

        internal static IDetectionSettingsRepository Repository { get; set; } =
            new PostgresDetectionSettingsRepository(() => PostgresStorage.Database);

        private sealed class Entry
        {
            internal DetectionPolicy Policy;
            /// <summary>From the old hashes, until PostgreSQL has answered.</summary>
            internal bool Provisional;
            internal DateTime CheckedAt;
            internal DateTime LoadedAt;
        }

        private static readonly ConcurrentDictionary<long, Entry> Entries = new();
        private static readonly ConcurrentDictionary<long, Task<DetectionPolicy>> Loading = new();

        internal static string CacheKey(long chatId) => $"detect:policy:{chatId}";
        internal static string VersionKey(long chatId) => $"detect:policy:{chatId}:version";

        /// <summary>The group's settings for the message being handled. Throws only when Redis fails too.</summary>
        internal static async Task<DetectionPolicy> GetAsync(long chatId)
        {
            var now = Clock.UtcNow;
            if (Entries.TryGetValue(chatId, out var entry))
            {
                if (now - entry.CheckedAt < RecheckAfter) return entry.Policy;
                entry.CheckedAt = now;
                if (entry.Provisional || now - entry.LoadedAt >= ReloadAfter)
                {
                    _ = StartLoad(chatId); // in the background; this message keeps what is in memory
                    return entry.Policy;
                }
                try
                {
                    var cached = await Redis.db.StringGetAsync(VersionKey(chatId));
                    if (cached.TryParse(out long version) && version > entry.Policy.Version)
                        await ReadCacheAsync(chatId);
                }
                catch (Exception e)
                {
                    // The policy in memory is still good; the next check tries again.
                    ErrorReporter.Report(e, $"Checking the cached detection settings of {chatId}");
                }
                return Entries.TryGetValue(chatId, out entry) ? entry.Policy : await GetAsync(chatId);
            }

            var fromCache = await ReadCacheAsync(chatId);
            if (fromCache != null) return fromCache;

            // Neither in memory nor cached: PostgreSQL, if it answers within ColdWait (a read, or
            // the import on a group's first use); otherwise what the old hashes say until it does.
            var load = StartLoad(chatId);
            if (load != null)
            {
                using var cancel = new CancellationTokenSource();
                await Task.WhenAny(load, Clock.WaitAsync(ColdWait, cancel.Token));
                cancel.Cancel();
                if (load.IsCompletedSuccessfully && load.Result != null) return load.Result;
            }
            var legacy = LegacySettings.Map(await LegacySettings.ReadAsync(Redis.db, chatId));
            Remember(chatId, legacy, provisional: true);
            return Entries.TryGetValue(chatId, out entry) ? entry.Policy : legacy;
        }

        /// <summary>For code that cannot await (menus, commands).</summary>
        internal static DetectionPolicy For(long chatId) => GetAsync(chatId).GetAwaiter().GetResult();

        /// <summary>
        /// Applies <paramref name="change"/> to the group's settings in PostgreSQL and returns the
        /// result. Throws <see cref="DetectionSettingsUnavailableException"/> when PostgreSQL is not
        /// ready, and whatever the write threw when it fails.
        /// </summary>
        internal static async Task<DetectionPolicy> UpdateAsync(long chatId, Func<DetectionPolicy, DetectionPolicy> change)
        {
            if (!PostgresStorage.Ready) throw new DetectionSettingsUnavailableException();
            for (var attempt = 0; attempt < SaveAttempts; attempt++)
            {
                var current = await LoadOrImportAsync(chatId);
                var next = change(current) with { Version = current.Version };
                if (next.SameSettingsAs(current))
                {
                    await StoreAsync(chatId, current);
                    return current;
                }
                var saved = await Repository.SaveAsync(chatId, next, current.Version);
                if (saved == null) continue; // changed by someone else in the meantime: again, on theirs
                await StoreAsync(chatId, saved);
                return saved;
            }
            throw new InvalidOperationException($"The detection settings of {chatId} kept changing while being saved");
        }

        /// <summary>As <see cref="UpdateAsync"/>, for code that cannot await.</summary>
        internal static DetectionPolicy Update(long chatId, Func<DetectionPolicy, DetectionPolicy> change) =>
            UpdateAsync(chatId, change).GetAwaiter().GetResult();

        /// <summary>
        /// As <see cref="Update"/>, but a failure is reported and returns null, for menus and
        /// commands that tell the admin it did not work.
        /// </summary>
        internal static DetectionPolicy TryUpdate(long chatId, Func<DetectionPolicy, DetectionPolicy> change)
        {
            try
            {
                return Update(chatId, change);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Changing the detection settings of {chatId}");
                return null;
            }
        }

        private static async Task<DetectionPolicy> LoadOrImportAsync(long chatId)
        {
            var stored = await Repository.LoadAsync(chatId);
            if (stored != null) return stored;
            var legacy = LegacySettings.Map(await LegacySettings.ReadAsync(Redis.db, chatId));
            return await Repository.ImportAsync(chatId, legacy);
        }

        /// <summary>
        /// Loads (or imports) the group's settings from PostgreSQL in the background, one load per
        /// group at a time. The task gives the loaded settings, or null when it failed (reported);
        /// null itself when PostgreSQL is not ready.
        /// </summary>
        private static Task<DetectionPolicy> StartLoad(long chatId)
        {
            if (!PostgresStorage.Ready) return null; // still on the cache or the old hashes; tried again later
            var done = new TaskCompletionSource<DetectionPolicy>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = Loading.GetOrAdd(chatId, done.Task);
            if (running != done.Task) return running;
            Bot.Dispatch(async () =>
            {
                DetectionPolicy loaded = null;
                try
                {
                    loaded = await LoadOrImportAsync(chatId);
                    await StoreAsync(chatId, loaded);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Loading the detection settings of {chatId}; the cached ones stay in use");
                }
                finally
                {
                    Loading.TryRemove(chatId, out _);
                    done.TrySetResult(loaded);
                }
            }, "DetectionSettings.Load");
            return done.Task;
        }

        private static async Task<DetectionPolicy> ReadCacheAsync(long chatId)
        {
            var json = await Redis.db.StringGetAsync(CacheKey(chatId));
            var policy = json.HasValue ? DetectionPolicyJson.Deserialize(json.ToString()) : null;
            if (policy == null) return null;
            Remember(chatId, policy, provisional: false);
            return Entries.TryGetValue(chatId, out var entry) ? entry.Policy : policy;
        }

        /// <summary>Keeps <paramref name="policy"/> in memory and in the cache.</summary>
        private static async Task StoreAsync(long chatId, DetectionPolicy policy)
        {
            if (!Remember(chatId, policy, provisional: false)) return;
            try
            {
                var db = Redis.db;
                // The policy first: a reader that sees the new version finds the new policy.
                await db.StringSetAsync(CacheKey(chatId), DetectionPolicyJson.Serialize(policy), CacheFor);
                await db.StringSetAsync(VersionKey(chatId), policy.Version, CacheFor);
            }
            catch (Exception e)
            {
                // PostgreSQL has it; the other edition picks it up on its next reload.
                ErrorReporter.Report(e, $"Caching the detection settings of {chatId}");
            }
        }

        /// <summary>False when memory already holds a newer version from PostgreSQL.</summary>
        private static bool Remember(long chatId, DetectionPolicy policy, bool provisional)
        {
            var now = Clock.UtcNow;
            if (Entries.Count > MaxGroups) Entries.Clear();
            var kept = true;
            Entries.AddOrUpdate(chatId,
                _ => new Entry { Policy = policy, Provisional = provisional, CheckedAt = now, LoadedAt = now },
                (_, current) =>
                {
                    if (!current.Provisional && (provisional || current.Policy.Version > policy.Version))
                    {
                        kept = false;
                        return current;
                    }
                    return new Entry { Policy = policy, Provisional = provisional, CheckedAt = now, LoadedAt = now };
                });
            return kept;
        }

        internal static void ResetForTests()
        {
            Entries.Clear();
            Loading.Clear();
        }
    }

    /// <summary>PostgreSQL is not ready, so settings cannot be changed.</summary>
    internal sealed class DetectionSettingsUnavailableException : InvalidOperationException
    {
        internal DetectionSettingsUnavailableException() : base("PostgreSQL is not ready; settings cannot be changed.") { }
    }
}
