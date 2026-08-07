using System;
using System.Collections.Generic;

namespace Enforcer5.Storage
{
    internal static class GroupSettingGroups
    {
        internal const string Settings = "settings";
        internal const string Flood = "flood";
        internal const string FloodExceptions = "floodexceptions";
        internal const string Characters = "char";
        internal const string Media = "media";
        internal const string Warn = "warnsettings";
        internal const string Other = "otherSettings";
        internal const string Welcome = "welcome";
        internal const string NameLength = "antinamelengthsettings";
        internal const string TextLength = "antitextlengthsettings";

        internal static IReadOnlyList<string> All { get; } = new[]
        {
            Settings, Flood, FloodExceptions, Characters, Media, Warn, Other, Welcome,
            NameLength, TextLength
        };

        internal static bool IsSupported(string settingGroup)
        {
            foreach (var candidate in All)
                if (string.Equals(candidate, settingGroup, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
