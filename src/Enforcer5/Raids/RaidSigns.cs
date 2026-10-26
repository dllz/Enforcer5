using System;
using System.Collections.Generic;
using System.Linq;

namespace Enforcer5.Raids
{
    /// <summary>How a group wants raids handled. Kept in <c>chat:{id}:raid</c>, field <c>mode</c>.</summary>
    internal enum RaidMode
    {
        /// <summary>The default: tell the admins about a possible raid and do what they choose.</summary>
        Human,
        /// <summary>Act at once and tell the admins what was done.</summary>
        Auto,
        /// <summary>No detection and no link reports; <c>/raid</c> still works.</summary>
        Off
    }

    /// <summary>What raid mode does once it is on.</summary>
    [Flags]
    internal enum RaidActions
    {
        None = 0,
        /// <summary>The group's own permissions set to nothing: only admins can post.</summary>
        Lock = 1,
        /// <summary>Everyone who joins is muted for <see cref="RaidLimits.QuarantineFor"/>.</summary>
        Quarantine = 2,
        /// <summary>Untrusted members who show raid signs are banned, their messages with them.</summary>
        BanSuspects = 4,
        All = Lock | Quarantine | BanSuspects
    }

    /// <summary>The numbers raid detection works with, in one place.</summary>
    internal sealed class RaidLimits
    {
        /// <summary>The window every sign is counted over.</summary>
        internal TimeSpan Window = TimeSpan.FromSeconds(60);
        /// <summary>Joins in the window that make a wave, unless the group's normal is higher.</summary>
        internal int JoinWave = 15;
        /// <summary>A wave is also this many times the group's usual joins a minute.</summary>
        internal double JoinWaveOverNormal = 20;
        /// <summary>Untrusted members posting the same text or image, flooding, or posting bare links: this many is a sign.</summary>
        internal int SameContentUsers = 3;
        internal int Flooders = 3;
        internal int BareLinkUsers = 3;
        internal int Reports = 5;
        /// <summary>Joiners who post within this long of joining, counted as a sign once there are three.</summary>
        internal TimeSpan PostSoonAfterJoin = TimeSpan.FromSeconds(30);
        /// <summary>A score this high is a raid; half of it a possible one.</summary>
        internal int RaidScore = 4;
        internal int PossibleScore = 2;
        /// <summary>Raid mode ends after this long with no joins and no signs.</summary>
        internal TimeSpan QuietEnd = TimeSpan.FromMinutes(10);
        /// <summary>How long a joiner stays muted.</summary>
        internal TimeSpan QuarantineFor = TimeSpan.FromHours(24);
        /// <summary>The raid notice is updated at most this often.</summary>
        internal TimeSpan NoticeEvery = TimeSpan.FromSeconds(30);
        /// <summary>One bare-link report per member per this long.</summary>
        internal TimeSpan LinkReportEvery = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// The signs in one group over the last <see cref="RaidLimits.Window"/>, in memory. Only
    /// untrusted members are ever added as suspects; joins are anyone joining.
    /// </summary>
    internal sealed class RaidSigns
    {
        private readonly Queue<(DateTime At, long User)> _joins = new();
        private readonly Dictionary<long, DateTime> _joinedAt = new();
        private readonly Queue<(DateTime At, string Key, long User)> _content = new();
        private readonly Queue<(DateTime At, long User)> _floods = new();
        private readonly Queue<(DateTime At, long User)> _bareLinks = new();
        private readonly Queue<(DateTime At, long User)> _postedSoon = new();
        private readonly Queue<DateTime> _reports = new();

        internal DateTime LastActivity { get; private set; }

        internal void Joined(long user, DateTime at)
        {
            _joins.Enqueue((at, user));
            _joinedAt[user] = at;
            LastActivity = at;
        }

        /// <summary>When <paramref name="user"/> joined, if within the last day; null otherwise.</summary>
        internal DateTime? JoinedAt(long user, DateTime now) =>
            _joinedAt.TryGetValue(user, out var at) && now - at < TimeSpan.FromDays(1) ? at : null;

        /// <summary>Adds a message's content key; returns everyone who posted it in the window.</summary>
        internal IReadOnlyCollection<long> Posted(string key, long user, DateTime at)
        {
            _content.Enqueue((at, key, user));
            return _content.Where(c => c.Key == key).Select(c => c.User).Distinct().ToList();
        }

        internal void Flooded(long user, DateTime at) => Mark(_floods, user, at);
        internal void BareLink(long user, DateTime at) => Mark(_bareLinks, user, at);
        internal void PostedSoonAfterJoining(long user, DateTime at) => Mark(_postedSoon, user, at);

        internal void Reported(DateTime at)
        {
            _reports.Enqueue(at);
        }

        // Each of these is a sign of untrusted activity, which keeps a raid going.
        private void Mark(Queue<(DateTime, long)> queue, long user, DateTime at)
        {
            queue.Enqueue((at, user));
            LastActivity = at;
        }

        /// <summary>A content key posted by untrusted members counts once there are enough of them.</summary>
        internal void SameContentFromUntrusted(DateTime at) => LastActivity = at;

        internal int JoinCount => _joins.Count;
        internal int FlooderCount => _floods.Select(f => f.User).Distinct().Count();
        internal int BareLinkCount => _bareLinks.Select(f => f.User).Distinct().Count();
        internal int PostedSoonCount => _postedSoon.Select(f => f.User).Distinct().Count();
        internal int ReportCount => _reports.Count;
        internal int SameContentUsers { get; set; }

        internal void Trim(DateTime now, TimeSpan window)
        {
            static void TrimQueue<T>(Queue<T> q, Func<T, DateTime> at, DateTime cut)
            {
                while (q.Count > 0 && at(q.Peek()) <= cut) q.Dequeue();
            }
            var cut = now - window;
            TrimQueue(_joins, j => j.At, cut);
            TrimQueue(_content, c => c.At, cut);
            TrimQueue(_floods, f => f.At, cut);
            TrimQueue(_bareLinks, f => f.At, cut);
            TrimQueue(_postedSoon, f => f.At, cut);
            TrimQueue(_reports, r => r, cut);
            if (_content.Count == 0) SameContentUsers = 0;
            foreach (var old in _joinedAt.Where(p => now - p.Value >= TimeSpan.FromDays(1)).Select(p => p.Key).ToList())
                _joinedAt.Remove(old);
        }

        /// <summary>
        /// The raid score: a wave of joins 2; untrusted members posting the same thing 3; flooding 2;
        /// bare links 2; joiners posting at once 1; a burst of reports 1.
        /// </summary>
        internal (int Score, List<string> Reasons) Score(RaidLimits limits, double? usualJoinsPerHour)
        {
            var reasons = new List<string>();
            var score = 0;
            var wave = limits.JoinWave;
            if (usualJoinsPerHour is { } perHour)
                wave = Math.Max(wave, (int)Math.Ceiling(perHour / 60 * limits.Window.TotalMinutes * limits.JoinWaveOverNormal));
            if (JoinCount >= wave) { score += 2; reasons.Add("joins"); }
            if (SameContentUsers >= limits.SameContentUsers) { score += 3; reasons.Add("same_content"); }
            if (FlooderCount >= limits.Flooders) { score += 2; reasons.Add("flood"); }
            if (BareLinkCount >= limits.BareLinkUsers) { score += 2; reasons.Add("bare_links"); }
            if (PostedSoonCount >= 3) { score += 1; reasons.Add("posted_on_joining"); }
            if (ReportCount >= limits.Reports) { score += 1; reasons.Add("reports"); }
            return (score, reasons);
        }
    }
}
