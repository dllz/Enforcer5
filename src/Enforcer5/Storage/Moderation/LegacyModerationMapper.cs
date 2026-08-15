using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Enforcer5.Storage.Moderation
{
    internal static class LegacyModerationMapper
    {
        internal static ModerationSnapshot MapGroup(
            long groupId,
            IEnumerable<string> authorizedUsers,
            IEnumerable<string> moderators,
            IEnumerable<string> elevationBlocks,
            IEnumerable<string> watchDisabledUsers,
            IEnumerable<string> reportBlockedUsers,
            IEnumerable<string> adminNotificationsDisabledUsers,
            IEnumerable<string> tagAllSubscribers,
            IReadOnlyDictionary<string, string> standardWarnings,
            IReadOnlyDictionary<string, string> lifetimeWarnings,
            IReadOnlyDictionary<string, string> prewarnings,
            IReadOnlyDictionary<string, string> mediaWarnings,
            DateTimeOffset capturedAt)
        {
            var roles = MapUsers(authorizedUsers, (userId) =>
                new GroupRoleAssignment(groupId, userId, GroupRoles.Authorized, capturedAt, null));
            roles.AddRange(MapUsers(moderators, (userId) =>
                new GroupRoleAssignment(groupId, userId, GroupRoles.Moderator, capturedAt, null)));

            var blocks = MapUsers(elevationBlocks, (userId) =>
                new GroupElevationBlock(groupId, userId, capturedAt, null));
            var policies = MapPolicies(groupId, capturedAt,
                (watchDisabledUsers, GroupUserPolicyNames.WatchDisabled),
                (reportBlockedUsers, GroupUserPolicyNames.ReportsBlocked),
                (adminNotificationsDisabledUsers, GroupUserPolicyNames.AdminNotificationsDisabled),
                (tagAllSubscribers, GroupUserPolicyNames.TagAllSubscribed));
            var balances = MapBalances(groupId, standardWarnings, lifetimeWarnings,
                prewarnings, mediaWarnings, capturedAt);

            return new ModerationSnapshot(roles, blocks, policies, balances);
        }

        private static List<T> MapUsers<T>(IEnumerable<string> values, Func<long, T> map)
        {
            return (values ?? Array.Empty<string>())
                .Select(ParseUserId)
                .Where(userId => userId.HasValue)
                .Select(userId => map(userId.Value))
                .ToList();
        }

        private static List<GroupUserPolicy> MapPolicies(long groupId, DateTimeOffset capturedAt,
            params (IEnumerable<string> Users, string Policy)[] sources)
        {
            var policies = new List<GroupUserPolicy>();
            foreach (var source in sources)
                policies.AddRange(MapUsers(source.Users, userId =>
                    new GroupUserPolicy(groupId, userId, source.Policy, true, capturedAt)));
            return policies;
        }

        private static List<WarningBalance> MapBalances(long groupId,
            IReadOnlyDictionary<string, string> standard,
            IReadOnlyDictionary<string, string> lifetime,
            IReadOnlyDictionary<string, string> prewarnings,
            IReadOnlyDictionary<string, string> media,
            DateTimeOffset capturedAt)
        {
            standard ??= new Dictionary<string, string>();
            lifetime ??= new Dictionary<string, string>();
            prewarnings ??= new Dictionary<string, string>();
            media ??= new Dictionary<string, string>();
            var userIds = standard.Keys.Concat(lifetime.Keys).Concat(prewarnings.Keys).Concat(media.Keys)
                .Select(ParseUserId).Where(value => value.HasValue).Select(value => value.Value).Distinct();
            var balances = new List<WarningBalance>();

            foreach (var userId in userIds)
            {
                var key = userId.ToString(CultureInfo.InvariantCulture);
                var active = Quantity(standard, key);
                var total = Math.Max(active, Quantity(lifetime, key));
                AddBalance(balances, groupId, userId, WarningTypes.Standard, active, total, capturedAt);
                var prewarningQuantity = Quantity(prewarnings, key);
                AddBalance(balances, groupId, userId, WarningTypes.Prewarning,
                    prewarningQuantity, prewarningQuantity, capturedAt);
                var mediaQuantity = Quantity(media, key);
                AddBalance(balances, groupId, userId, WarningTypes.Media,
                    mediaQuantity, mediaQuantity, capturedAt);
            }
            return balances;
        }

        private static void AddBalance(List<WarningBalance> balances, long groupId, long userId,
            string warningType, long active, long lifetime, DateTimeOffset capturedAt)
        {
            if (active == 0 && lifetime == 0) return;
            balances.Add(new WarningBalance(groupId, userId, warningType, active, lifetime, capturedAt));
        }

        private static long Quantity(IReadOnlyDictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out var raw) &&
                   long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity)
                ? Math.Max(0, quantity)
                : 0;
        }

        private static long? ParseUserId(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId) &&
                   userId != 0 ? userId : null;
        }
    }
}
