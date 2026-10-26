using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;
using Newtonsoft.Json;
using Npgsql;

namespace Enforcer5.Data
{
    /// <summary>A raid's summary row, as written and rewritten while it runs.</summary>
    internal sealed record RaidIncidentRow(string IncidentKey, long TelegramChatId, string Edition, string Trigger, string PhaseReached,
        DateTimeOffset StartedAt, DateTimeOffset? ActivatedAt, DateTimeOffset? LockedAt, DateTimeOffset? EndedAt, string EndedBy,
        int PeakScore, string[] Reasons, int Joined, int Muted, int Banned);

    /// <summary>One point on a raid's timeline. <see cref="Detail"/> is JSON.</summary>
    internal sealed record RaidEventRow(string IncidentKey, long OffsetMs, string Kind, string Actor, string Detail);

    internal sealed record TelemetryBatch(IReadOnlyList<RaidIncidentRow> Incidents, IReadOnlyList<RaidEventRow> Events)
    {
        internal bool IsEmpty => Incidents.Count == 0 && Events.Count == 0;
    }

    /// <summary>A raid as exported by /raidprofile and replayed by the test harness. No ids, no text.</summary>
    internal sealed class RaidProfile
    {
        public string IncidentKey { get; set; }
        public string Trigger { get; set; }
        public string PhaseReached { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public long? ActivatedAfterMs { get; set; }
        public long? LockedAfterMs { get; set; }
        public long? EndedAfterMs { get; set; }
        public string EndedBy { get; set; }
        public int PeakScore { get; set; }
        public string[] Reasons { get; set; }
        public int Joined { get; set; }
        public int Muted { get; set; }
        public int Banned { get; set; }
        public List<RaidProfileEvent> Events { get; set; } = new();

        internal string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
        internal static RaidProfile FromJson(string json) => JsonConvert.DeserializeObject<RaidProfile>(json);
    }

    internal sealed class RaidProfileEvent
    {
        public long T { get; set; }
        public string Kind { get; set; }
        public string Actor { get; set; }
        public Dictionary<string, object> Detail { get; set; }
    }

    internal interface IRaidTelemetryRepository
    {
        Task ApplyAsync(TelemetryBatch batch);

        /// <summary>
        /// The raid with this key or numeric id; or, with neither, the latest one in
        /// <paramref name="telegramChatId"/> (any group when null). Null when there is none.
        /// </summary>
        Task<RaidProfile> GetProfileAsync(string incident, long? telegramChatId);

        /// <summary>Drops raids older than 90 days.</summary>
        Task CleanUpAsync(DateTimeOffset now);
    }

    internal sealed class PostgresRaidTelemetryRepository : IRaidTelemetryRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresRaidTelemetryRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        public async Task ApplyAsync(TelemetryBatch batch)
        {
            if (batch.IsEmpty) return;
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var i in batch.Incidents)
            {
                await connection.ExecuteAsync(@"
INSERT INTO raid_incidents (incident_key, group_id, edition, trigger, phase_reached, started_at, activated_at, locked_at,
    ended_at, ended_by, peak_score, reasons, joined, muted, banned)
SELECT @IncidentKey, g.group_id, @Edition, @Trigger, @PhaseReached, @StartedAt, @ActivatedAt, @LockedAt, @EndedAt, @EndedBy,
       @PeakScore, @Reasons, @Joined, @Muted, @Banned
FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @TelegramChatId
ON CONFLICT (incident_key) DO UPDATE SET
    trigger = EXCLUDED.trigger, phase_reached = EXCLUDED.phase_reached,
    activated_at = COALESCE(raid_incidents.activated_at, EXCLUDED.activated_at),
    locked_at = COALESCE(raid_incidents.locked_at, EXCLUDED.locked_at),
    ended_at = EXCLUDED.ended_at, ended_by = EXCLUDED.ended_by,
    peak_score = GREATEST(raid_incidents.peak_score, EXCLUDED.peak_score), reasons = EXCLUDED.reasons,
    joined = GREATEST(raid_incidents.joined, EXCLUDED.joined), muted = GREATEST(raid_incidents.muted, EXCLUDED.muted),
    banned = GREATEST(raid_incidents.banned, EXCLUDED.banned);",
                    new
                    {
                        i.IncidentKey, i.TelegramChatId, i.Edition, i.Trigger, i.PhaseReached,
                        StartedAt = i.StartedAt.UtcDateTime, ActivatedAt = i.ActivatedAt?.UtcDateTime, LockedAt = i.LockedAt?.UtcDateTime,
                        EndedAt = i.EndedAt?.UtcDateTime, i.EndedBy, i.PeakScore, Reasons = i.Reasons ?? Array.Empty<string>(),
                        i.Joined, i.Muted, i.Banned
                    }, transaction);
            }
            if (batch.Events.Count > 0)
            {
                // Events of an incident whose row is not written (its group unknown to PostgreSQL) are dropped.
                await connection.ExecuteAsync(@"
INSERT INTO raid_events (incident_id, offset_ms, kind, actor, detail)
SELECT i.incident_id, e.offset_ms, e.kind, e.actor, e.detail::jsonb
FROM unnest(@Keys, @Offsets, @Kinds, @Actors, @Details) AS e(incident_key, offset_ms, kind, actor, detail)
JOIN raid_incidents i ON i.incident_key = e.incident_key;",
                    new
                    {
                        Keys = batch.Events.Select(e => e.IncidentKey).ToArray(),
                        Offsets = batch.Events.Select(e => e.OffsetMs).ToArray(),
                        Kinds = batch.Events.Select(e => e.Kind).ToArray(),
                        Actors = batch.Events.Select(e => e.Actor).ToArray(),
                        Details = batch.Events.Select(e => e.Detail).ToArray()
                    }, transaction);
            }
            await transaction.CommitAsync();
        }

        private sealed class IncidentRecord
        {
            public long IncidentId { get; set; }
            public string IncidentKey { get; set; }
            public string Trigger { get; set; }
            public string PhaseReached { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime? ActivatedAt { get; set; }
            public DateTime? LockedAt { get; set; }
            public DateTime? EndedAt { get; set; }
            public string EndedBy { get; set; }
            public int PeakScore { get; set; }
            public string[] Reasons { get; set; }
            public int Joined { get; set; }
            public int Muted { get; set; }
            public int Banned { get; set; }
        }

        public async Task<RaidProfile> GetProfileAsync(string incident, long? telegramChatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            const string columns = @"i.incident_id AS IncidentId, i.incident_key AS IncidentKey, i.trigger AS Trigger,
    i.phase_reached AS PhaseReached, i.started_at AS StartedAt, i.activated_at AS ActivatedAt, i.locked_at AS LockedAt,
    i.ended_at AS EndedAt, i.ended_by AS EndedBy, i.peak_score AS PeakScore, i.reasons AS Reasons, i.joined AS Joined,
    i.muted AS Muted, i.banned AS Banned";
            IncidentRecord row;
            if (!string.IsNullOrEmpty(incident))
                row = await connection.QuerySingleOrDefaultAsync<IncidentRecord>($@"
SELECT {columns} FROM raid_incidents i WHERE i.incident_key = @Incident OR i.incident_id::text = @Incident LIMIT 1", new { Incident = incident });
            else
                row = await connection.QuerySingleOrDefaultAsync<IncidentRecord>($@"
SELECT {columns} FROM raid_incidents i
JOIN groups g ON g.group_id = i.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE @Chat IS NULL OR c.telegram_chat_id = @Chat
ORDER BY i.started_at DESC LIMIT 1", new { Chat = telegramChatId });
            if (row == null) return null;

            var events = await connection.QueryAsync<(long OffsetMs, string Kind, string Actor, string Detail)>(@"
SELECT offset_ms, kind, actor, detail::text FROM raid_events WHERE incident_id = @Id ORDER BY offset_ms, event_id", new { Id = row.IncidentId });
            long? After(DateTime? at) => at.HasValue ? (long)(at.Value - row.StartedAt).TotalMilliseconds : null;
            return new RaidProfile
            {
                IncidentKey = row.IncidentKey, Trigger = row.Trigger, PhaseReached = row.PhaseReached,
                StartedAt = new DateTimeOffset(DateTime.SpecifyKind(row.StartedAt, DateTimeKind.Utc)),
                ActivatedAfterMs = After(row.ActivatedAt), LockedAfterMs = After(row.LockedAt), EndedAfterMs = After(row.EndedAt),
                EndedBy = row.EndedBy, PeakScore = row.PeakScore, Reasons = row.Reasons, Joined = row.Joined, Muted = row.Muted,
                Banned = row.Banned,
                Events = events.Select(e => new RaidProfileEvent
                {
                    T = e.OffsetMs, Kind = e.Kind, Actor = e.Actor,
                    Detail = e.Detail == null ? null : JsonConvert.DeserializeObject<Dictionary<string, object>>(e.Detail)
                }).ToList()
            };
        }

        public async Task CleanUpAsync(DateTimeOffset now)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync("DELETE FROM raid_incidents WHERE started_at < @Now - interval '90 days'",
                new { Now = now.UtcDateTime }, commandTimeout: 300);
        }
    }
}
