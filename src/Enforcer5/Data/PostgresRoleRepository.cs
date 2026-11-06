using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// The lists of members per group and the global lists that used to be Redis sets (rollout step 14). A group's
    /// list is keyed by the group; a global list by 0.
    /// </summary>
    internal enum RoleList
    {
        /// <summary>/auth (<c>chat:{id}:auth</c>): may elevate, deelevate, auth and block others; trusted.</summary>
        Authorized,
        /// <summary>/elevate by the creator or an authorized member (<c>chat:{id}:mod</c>): elevated for good.</summary>
        Moderator,
        /// <summary>/blockelevate (<c>chat:{id}:deauth</c>): may not elevate as a plain admin.</summary>
        ElevationBlocked,
        /// <summary>/disablewatch (<c>chat:{id}:watch</c>): exempt from the checks, trusted, kept despite a global ban.</summary>
        Exempt,
        /// <summary>/adminoff (<c>chat:{id}:adminOff</c>): no @admin reports for this admin.</summary>
        AdminReportsOff,
        /// <summary>/reportblock (<c>chat:{id}:reportblocked</c>): may not report.</summary>
        ReportBlocked,
        /// <summary>/pingme (<c>chat:{id}:tagall2</c>): on /pingall's list.</summary>
        PingMe,
        /// <summary>/makelangadmin (<c>langAdmins</c>): reviews language uploads. Global.</summary>
        LanguageAdmin,
        /// <summary>/allowp (<c>premiumBot</c>): groups the premium edition serves. Global, by group id.</summary>
        Premium,
        /// <summary>/bangroup (<c>bot:bannedGroups</c>): groups and users the bot ignores. Global, by Telegram id.</summary>
        BotBanned
    }

    /// <summary>One member (or group) on one list.</summary>
    internal sealed record RoleEntry(long ChatId, RoleList List, long Id);

    internal static class RoleLists
    {
        internal static bool IsGlobal(this RoleList list) => list is RoleList.LanguageAdmin or RoleList.Premium or RoleList.BotBanned;

        internal static readonly RoleList[] GroupLists =
        {
            RoleList.Authorized, RoleList.Moderator, RoleList.ElevationBlocked, RoleList.Exempt, RoleList.AdminReportsOff,
            RoleList.ReportBlocked, RoleList.PingMe
        };

        internal static readonly RoleList[] GlobalLists = { RoleList.LanguageAdmin, RoleList.Premium, RoleList.BotBanned };

        /// <summary>The Redis set the list was kept in until step 14.</summary>
        internal static string LegacyKey(long chatId, RoleList list) => list switch
        {
            RoleList.Authorized => $"chat:{chatId}:auth",
            RoleList.Moderator => $"chat:{chatId}:mod",
            RoleList.ElevationBlocked => $"chat:{chatId}:deauth",
            RoleList.Exempt => $"chat:{chatId}:watch",
            RoleList.AdminReportsOff => $"chat:{chatId}:adminOff",
            RoleList.ReportBlocked => $"chat:{chatId}:reportblocked",
            RoleList.PingMe => $"chat:{chatId}:tagall2",
            RoleList.LanguageAdmin => "langAdmins",
            RoleList.Premium => "premiumBot",
            RoleList.BotBanned => "bot:bannedGroups",
            _ => throw new ArgumentOutOfRangeException(nameof(list), list, null)
        };
    }

    /// <summary>
    /// The lists in PostgreSQL, in the approved design's tables: <c>group_role_assignments</c> (authorized,
    /// moderator) and <c>group_elevation_blocks</c> as periods; <c>group_user_preferences</c> (exempt, no reports, report
    /// blocked, ping me) as flags; <c>global_role_assignments</c> (<c>language_admin</c>), <c>group_entitlements</c>
    /// (<c>premium</c>), and <c>bot_bans</c>, which is new. All of it is small (about 90 members in groups and 80 global
    /// entries on 2026-11-06), so it is read whole.
    /// </summary>
    internal sealed class PostgresRoleRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresRoleRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        /// <summary>Every entry of every list, and the groups whose old Redis sets were imported.</summary>
        internal async Task<(IReadOnlyList<RoleEntry> Entries, IReadOnlyCollection<long> Imported)> LoadAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            var rows = await connection.QueryAsync<(long Chat, string List, long Id)>(@"
SELECT c.telegram_chat_id, r.role, a.telegram_user_id
FROM group_role_assignments r
JOIN groups g ON g.group_id = r.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN user_identity_links l ON l.user_id = r.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE r.revoked_at IS NULL
UNION ALL
SELECT c.telegram_chat_id, 'blocked', a.telegram_user_id
FROM group_elevation_blocks b
JOIN groups g ON g.group_id = b.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN user_identity_links l ON l.user_id = b.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE b.revoked_at IS NULL
UNION ALL
SELECT c.telegram_chat_id, f.list, a.telegram_user_id
FROM group_user_preferences p
JOIN groups g ON g.group_id = p.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
JOIN user_identity_links l ON l.user_id = p.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts a ON a.account_id = l.account_id
CROSS JOIN LATERAL (VALUES ('exempt', p.watch_disabled), ('adminoff', p.admin_notifications_disabled),
                           ('reportblocked', p.reports_blocked), ('pingme', p.tag_all_subscribed)) AS f(list, set)
WHERE f.set
UNION ALL
SELECT 0, r.role, a.telegram_user_id
FROM global_role_assignments r
JOIN user_identity_links l ON l.user_id = r.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE r.revoked_at IS NULL AND r.role = 'language_admin'
UNION ALL
SELECT 0, 'premium', c.telegram_chat_id
FROM group_entitlements e JOIN groups g ON g.group_id = e.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE e.entitlement = 'premium' AND e.enabled AND (e.expires_at IS NULL OR e.expires_at > now())
UNION ALL
SELECT 0, 'botbanned', telegram_id FROM bot_bans");
            var imported = await connection.QueryAsync<string>(
                "SELECT name FROM storage_imports WHERE name LIKE 'roles:%'");
            var entries = rows.Select(r => new RoleEntry(r.Chat, ListOf(r.List), r.Id)).ToList();
            var groups = imported.Select(n => long.TryParse(n.Substring("roles:".Length), out var id) ? id : (long?)null)
                .Where(id => id.HasValue).Select(id => id.Value).ToList();
            return (entries, groups);
        }

        private static RoleList ListOf(string value) => value switch
        {
            "authorized" => RoleList.Authorized,
            "moderator" => RoleList.Moderator,
            "blocked" => RoleList.ElevationBlocked,
            "exempt" => RoleList.Exempt,
            "adminoff" => RoleList.AdminReportsOff,
            "reportblocked" => RoleList.ReportBlocked,
            "pingme" => RoleList.PingMe,
            "language_admin" => RoleList.LanguageAdmin,
            "premium" => RoleList.Premium,
            "botbanned" => RoleList.BotBanned,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

        /// <summary>Puts <paramref name="id"/> on the list or takes it off. Idempotent.</summary>
        internal async Task SetAsync(RoleEntry entry, bool on, long? actorId, DateTimeOffset at)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SetAsync(connection, transaction, entry, on, actorId, at);
            await transaction.CommitAsync();
        }

        /// <summary>
        /// Imports the old Redis sets: a group's (<paramref name="chatId"/>) or the global ones (0), once ever
        /// (<c>storage_imports</c>, <c>roles:{chat}</c>). False when they had been imported already.
        /// </summary>
        internal async Task<bool> ImportAsync(long chatId, IReadOnlyList<RoleEntry> entries, DateTimeOffset at)
        {
            var name = $"roles:{chatId}";
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@Name))", new { Name = name }, transaction));
            if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM storage_imports WHERE name = @Name)", new { Name = name }, transaction)))
                return false;
            foreach (var entry in entries) await SetAsync(connection, transaction, entry, true, null, at);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO storage_imports (name, done_at, details) VALUES (@Name, @At, @Details)",
                new { Name = name, At = at, Details = $"{entries.Count} entries" }, transaction));
            await transaction.CommitAsync();
            return true;
        }

        private static async Task SetAsync(DbConnection connection, DbTransaction transaction, RoleEntry entry, bool on,
            long? actorId, DateTimeOffset at)
        {
            Task Lock() => connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            Task<PrincipalResolver.UserIds> User(long id) => PrincipalResolver.FindOrEnsureUserAsync(connection, transaction, id, Lock);
            Task<long> Group(long id) => PrincipalResolver.FindOrEnsureGroupAsync(connection, transaction, id,
                PostgresBanHistoryRepository.ChatType(id), Lock);
            CommandDefinition Sql(string sql, object args) => new(sql, args, transaction);

            switch (entry.List)
            {
                case RoleList.Authorized:
                case RoleList.Moderator:
                {
                    var role = entry.List == RoleList.Authorized ? "authorized" : "moderator";
                    var (group, user) = (await Group(entry.ChatId), (await User(entry.Id)).UserId);
                    await connection.ExecuteAsync(Sql(on
                        ? @"INSERT INTO group_role_assignments (group_id, user_id, role, assigned_at) VALUES (@Group, @User, @Role, @At)
                            ON CONFLICT (group_id, user_id, role) WHERE revoked_at IS NULL DO NOTHING"
                        : @"UPDATE group_role_assignments SET revoked_at = GREATEST(@At, assigned_at)
                            WHERE group_id = @Group AND user_id = @User AND role = @Role AND revoked_at IS NULL",
                        new { Group = group, User = user, Role = role, At = at }));
                    break;
                }
                case RoleList.ElevationBlocked:
                {
                    var (group, user) = (await Group(entry.ChatId), (await User(entry.Id)).UserId);
                    await connection.ExecuteAsync(Sql(on
                        ? @"INSERT INTO group_elevation_blocks (group_id, user_id, blocked_at) VALUES (@Group, @User, @At)
                            ON CONFLICT (group_id, user_id) WHERE revoked_at IS NULL DO NOTHING"
                        : @"UPDATE group_elevation_blocks SET revoked_at = GREATEST(@At, blocked_at)
                            WHERE group_id = @Group AND user_id = @User AND revoked_at IS NULL",
                        new { Group = group, User = user, At = at }));
                    break;
                }
                case RoleList.Exempt:
                case RoleList.AdminReportsOff:
                case RoleList.ReportBlocked:
                case RoleList.PingMe:
                {
                    var column = entry.List switch
                    {
                        RoleList.Exempt => "watch_disabled",
                        RoleList.AdminReportsOff => "admin_notifications_disabled",
                        RoleList.ReportBlocked => "reports_blocked",
                        _ => "tag_all_subscribed"
                    };
                    var (group, user) = (await Group(entry.ChatId), (await User(entry.Id)).UserId);
                    await connection.ExecuteAsync(Sql($@"
INSERT INTO group_user_preferences (group_id, user_id, {column}, updated_at) VALUES (@Group, @User, @On, @At)
ON CONFLICT (group_id, user_id) DO UPDATE SET {column} = EXCLUDED.{column}, updated_at = EXCLUDED.updated_at",
                        new { Group = group, User = user, On = on, At = at }));
                    break;
                }
                case RoleList.LanguageAdmin:
                {
                    var user = (await User(entry.Id)).UserId;
                    await connection.ExecuteAsync(Sql(on
                        ? @"INSERT INTO global_role_assignments (user_id, role, assigned_at) VALUES (@User, 'language_admin', @At)
                            ON CONFLICT (user_id, role) WHERE revoked_at IS NULL DO NOTHING"
                        : @"UPDATE global_role_assignments SET revoked_at = GREATEST(@At, assigned_at)
                            WHERE user_id = @User AND role = 'language_admin' AND revoked_at IS NULL",
                        new { User = user, At = at }));
                    break;
                }
                case RoleList.Premium:
                {
                    var group = await Group(entry.Id);
                    await connection.ExecuteAsync(Sql(@"
INSERT INTO group_entitlements (group_id, entitlement, enabled) VALUES (@Group, 'premium', @On)
ON CONFLICT (group_id, entitlement) DO UPDATE SET enabled = EXCLUDED.enabled",
                        new { Group = group, On = on }));
                    break;
                }
                case RoleList.BotBanned:
                {
                    long? by = actorId.HasValue ? (await User(actorId.Value)).AccountId : null;
                    await connection.ExecuteAsync(Sql(on
                        ? @"INSERT INTO bot_bans (telegram_id, banned_at, banned_by_account_id) VALUES (@Id, @At, @By)
                            ON CONFLICT (telegram_id) DO NOTHING"
                        : "DELETE FROM bot_bans WHERE telegram_id = @Id",
                        new { entry.Id, At = at, By = by }));
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(entry), entry.List, null);
            }
        }
    }
}
