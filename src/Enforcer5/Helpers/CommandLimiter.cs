using System;
using System.Collections.Generic;
using System.Linq;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Limits how fast commands, buttons and @admin reports are accepted: normal use is instant,
    /// flooding is not. Ported from blackwolf's CommandRateLimiter (Werewolf Control/Helpers), with
    /// the spam ban replaced by the slow lane. All numbers live here so they can be tuned in one place.
    /// </summary>
    internal sealed class CommandLimits
    {
        // Per user, every command costs one token whatever it is. A full bucket covers a quick
        // handful of commands; then one every UserRefill.
        internal double UserBurst = 4;
        internal TimeSpan UserRefill = TimeSpan.FromSeconds(3);

        /// <summary>Each time a user empties their bucket the cooldown takes the next step (the last one repeats).</summary>
        internal TimeSpan[] UserCooldowns = { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2) };

        /// <summary>The user's ladder starts over after this long without a new cooldown.</summary>
        internal TimeSpan UserQuietReset = TimeSpan.FromMinutes(5);

        // Per group, around Telegram's 20 messages a minute per group: when many people flood
        // commands at once the group pauses commands for everyone but its admins.
        internal double GroupBurst = 20;
        internal TimeSpan GroupRefill = TimeSpan.FromSeconds(3);

        internal TimeSpan[] GroupCooldowns =
        {
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)
        };

        internal TimeSpan GroupQuietReset = TimeSpan.FromHours(1);

        /// <summary>A group pause at least this long is reported to the error chat.</summary>
        internal TimeSpan OwnerAlertFrom = TimeSpan.FromMinutes(30);

        /// <summary>Buttons are cheaper than commands: menus take several presses.</summary>
        internal double ButtonCost = 0.5;

        /// <summary>
        /// Commands sent while throttled, within <see cref="SlowLaneWindow"/>, that put someone in the
        /// slow lane. Halved for each strike in the last <see cref="StrikeMemory"/>, down to
        /// <see cref="SlowLaneMinAttempts"/>.
        /// </summary>
        internal int SlowLaneAttempts = 15;
        internal int SlowLaneMinAttempts = 4;
        internal TimeSpan SlowLaneWindow = TimeSpan.FromSeconds(60);

        /// <summary>In the slow lane, one command, button or report is answered this often.</summary>
        internal TimeSpan SlowLaneEvery = TimeSpan.FromMinutes(5);

        /// <summary>The slow lane ends after this long without a single attempt.</summary>
        internal TimeSpan SlowLaneQuiet = TimeSpan.FromMinutes(15);

        /// <summary>How long a stay in the slow lane counts against someone (as a strike).</summary>
        internal TimeSpan StrikeMemory = TimeSpan.FromHours(24);

        internal static CommandLimits ForEdition()
        {
            var limits = new CommandLimits();
#if PREMIUM
            // Premium groups paid for more room, as sponsored groups have in blackwolf.
            limits.UserBurst = 6;
            limits.UserRefill = TimeSpan.FromSeconds(2);
            limits.GroupBurst = 30;
            limits.GroupRefill = TimeSpan.FromSeconds(2);
#endif
            return limits;
        }
    }

    /// <summary>Someone's stay in the slow lane, as kept in Redis so a restart (and the other edition) keeps it.</summary>
    internal sealed class SlowLaneState
    {
        /// <summary>The slow lane ends at this time; every attempt moves it on.</summary>
        public DateTime QuietUntilUtc { get; set; }
        /// <summary>The next command is answered from this time on.</summary>
        public DateTime NextAnswerUtc { get; set; }
        /// <summary>Stays in the slow lane within <see cref="CommandLimits.StrikeMemory"/>, this one included.</summary>
        public int Strikes { get; set; }
        public DateTime StrikesSinceUtc { get; set; }

        internal bool Active(DateTime now) => QuietUntilUtc > now;
    }

    internal enum ThrottleScope
    {
        None,
        /// <summary>This user sent too many: their own cooldown.</summary>
        User,
        /// <summary>Too many people at once in this group: paused for everyone but its admins.</summary>
        Group,
        /// <summary>Kept going while throttled: one answer every few minutes.</summary>
        SlowLane
    }

    internal readonly struct CommandDecision
    {
        internal bool Allowed { get; init; }
        internal ThrottleScope Scope { get; init; }
        /// <summary>How long until the scope that blocked this command accepts commands again.</summary>
        internal TimeSpan RetryIn { get; init; }
        /// <summary>The block just started: tell them, once. Every later attempt is dropped silently.</summary>
        internal bool Notify { get; init; }
        /// <summary>A group pause long enough to tell the error chat about.</summary>
        internal bool AlertOwner { get; init; }
        /// <summary>For an allowed command: answered as the one a slow-lane user gets every few minutes.</summary>
        internal bool FromSlowLane { get; init; }
        /// <summary>The slow lane state to keep, when it changed.</summary>
        internal SlowLaneState SlowLane { get; init; }

        internal static CommandDecision Allow => new CommandDecision { Allowed = true };
    }

    /// <summary>
    /// The limiter. Everyone gets a bucket of commands that refills over time; so does every group.
    /// <list type="bullet">
    /// <item>Empty your bucket: a short cooldown, told once, growing 10s, 30s, 2m if you keep doing it.</item>
    /// <item>Keep going while in a cooldown (15 tries in a minute): the slow lane. You are told once;
    /// then one command, button or @admin report of yours is answered every 5 minutes, the rest are
    /// dropped without a single Telegram call. 15 minutes without trying and you are back to normal.
    /// Command spam never bans anyone: a legitimate user who stops is out within minutes. Someone who
    /// lands there again within a day gets there sooner, never for longer.</item>
    /// <item>Too many people at once in a group: commands pause there for everyone but its admins.</item>
    /// <item>Admins are slowed like anyone when they waste calls, but never put in the slow lane, and a
    /// command that carried out real enforcement gives its token back (<see cref="Refund"/>), so an
    /// admin banning fifty raiders is never slowed.</item>
    /// </list>
    /// Buckets live in memory per process; slow lane stays are also kept in Redis by the caller.
    /// </summary>
    internal sealed class CommandLimiter
    {
        private sealed class Bucket
        {
            public double Tokens;
            public DateTime Updated;
            public DateTime LastSeen;
            public DateTime CooldownUntil;
            public int Level;
            public readonly Queue<DateTime> ThrottledAttempts = new();
        }

        private readonly object _lock = new();
        private readonly CommandLimits _limits;
        private readonly Func<DateTime> _now;
        private readonly Dictionary<long, Bucket> _users = new();
        private readonly Dictionary<long, Bucket> _groups = new();
        private readonly Dictionary<long, SlowLaneState> _slowLane = new();

        internal CommandLimiter(CommandLimits limits = null, Func<DateTime> now = null)
        {
            _limits = limits ?? CommandLimits.ForEdition();
            _now = now ?? (() => Clock.UtcNow);
        }

        internal CommandLimits Limits => _limits;

        /// <summary>
        /// Decides on one command (cost 1) or button press (<see cref="CommandLimits.ButtonCost"/>) from
        /// <paramref name="userId"/> in <paramref name="groupId"/> (0 for a private chat).
        /// <paramref name="isAdmin"/> is only asked when it matters, since it may cost a lookup.
        /// <paramref name="stored"/> is the slow lane state from Redis, for someone not seen in this
        /// process yet; null when there is none or it was already loaded.
        /// </summary>
        internal CommandDecision Check(long userId, long groupId, double cost, Func<bool> isAdmin, SlowLaneState stored = null)
        {
            lock (_lock)
            {
                var now = _now();
                if (stored != null && !_slowLane.ContainsKey(userId)) _slowLane[userId] = stored;
                var user = Touch(_users, userId, now, _limits.UserBurst, _limits.UserRefill, _limits.UserQuietReset);

                // The slow lane: one answer every few minutes, every attempt keeps it going.
                if (_slowLane.TryGetValue(userId, out var lane) && lane.Active(now))
                {
                    if (isAdmin())
                    {
                        _slowLane.Remove(userId);
                    }
                    else
                    {
                        lane.QuietUntilUtc = now + _limits.SlowLaneQuiet;
                        if (now >= lane.NextAnswerUtc)
                        {
                            lane.NextAnswerUtc = now + _limits.SlowLaneEvery;
                            return new CommandDecision { Allowed = true, FromSlowLane = true, SlowLane = lane };
                        }
                        return new CommandDecision { Scope = ThrottleScope.SlowLane, RetryIn = lane.NextAnswerUtc - now, SlowLane = lane };
                    }
                }

                var group = groupId != 0
                    ? Touch(_groups, groupId, now, _limits.GroupBurst, _limits.GroupRefill, _limits.GroupQuietReset)
                    : null;

                if (group != null && now < group.CooldownUntil && !isAdmin())
                    return Throttled(user, userId, ThrottleScope.Group, group.CooldownUntil - now, now, isAdmin);
                if (now < user.CooldownUntil)
                    return Throttled(user, userId, ThrottleScope.User, user.CooldownUntil - now, now, isAdmin);

                if (user.Tokens < cost)
                {
                    var wait = StartCooldown(user, now, _limits.UserCooldowns);
                    return new CommandDecision { Scope = ThrottleScope.User, RetryIn = wait, Notify = true };
                }
                if (group != null && group.Tokens < cost)
                {
                    if (!isAdmin())
                    {
                        var wait = StartCooldown(group, now, _limits.GroupCooldowns);
                        return new CommandDecision
                        {
                            Scope = ThrottleScope.Group, RetryIn = wait, Notify = true, AlertOwner = wait >= _limits.OwnerAlertFrom
                        };
                    }
                    // An admin's command does not wait on the group, and does not drain it either.
                    group = null;
                }

                user.Tokens -= cost;
                if (group != null) group.Tokens -= cost;
                return CommandDecision.Allow;
            }
        }

        // A command sent while throttled: dropped, and counted towards the slow lane.
        private CommandDecision Throttled(Bucket user, long userId, ThrottleScope scope, TimeSpan retryIn, DateTime now, Func<bool> isAdmin)
        {
            user.ThrottledAttempts.Enqueue(now);
            while (user.ThrottledAttempts.Count > 0 && now - user.ThrottledAttempts.Peek() > _limits.SlowLaneWindow)
                user.ThrottledAttempts.Dequeue();

            _slowLane.TryGetValue(userId, out var previous);
            var strikes = previous != null && now - previous.StrikesSinceUtc < _limits.StrikeMemory ? previous.Strikes : 0;
            var needed = Math.Max(_limits.SlowLaneMinAttempts, _limits.SlowLaneAttempts >> strikes);
            if (user.ThrottledAttempts.Count < needed || isAdmin())
                return new CommandDecision { Scope = scope, RetryIn = retryIn };

            user.ThrottledAttempts.Clear();
            var lane = new SlowLaneState
            {
                QuietUntilUtc = now + _limits.SlowLaneQuiet,
                NextAnswerUtc = now + _limits.SlowLaneEvery,
                Strikes = strikes + 1,
                StrikesSinceUtc = strikes == 0 ? now : previous.StrikesSinceUtc
            };
            _slowLane[userId] = lane;
            return new CommandDecision { Scope = ThrottleScope.SlowLane, RetryIn = _limits.SlowLaneEvery, Notify = true, SlowLane = lane };
        }

        /// <summary>
        /// Gives back what a command cost: it carried out real enforcement (a ban, kick or mute
        /// Telegram took), which is what the bot is for, not a wasted call.
        /// </summary>
        internal void Refund(long userId, long groupId, double cost)
        {
            lock (_lock)
            {
                if (_users.TryGetValue(userId, out var user)) user.Tokens = Math.Min(_limits.UserBurst, user.Tokens + cost);
                if (groupId != 0 && _groups.TryGetValue(groupId, out var group))
                    group.Tokens = Math.Min(_limits.GroupBurst, group.Tokens + cost);
            }
        }

        /// <summary>Takes back a <see cref="Refund"/>: the enforcement was refused after all.</summary>
        internal void Charge(long userId, long groupId, double cost)
        {
            lock (_lock)
            {
                if (_users.TryGetValue(userId, out var user)) user.Tokens -= cost;
                if (groupId != 0 && _groups.TryGetValue(groupId, out var group)) group.Tokens -= cost;
            }
        }

        /// <summary>True while <paramref name="userId"/> is in the slow lane; null when this process does not know.</summary>
        internal bool? InSlowLane(long userId)
        {
            lock (_lock)
                return _slowLane.TryGetValue(userId, out var lane) ? lane.Active(_now()) : null;
        }

        /// <summary>Forgets idle buckets and ended slow lane stays. Called now and then.</summary>
        internal void Prune()
        {
            lock (_lock)
            {
                var now = _now();
                Prune(_users, now, _limits.UserQuietReset);
                Prune(_groups, now, _limits.GroupQuietReset);
                foreach (var id in _slowLane.Where(p => !p.Value.Active(now) && now - p.Value.StrikesSinceUtc >= _limits.StrikeMemory)
                             .Select(p => p.Key).ToList())
                    _slowLane.Remove(id);
            }
        }

        /// <summary>Language-neutral "m:ss" or "h:mm:ss".</summary>
        internal static string FormatWait(TimeSpan wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        private static Bucket Touch(Dictionary<long, Bucket> map, long id, DateTime now, double burst, TimeSpan refill, TimeSpan quietReset)
        {
            if (!map.TryGetValue(id, out var b))
            {
                b = new Bucket { Tokens = burst, Updated = now };
                map[id] = b;
            }
            else
            {
                var elapsed = (now - b.Updated).TotalSeconds;
                if (elapsed > 0) b.Tokens = Math.Min(burst, b.Tokens + elapsed / refill.TotalSeconds);
                b.Updated = now;
            }
            if (b.Level > 0 && now >= b.CooldownUntil && now - b.CooldownUntil >= quietReset) b.Level = 0;
            b.LastSeen = now;
            return b;
        }

        private static TimeSpan StartCooldown(Bucket b, DateTime now, TimeSpan[] ladder)
        {
            var d = ladder[Math.Min(b.Level, ladder.Length - 1)];
            b.Level = Math.Min(b.Level + 1, ladder.Length);
            b.CooldownUntil = now + d;
            return d;
        }

        private static void Prune(Dictionary<long, Bucket> map, DateTime now, TimeSpan quietReset)
        {
            foreach (var id in map.Where(x => now >= x.Value.CooldownUntil && now - x.Value.LastSeen >= quietReset &&
                                              (x.Value.Level == 0 || now - x.Value.CooldownUntil >= quietReset))
                         .Select(x => x.Key).ToList())
                map.Remove(id);
        }
    }
}
