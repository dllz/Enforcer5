using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Newtonsoft.Json;

namespace Enforcer5.Raids
{
    /// <summary>
    /// Records every raid (and every possible raid the admins were asked about) as a profile in
    /// PostgreSQL: the summary in <c>raid_incidents</c>, the timeline in <c>raid_events</c>. A profile
    /// can be exported with /raidprofile and replayed by the test harness, so detection can be tuned
    /// on real raids.
    /// <para>
    /// No personal data: people are a hash of their account id that only means something within the
    /// incident (<see cref="Actor"/>), invite links and message content are hashed the same way, and
    /// no text is kept. The joins of the minute before a raid started are included (at negative
    /// offsets), since they are what set it off.
    /// </para>
    /// <para>
    /// Buffered in memory and written with the minute's principal flush. Off without PostgreSQL;
    /// at most <see cref="MaxEvents"/> events per incident.
    /// </para>
    /// </summary>
    internal static class RaidTelemetry
    {
        internal const int MaxEvents = 20_000;

#if PREMIUM
        private const string Edition = "premium";
#else
        private const string Edition = "normal";
#endif

        private sealed class Open
        {
            public string Key;
            public DateTime Start;
            public int Events;
            public DateTime? ActivatedAt;
            public DateTime? LockedAt;
        }

        private static readonly object Lock = new();
        private static readonly Dictionary<long, Queue<JoinEvent>> RecentJoins = new();
        private static readonly Dictionary<long, Open> Incidents = new();
        private static Dictionary<string, RaidIncidentRow> _rows = new();
        private static List<RaidEventRow> _events = new();
        private static int _open;

        /// <summary>True when profiles are recorded. Settable so tests can turn it on.</summary>
        internal static Func<bool> Enabled { get; set; } = () => PostgresStorage.Ready;

        /// <summary>Someone in a raid, as a short hash that only means something within that incident.</summary>
        internal static string Actor(string incidentKey, long id) => Short(incidentKey + ":" + id);

        private static string Short(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 5).ToLowerInvariant();

        private static DateTime Now => Clock.UtcNow;

        // With the lock held.
        private static void Event(long chatId, string kind, long? actor, object detail, DateTime? at = null)
        {
            if (!Incidents.TryGetValue(chatId, out var open) || open.Events >= MaxEvents) return;
            open.Events++;
            var offset = (long)((at ?? Now) - open.Start).TotalMilliseconds;
            _events.Add(new RaidEventRow(open.Key, offset, kind, actor.HasValue ? Actor(open.Key, actor.Value) : null,
                detail == null ? null : JsonConvert.SerializeObject(detail)));
        }

        internal static void Joined(JoinEvent join)
        {
            if (!Enabled()) return;
            lock (Lock)
            {
                if (!RecentJoins.TryGetValue(join.ChatId, out var q)) RecentJoins[join.ChatId] = q = new Queue<JoinEvent>();
                q.Enqueue(join);
                while (q.Count > 0 && (q.Count > 200 || Now - q.Peek().At.UtcDateTime > TimeSpan.FromMinutes(1))) q.Dequeue();
                if (Incidents.TryGetValue(join.ChatId, out var open))
                    Event(join.ChatId, "join", join.UserId, JoinDetail(open.Key, join), join.At.UtcDateTime);
            }
        }

        private static object JoinDetail(string key, JoinEvent join) => new
        {
            source = join.Source,
            link = join.InviteLink == null ? null : Short(key + ":" + join.InviteLink),
            added_by = join.AddedBy.HasValue ? Actor(key, join.AddedBy.Value) : null
        };

        /// <summary>A message in a group with an incident on, by its fingerprint.</summary>
        /// <summary>
        /// A message in a group, by its fingerprint. Kept when an incident is on there; a message
        /// that looked like a sign (<paramref name="untrusted"/> known) is also kept for a minute in
        /// case it sets one off.
        /// </summary>
        internal static void Message(long chatId, long userId, MessageFingerprint print, bool? untrusted)
        {
            // Every group message comes here: no lock unless an incident is on somewhere, or this
            // one looked like a sign.
            if (System.Threading.Volatile.Read(ref _open) == 0 && untrusted == null || !Enabled()) return;
            var now = Now;
            lock (Lock)
            {
                if (!Incidents.TryGetValue(chatId, out var open))
                {
                    if (!RecentMessages.TryGetValue(chatId, out var q)) RecentMessages[chatId] = q = new Queue<(DateTime, long, MessageFingerprint, bool?)>();
                    q.Enqueue((now, userId, print, untrusted));
                    while (q.Count > 0 && (q.Count > 200 || now - q.Peek().At > TimeSpan.FromMinutes(1))) q.Dequeue();
                    return;
                }
                Event(chatId, "message", userId, MessageDetail(open.Key, print, untrusted));
            }
        }

        private static object MessageDetail(string key, MessageFingerprint print, bool? untrusted)
        {
            var content = print.TextHash ?? print.MediaUniqueId;
            return new
            {
                type = print.ContentType,
                content = content == null ? null : Short(key + ":" + content),
                bare_link = print.BareLink,
                links = print.LinkCount,
                script = print.Script,
                untrusted
            };
        }

        private static readonly Dictionary<long, Queue<(DateTime At, long User, MessageFingerprint Print, bool? Untrusted)>> RecentMessages = new();

        internal static void Signal(long chatId, int score, IReadOnlyList<string> reasons)
        {
            if (!Enabled()) return;
            lock (Lock) Event(chatId, "signal", null, new { score, reasons });
        }

        internal static void Started(RaidIncident incident)
        {
            if (!Enabled()) return;
            lock (Lock)
            {
                if (Incidents.ContainsKey(incident.ChatId)) return;
                Incidents[incident.ChatId] = new Open { Key = incident.IncidentId, Start = incident.StartedUtc };
                System.Threading.Volatile.Write(ref _open, Incidents.Count);
                // The joins and messages that set it off, before its start, in order.
                if (RecentJoins.TryGetValue(incident.ChatId, out var q))
                    foreach (var j in q) Event(incident.ChatId, "join", j.UserId, JoinDetail(incident.IncidentId, j), j.At.UtcDateTime);
                if (RecentMessages.Remove(incident.ChatId, out var m))
                    foreach (var x in m) Event(incident.ChatId, "message", x.User, MessageDetail(incident.IncidentId, x.Print, x.Untrusted), x.At);
                Event(incident.ChatId, "phase", null, new { phase = incident.Phase.ToString().ToLowerInvariant(), incident.Trigger, incident.PeakScore, incident.Reasons });
                Row(incident, null);
            }
        }

        internal static void Activated(RaidIncident incident, long? by)
        {
            if (!Enabled()) return;
            Started(incident);
            lock (Lock)
            {
                if (!Incidents.TryGetValue(incident.ChatId, out var open)) return;
                open.ActivatedAt ??= Now;
                Event(incident.ChatId, "phase", null, new { phase = "active", incident.Trigger, by_admin = by.HasValue, actions = incident.Actions.ToString() });
                Row(incident, null);
            }
        }

        internal static void Action(long chatId, string action, long userId)
        {
            if (!Enabled()) return;
            lock (Lock)
            {
                if (action == "locked" && Incidents.TryGetValue(chatId, out var open)) open.LockedAt ??= Now;
                Event(chatId, "action", userId == 0 ? null : userId, new { action });
            }
        }

        /// <summary>Telegram answered 429 in a chat (or bot-wide, chat ""): recorded in every raid it touches.</summary>
        internal static void RateLimited(Lane lane, string chat, int seconds)
        {
            if (!Enabled()) return;
            lock (Lock)
            {
                if (Incidents.Count == 0) return;
                foreach (var chatId in Incidents.Keys.ToList())
                    if (chat.Length == 0 || chat == chatId.ToString())
                        Event(chatId, "rate_limit", null, new { lane = lane.ToString().ToLowerInvariant(), seconds });
            }
        }

        internal static void Ended(RaidIncident incident, long? by, bool ignored)
        {
            if (!Enabled()) return;
            lock (Lock)
            {
                if (!Incidents.ContainsKey(incident.ChatId)) Started(incident);
                Event(incident.ChatId, "phase", null, new { phase = "ended" });
                Row(incident, ignored ? "ignored" : by.HasValue ? "admin" : "quiet");
                Incidents.Remove(incident.ChatId);
                System.Threading.Volatile.Write(ref _open, Incidents.Count);
            }
        }

        // With the lock held. The incident's summary row, as it stands now.
        private static void Row(RaidIncident incident, string endedBy)
        {
            Incidents.TryGetValue(incident.ChatId, out var open);
            DateTimeOffset? Utc(DateTime? t) => t.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(t.Value, DateTimeKind.Utc)) : null;
            _rows[incident.IncidentId] = new RaidIncidentRow(incident.IncidentId, incident.ChatId, Edition, incident.Trigger,
                incident.Phase == RaidPhase.Active || incident.ActiveSinceUtc.HasValue ? "active" : "alert",
                Utc(incident.StartedUtc).Value, Utc(incident.ActiveSinceUtc ?? open?.ActivatedAt), Utc(open?.LockedAt),
                endedBy != null ? Utc(Now) : null, endedBy, incident.PeakScore, incident.Reasons?.ToArray(),
                incident.Joined, incident.Muted, incident.Banned);
        }

        /// <summary>Writes what was recorded. Called by the principal flush, after the groups exist.</summary>
        internal static async Task FlushAsync()
        {
            TelemetryBatch batch;
            lock (Lock)
            {
                batch = new TelemetryBatch(_rows.Values.ToList(), _events);
                _rows = new Dictionary<string, RaidIncidentRow>();
                _events = new List<RaidEventRow>();
            }
            if (batch.IsEmpty || !PostgresStorage.Ready) return;
            try
            {
                await Repositories.RaidTelemetry.ApplyAsync(batch);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing raid telemetry ({batch.Incidents.Count} incidents, {batch.Events.Count} events); retrying next minute");
                lock (Lock)
                {
                    foreach (var row in batch.Incidents) _rows.TryAdd(row.IncidentKey, row);
                    _events.InsertRange(0, batch.Events.Take(MaxEvents));
                }
            }
        }

        /// <summary>Forgets everything, for tests.</summary>
        internal static void ResetForTests()
        {
            lock (Lock)
            {
                RecentJoins.Clear();
                RecentMessages.Clear();
                Incidents.Clear();
                _open = 0;
                _rows = new Dictionary<string, RaidIncidentRow>();
                _events = new List<RaidEventRow>();
            }
        }
    }
}
