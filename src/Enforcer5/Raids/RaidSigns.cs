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
        /// <summary>
        /// Untrusted members who show harmful raid signs are muted for <see cref="RaidLimits.QuarantineFor"/>
        /// and the messages that showed them deleted: what auto mode does before it is clearly a raid.
        /// </summary>
        MuteSuspects = 8,
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
        /// <summary>This many untrusted members posting the same harmful content is a raid on its own.</summary>
        internal int SameContentMany = 6;
        /// <summary>Texts this long (in letters) are harmful content when posted alike, even without a link or a file.</summary>
        internal int LongTextLetters = 100;
        /// <summary>Two texts this alike (shared three-letter runs) are the same, so a random ending does not hide spam.</summary>
        internal double SimilarText = 0.7;
        internal int Flooders = 3;
        internal int BareLinkUsers = 3;
        internal int Reports = 5;
        /// <summary>Joiners who post within this long of joining, counted as a sign once there are three.</summary>
        internal TimeSpan PostSoonAfterJoin = TimeSpan.FromSeconds(30);
        /// <summary>A score this high is a raid; half of it a possible one.</summary>
        internal int RaidScore = 4;
        internal int PossibleScore = 2;
        /// <summary>Raid mode ends after this long without signs or a stream of joins.</summary>
        internal TimeSpan QuietEnd = TimeSpan.FromMinutes(10);
        /// <summary>
        /// Joins in the window that keep raid mode going (or three times the group's usual, if higher):
        /// a real member joining now and then does not.
        /// </summary>
        internal int JoinsKeepGoing = 3;
        /// <summary>Someone an admin let off (an ignored link report or alert) is not a suspect again for this long.</summary>
        internal TimeSpan ClearedFor = TimeSpan.FromHours(24);
        /// <summary>How long a joiner stays muted.</summary>
        internal TimeSpan QuarantineFor = TimeSpan.FromHours(24);
        /// <summary>The raid notice is updated at most this often.</summary>
        internal TimeSpan NoticeEvery = TimeSpan.FromSeconds(30);
        /// <summary>One bare-link report per member per this long.</summary>
        internal TimeSpan LinkReportEvery = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// The signs in one group over the last <see cref="RaidLimits.Window"/>, in memory. Only
    /// untrusted members are ever added as suspects; joins are anyone joining who was not let in by
    /// an admin.
    /// </summary>
    internal sealed class RaidSigns
    {
        private readonly Queue<(DateTime At, long User)> _joins = new();
        private readonly Dictionary<long, DateTime> _joinedAt = new();
        private readonly Queue<Posting> _content = new();
        private readonly Queue<(DateTime At, long User)> _floods = new();
        private readonly Queue<(DateTime At, long User)> _bareLinks = new();
        private readonly Queue<(DateTime At, long User)> _postedSoon = new();
        private readonly Queue<DateTime> _reports = new();

        /// <summary>A message that can be compared: its key, the three-letter runs of its text, who posted it.</summary>
        internal sealed record Posting(DateTime At, string Key, HashSet<int> Sketch, long User, int MessageId);

        internal void Joined(long user, DateTime at)
        {
            _joins.Enqueue((at, user));
            _joinedAt[user] = at;
        }

        /// <summary>A join turned out to be let in by an admin (an approved request): not counted after all.</summary>
        internal void Vetted(long user)
        {
            _joinedAt.Remove(user);
            var rest = _joins.Where(j => j.User != user).ToList();
            _joins.Clear();
            foreach (var j in rest) _joins.Enqueue(j);
        }

        /// <summary>When <paramref name="user"/> joined, if within the last day; null otherwise.</summary>
        internal DateTime? JoinedAt(long user, DateTime now) =>
            _joinedAt.TryGetValue(user, out var at) && now - at < TimeSpan.FromDays(1) ? at : null;

        /// <summary>
        /// Adds a message; returns everyone who posted the same or nearly the same in the window, with
        /// their messages, and the key the first of them was seen under (so near copies share one).
        /// </summary>
        internal (string Key, IReadOnlyList<Posting> Same) Posted(string key, HashSet<int> sketch, long user, int messageId, DateTime at,
            double similar)
        {
            var same = _content.Where(c => c.Key == key || Similar(c.Sketch, sketch, similar)).ToList();
            var posting = new Posting(at, key, sketch, user, messageId);
            _content.Enqueue(posting);
            same.Add(posting);
            return (same[0].Key, same);
        }

        /// <summary>True when two sketches share at least <paramref name="threshold"/> of their runs (Jaccard).</summary>
        internal static bool Similar(HashSet<int> a, HashSet<int> b, double threshold)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return false;
            var small = a.Count <= b.Count ? a : b;
            var large = ReferenceEquals(small, a) ? b : a;
            // Too different in length to be the same text.
            if (small.Count < large.Count * threshold) return false;
            var shared = small.Count(large.Contains);
            return shared >= threshold * (a.Count + b.Count - shared);
        }

        internal void Flooded(long user, DateTime at) => _floods.Enqueue((at, user));
        internal void BareLink(long user, DateTime at) => _bareLinks.Enqueue((at, user));
        internal void PostedSoonAfterJoining(long user, DateTime at) => _postedSoon.Enqueue((at, user));
        internal void Reported(DateTime at) => _reports.Enqueue(at);

        internal int JoinCount => _joins.Count;
        internal int FlooderCount => _floods.Select(f => f.User).Distinct().Count();
        internal int BareLinkCount => _bareLinks.Select(f => f.User).Distinct().Count();
        internal int PostedSoonCount => _postedSoon.Select(f => f.User).Distinct().Count();
        internal int ReportCount => _reports.Count;
        /// <summary>The most untrusted members seen posting the same harmful content (a link, a file, a long text).</summary>
        internal int SameContentUsers { get; set; }
        /// <summary>The most untrusted members seen posting the same plain text: a giveaway or a greeting as often as a raid.</summary>
        internal int EchoUsers { get; set; }

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
            if (_content.Count == 0) SameContentUsers = EchoUsers = 0;
            foreach (var old in _joinedAt.Where(p => now - p.Value >= TimeSpan.FromDays(1)).Select(p => p.Key).ToList())
                _joinedAt.Remove(old);
        }

        /// <summary>Signs that mean harm is being done: spam posted alike, floods, bare links. Only these close a group or act on people.</summary>
        internal static readonly string[] HarmfulReasons = { "same_content", "flood", "bare_links" };

        internal static bool Harmful(IEnumerable<string> reasons) => reasons.Any(HarmfulReasons.Contains);

        /// <summary>
        /// The raid score: a wave of joins 2; untrusted members posting the same harmful content 3, and
        /// 1 more when there are many of them; the same plain text 1; flooding 2; bare links 2; joiners
        /// posting at once 1; a burst of reports 1.
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
            if (SameContentUsers >= limits.SameContentMany) { score += 1; reasons.Add("same_content_many"); }
            if (EchoUsers >= limits.SameContentUsers) { score += 1; reasons.Add("echo"); }
            if (FlooderCount >= limits.Flooders) { score += 2; reasons.Add("flood"); }
            if (BareLinkCount >= limits.BareLinkUsers) { score += 2; reasons.Add("bare_links"); }
            if (PostedSoonCount >= 3) { score += 1; reasons.Add("posted_on_joining"); }
            if (ReportCount >= limits.Reports) { score += 1; reasons.Add("reports"); }
            return (score, reasons);
        }

        /// <summary>Joins in the window that keep raid mode going in a group with <paramref name="usualJoinsPerHour"/>.</summary>
        internal bool JoinsKeepItGoing(RaidLimits limits, double? usualJoinsPerHour)
        {
            var needed = limits.JoinsKeepGoing;
            if (usualJoinsPerHour is { } perHour)
                needed = Math.Max(needed, (int)Math.Ceiling(perHour / 60 * limits.Window.TotalMinutes * 3));
            return JoinCount >= needed;
        }
    }
}
