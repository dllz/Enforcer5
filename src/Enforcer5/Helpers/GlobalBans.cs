using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Storage;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Global bans, which run on PostgreSQL (rollout step 7). Every message checks its sender (or
    /// the member joining) against them, so the active bans are kept in memory: loaded once
    /// PostgreSQL is ready, updated straight away by this process's own bans and unbans, and reloaded
    /// every minute so a change made by the other edition arrives within a minute.
    /// <para>
    /// Until the first load succeeds nothing is enforced: absence means allowed. A failed reload
    /// keeps the last set and is reported.
    /// </para>
    /// </summary>
    internal static class GlobalBans
    {
        internal static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(1);

        private static readonly object Gate = new object();
        private static volatile IReadOnlyDictionary<long, GlobalBanRecord> _active =
            new Dictionary<long, GlobalBanRecord>();
        private static volatile bool _loaded;
        // Bumped by every local change, so a reload that read the table before the change does not
        // put back what the change replaced.
        private static long _version;
        private static int _started;

        /// <summary>True once the active bans have been loaded from PostgreSQL.</summary>
        internal static bool Loaded => _loaded;

        /// <summary>The active global ban on <paramref name="userId"/>, or null.</summary>
        internal static GlobalBanRecord Get(long userId) =>
            _active.TryGetValue(userId, out var ban) ? ban : null;

        /// <summary>
        /// Loads the active bans and keeps reloading them every minute, in the background. Never
        /// throws and never holds up startup.
        /// </summary>
        internal static void Start(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            if (string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection")))
            {
                ErrorReporter.Report("PostgresConnection is not set: global bans live in PostgreSQL, so none are enforced");
                return;
            }

            // Its own task, not Bot.Dispatch: it never finishes, and Dispatch counts work in flight.
            Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (PostgresStorage.Ready)
                    {
                        try
                        {
                            var first = !_loaded;
                            await ReloadAsync();
                            if (first && _loaded)
                                LogHelper.Info($"Global bans loaded: {_active.Count} active");
                        }
                        catch (Exception e)
                        {
                            ErrorReporter.Report(e, _loaded
                                ? "Reloading global bans; the last loaded set stays in use"
                                : "Loading global bans; none are enforced until this succeeds");
                        }
                    }
                    try { await Task.Delay(_loaded ? ReloadInterval : TimeSpan.FromSeconds(5), cancellationToken); }
                    catch (OperationCanceledException) { return; } // shutting down
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Replaces the in-memory set with what PostgreSQL holds, unless a ban or unban was made
        /// here while it was read (the next reload picks that up). Throws if the read fails.
        /// </summary>
        internal static async Task ReloadAsync()
        {
            var version = Interlocked.Read(ref _version);
            var rows = await Repositories.GlobalBans.GetActiveAsync();
            lock (Gate)
            {
                if (_version != version) return;
                _active = rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.First());
                _loaded = true;
            }
        }

        /// <summary>Bans <paramref name="userId"/> everywhere. Throws if PostgreSQL is not ready or the write fails.</summary>
        internal static async Task<GlobalBanRecord> BanAsync(long userId, string reason, long actorId)
        {
            var ban = await Repositories.GlobalBans.BanAsync(userId, reason, actorId, DateTimeOffset.UtcNow);
            Change(active => active[userId] = ban);
            return ban;
        }

        /// <summary>Lifts the ban on <paramref name="userId"/>. False when there was none. Throws as <see cref="BanAsync"/>.</summary>
        internal static async Task<bool> UnbanAsync(long userId, string reason)
        {
            var lifted = await Repositories.GlobalBans.UnbanAsync(userId, reason, DateTimeOffset.UtcNow);
            Change(active => active.Remove(userId));
            return lifted;
        }

        /// <summary>Records that the support chat was told about <paramref name="userId"/>'s ban.</summary>
        internal static async Task MarkNotifiedAsync(long userId)
        {
            var at = DateTimeOffset.UtcNow;
            await Repositories.GlobalBans.MarkNotifiedAsync(userId, at);
            Change(active =>
            {
                if (active.TryGetValue(userId, out var ban))
                    active[userId] = ban with { NotifiedAt = at };
            });
        }

        private static void Change(Action<Dictionary<long, GlobalBanRecord>> change)
        {
            lock (Gate)
            {
                var copy = new Dictionary<long, GlobalBanRecord>(_active);
                change(copy);
                _active = copy;
                _version++;
            }
        }

        /// <summary>Forgets everything loaded, for tests.</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _active = new Dictionary<long, GlobalBanRecord>();
                _loaded = false;
                _version++;
            }
        }
    }
}
