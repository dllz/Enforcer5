using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Telegram.Bot.Types;

namespace Enforcer5.Raids
{
    /// <summary>Where an incident stands.</summary>
    internal enum RaidPhase
    {
        /// <summary>A possible raid the admins were asked about (human mode).</summary>
        Alert,
        /// <summary>Raid mode is on.</summary>
        Active
    }

    /// <summary>
    /// One raid, from the first alert to its end, as kept in Redis (<c>raid:{edition}:{chat}</c>) so
    /// a restart in the middle of one carries on: the group's own permissions from before the lock are
    /// here, and are what gets put back.
    /// </summary>
    internal sealed class RaidIncident
    {
        public long ChatId { get; set; }
        public string IncidentId { get; set; } = Guid.NewGuid().ToString("N");
        public RaidPhase Phase { get; set; }
        public RaidActions Actions { get; set; }
        /// <summary>auto, human, command or button: what turned raid mode on.</summary>
        public string Trigger { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? ActiveSinceUtc { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public bool Locked { get; set; }
        /// <summary>An admin turned raid mode on or added to it (/raid, a button): they judged it a raid.</summary>
        public bool AdminDecided { get; set; }
        /// <summary>The group's own permissions before the lock; null when not locked by us.</summary>
        public ChatPermissions SavedPermissions { get; set; }
        public int? NoticeMessageId { get; set; }
        public DateTime NoticeUpdatedUtc { get; set; }
        public int Joined { get; set; }
        public int Muted { get; set; }
        public int Banned { get; set; }
        public int PeakScore { get; set; }
        public List<string> Reasons { get; set; } = new();
        /// <summary>Untrusted members who showed raid signs.</summary>
        public HashSet<long> Suspects { get; set; } = new();
        /// <summary>Who has already been banned or muted in this incident, so nobody is asked about twice.</summary>
        public HashSet<long> Handled { get; set; } = new();
        /// <summary>Who joined during it, for the quarantine and the release button.</summary>
        public HashSet<long> Joiners { get; set; } = new();
        public HashSet<long> Quarantined { get; set; } = new();
        /// <summary>Content seen from several untrusted members in this incident: anyone untrusted posting it later is a suspect too.</summary>
        public HashSet<string> SpamKeys { get; set; } = new();
        /// <summary>Plain text several untrusted members echoed: anyone untrusted posting it later is a weak suspect.</summary>
        public HashSet<string> EchoKeys { get; set; } = new();
        /// <summary>The messages that made each suspect one (up to 20 each): deleted when they are muted, or when a channel is banned.</summary>
        public Dictionary<long, List<int>> SuspectMessages { get; set; } = new();
        /// <summary>Suspects who only posted the same plain text as other newcomers: banned only when an admin turned raid mode on.</summary>
        public HashSet<long> WeakSuspects { get; set; } = new();
        /// <summary>Suspects muted (auto mode before it is clearly a raid); banned too if it becomes one.</summary>
        public HashSet<long> MutedSuspects { get; set; } = new();

        /// <summary>What the incident's buttons carry, so a button from an earlier raid does nothing to this one.</summary>
        [JsonIgnore]
        internal string ShortId => IncidentId.Substring(0, 8);

        internal string ToJson() => JsonConvert.SerializeObject(this);
        internal static RaidIncident FromJson(string json) => JsonConvert.DeserializeObject<RaidIncident>(json);
    }
}
