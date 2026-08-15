using System;
using System.Globalization;

namespace Enforcer5.Storage
{
    internal sealed record MigrationCommandOptions(string Domain, bool Reconcile, long? GroupId);

    internal static class MigrationCommandOptionsParser
    {
        internal static bool TryParse(string[] args, out MigrationCommandOptions options)
        {
            options = null;
            if (args == null || (args.Length != 3 && args.Length != 5) ||
                !string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase) ||
                !IsDomain(args[1]) || !TryMode(args[2], out var reconcile)) return false;

            long? groupId = null;
            if (args.Length == 5)
            {
                if (!string.Equals(args[3], "--group", StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed == 0) return false;
                groupId = parsed;
            }

            options = new MigrationCommandOptions(args[1].ToLowerInvariant(), reconcile, groupId);
            return true;
        }

        private static bool IsDomain(string value) =>
            string.Equals(value, "group-settings", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "principal-content", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "moderation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "operations", StringComparison.OrdinalIgnoreCase);

        private static bool TryMode(string value, out bool reconcile)
        {
            reconcile = string.Equals(value, "reconcile", StringComparison.OrdinalIgnoreCase);
            return reconcile || string.Equals(value, "backfill", StringComparison.OrdinalIgnoreCase);
        }
    }
}
