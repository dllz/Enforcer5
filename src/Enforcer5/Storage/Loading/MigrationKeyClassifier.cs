using System;

namespace Enforcer5.Storage.Loading
{
    /// <summary>
    /// What a single Redis key is, derived from its shape alone.
    /// </summary>
    /// <param name="KeyClass">One of <see cref="MigrationKeyClasses"/>.</param>
    /// <param name="TelegramChatId">The owning group, where the shape carries one.</param>
    /// <param name="TelegramUserId">The subject user, where the shape carries one.</param>
    internal readonly record struct ClassifiedKey(string KeyClass, long? TelegramChatId, long? TelegramUserId)
    {
        internal static readonly ClassifiedKey Unclassified =
            new(MigrationKeyClasses.Unclassified, null, null);

        internal bool IsUnclassified =>
            string.Equals(KeyClass, MigrationKeyClasses.Unclassified, StringComparison.Ordinal);
    }

    /// <summary>
    /// Classifies a Redis key by shape, with no round trips.
    /// <para>
    /// Shape alone is the only affordable signal: at 19.4M keys, a <c>TYPE</c> or <c>TTL</c> call
    /// per key would dominate the scan entirely. That constrains what can be known - a key whose
    /// ownership is genuinely ambiguous is reported as
    /// <see cref="MigrationKeyClasses.Unclassified"/> rather than guessed at, because a wrong owner
    /// silently migrates one group's data into another.
    /// </para>
    /// <para>
    /// Several legacy shapes are irregular and are handled explicitly rather than by a general
    /// rule, because a general rule gets them wrong:
    /// </para>
    /// <list type="bullet">
    /// <item><c>chat:{id}links</c> has no separator before the suffix.</item>
    /// <item><c>chat:{userId}</c> is a <em>user's</em> lifetime total, even though every other
    /// <c>chat:</c> key is group-scoped. Only the sign of the id distinguishes them.</item>
    /// <item><c>userJoin</c> exists both as <c>chat:{id}:userJoin</c> and as bare
    /// <c>{id}:userJoin</c>, from different call sites.</item>
    /// </list>
    /// </summary>
    internal static class MigrationKeyClassifier
    {
        internal static ClassifiedKey Classify(string key)
        {
            if (string.IsNullOrEmpty(key)) return ClassifiedKey.Unclassified;

            // Bot-wide singletons and the two global tempban hashes. These have no group dimension
            // at all, which is precisely why the tempban hashes were blocker B1.
            if (key.StartsWith("bot:", StringComparison.Ordinal) ||
                key is "tempbanned" or "tempbannedPremium" or "logChatGroups" or "premiumBot" or
                    "langAdmins" or "testWrite")
                return new ClassifiedKey(MigrationKeyClasses.BotGlobal, null, null);

            // Migration and runtime infrastructure, not user data.
            if (key.StartsWith("compat:", StringComparison.Ordinal) ||
                key.StartsWith("enforcer:", StringComparison.Ordinal) ||
                key.StartsWith("dbUpdate:", StringComparison.Ordinal) ||
                key.StartsWith("lenghtUpdate", StringComparison.Ordinal) ||
                key.StartsWith("spammers", StringComparison.Ordinal))
                return new ClassifiedKey(MigrationKeyClasses.Ephemeral, null, null);

            // "spam:{chatId}:{userId}" and "spam:added:{userId}" - deliberately cross-group, but
            // TTL-bounded and Redis-only for every edition, so no mixed state can arise.
            if (key.StartsWith("spam:", StringComparison.Ordinal))
                return new ClassifiedKey(MigrationKeyClasses.Ephemeral, null, null);

            // "{groupId}:users:{userId}" - the largest population at 7.4M keys.
            var usersMarker = key.IndexOf(":users:", StringComparison.Ordinal);
            if (usersMarker > 0 &&
                long.TryParse(key.AsSpan(0, usersMarker), out var statisticsGroupId) &&
                long.TryParse(key.AsSpan(usersMarker + 7), out var statisticsUserId))
                return new ClassifiedKey(MigrationKeyClasses.GroupStatistics,
                    statisticsGroupId, statisticsUserId);

            // "{chatId}:userJoin" - the prefixless variant, written from two call sites while the
            // rest of the codebase uses "chat:{chatId}:userJoin".
            var joinMarker = key.IndexOf(":userJoin", StringComparison.Ordinal);
            if (joinMarker > 0 && joinMarker + 9 == key.Length &&
                long.TryParse(key.AsSpan(0, joinMarker), out var joinGroupId))
                return new ClassifiedKey(MigrationKeyClasses.GroupScoped, joinGroupId, null);

            if (key.StartsWith("user:", StringComparison.Ordinal))
            {
                // "user:{id}" is the user record. "user:{id}:query" is inline query state.
                if (long.TryParse(key.AsSpan(5), out var userId))
                    return new ClassifiedKey(MigrationKeyClasses.User, null, userId);
                var querySuffix = key.IndexOf(':', 5);
                return querySuffix > 0 && long.TryParse(key.AsSpan(5, querySuffix - 5), out var queryUserId)
                    ? new ClassifiedKey(MigrationKeyClasses.Ephemeral, null, queryUserId)
                    : ClassifiedKey.Unclassified;
            }

            if (key.StartsWith("globalBan:", StringComparison.Ordinal))
                return long.TryParse(key.AsSpan(10), out var globalBanUserId)
                    ? new ClassifiedKey(MigrationKeyClasses.GlobalBan, null, globalBanUserId)
                    : ClassifiedKey.Unclassified;

            // "ban:{userId}" - 121,061 keys of lifetime moderation counters that nothing under
            // Storage/ touched until account_moderation_counters was added.
            if (key.StartsWith("ban:", StringComparison.Ordinal))
                return long.TryParse(key.AsSpan(4), out var banUserId)
                    ? new ClassifiedKey(MigrationKeyClasses.ModerationCounters, null, banUserId)
                    : ClassifiedKey.Unclassified;

            if (key.StartsWith("flagged:", StringComparison.Ordinal))
                return ClassifyReport(key, 8);
            if (key.StartsWith("flaggedReply:", StringComparison.Ordinal))
                return ClassifyReport(key, 13);

            if (key.StartsWith("chat:", StringComparison.Ordinal))
                return ClassifyChat(key);

            return ClassifiedKey.Unclassified;
        }

        private static ClassifiedKey ClassifyReport(string key, int prefixLength)
        {
            var separator = key.IndexOf(':', prefixLength);
            if (separator < 0) return ClassifiedKey.Unclassified;
            return long.TryParse(key.AsSpan(prefixLength, separator - prefixLength), out var groupId)
                ? new ClassifiedKey(MigrationKeyClasses.Report, groupId, null)
                : ClassifiedKey.Unclassified;
        }

        private static ClassifiedKey ClassifyChat(string key)
        {
            const int prefix = 5;
            var separator = key.IndexOf(':', prefix);

            if (separator < 0)
            {
                // No suffix separator. Either the bare id, or the irregular "chat:{id}links".
                if (long.TryParse(key.AsSpan(prefix), out var bareId))
                    // Sign is the only thing distinguishing a user's lifetime total from a group.
                    // Telegram group ids are negative, user ids positive.
                    return bareId > 0
                        ? new ClassifiedKey(MigrationKeyClasses.UserMessageTotal, null, bareId)
                        : new ClassifiedKey(MigrationKeyClasses.GroupScoped, bareId, null);

                if (key.EndsWith("links", StringComparison.Ordinal) &&
                    long.TryParse(key.AsSpan(prefix, key.Length - prefix - 5), out var linkGroupId))
                    return new ClassifiedKey(MigrationKeyClasses.GroupScoped, linkGroupId, null);

                return ClassifiedKey.Unclassified;
            }

            if (!long.TryParse(key.AsSpan(prefix, separator - prefix), out var chatId))
                return ClassifiedKey.Unclassified;

            var suffix = key.AsSpan(separator + 1);

            // Group-scoped but ephemeral: admin session caches and the TTL-bounded elevation block
            // list are rebuilt from Telegram, so they are recorded with their owner but not carried.
            if (suffix.StartsWith("adminses", StringComparison.Ordinal) ||
                suffix.StartsWith("blockList", StringComparison.Ordinal) ||
                suffix.StartsWith("spamList", StringComparison.Ordinal) ||
                suffix.StartsWith("lastwelcome", StringComparison.Ordinal))
                return new ClassifiedKey(MigrationKeyClasses.Ephemeral, chatId, null);

            // "chat:{groupId}:tempbanned:{userId}" carries a subject; the rest do not.
            var subjectSeparator = suffix.LastIndexOf(':');
            if (subjectSeparator > 0 &&
                long.TryParse(suffix[(subjectSeparator + 1)..], out var subjectUserId))
                return new ClassifiedKey(MigrationKeyClasses.GroupScoped, chatId, subjectUserId);

            return new ClassifiedKey(MigrationKeyClasses.GroupScoped, chatId, null);
        }
    }
}
