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

        internal string ToJson() => JsonConvert.SerializeObject(this);
        internal static RaidIncident FromJson(string json) => JsonConvert.DeserializeObject<RaidIncident>(json);
    }
}
