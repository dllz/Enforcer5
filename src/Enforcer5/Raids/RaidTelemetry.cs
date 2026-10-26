using System.Collections.Generic;
using Enforcer5.Data;

namespace Enforcer5.Raids
{
    /// <summary>
    /// Records raid profiles for telemetry and replay. Filled in by the telemetry step; until then
    /// every call does nothing.
    /// </summary>
    internal static class RaidTelemetry
    {
        internal static void Signal(long chatId, int score, IReadOnlyList<string> reasons) { }
        internal static void Started(RaidIncident incident) { }
        internal static void Activated(RaidIncident incident, long? by) { }
        internal static void Joined(JoinEvent join) { }
        internal static void Action(long chatId, string action, long userId) { }
        internal static void Ended(RaidIncident incident, long? by) { }
    }
}
