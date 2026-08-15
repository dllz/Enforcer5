using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;

namespace Enforcer5.Storage
{
    internal sealed class GroupSettingsRepository
    {
        private readonly PostgresDatabase _database;
        internal GroupSettingsRepository(PostgresDatabase database) => _database = database;

        internal async Task<Dictionary<string, string>> GetAsync(long chatId, string settingGroup,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId,
                cancellationToken: cancellationToken);
            if (!groupId.HasValue) return new Dictionary<string, string>();
            var sourceKey = RedisKey(chatId, settingGroup);
            var sourceDisposition = await connection.ExecuteScalarAsync<string>(new CommandDefinition(@"
SELECT disposition FROM legacy_redis_records WHERE source_key = @SourceKey AND source_field = ''",
                new { SourceKey = sourceKey }, cancellationToken: cancellationToken));
            if (string.Equals(sourceDisposition, "excluded", StringComparison.Ordinal))
                return new Dictionary<string, string>();
            var result = await ReadTypedAsync(connection, groupId.Value, settingGroup, cancellationToken);
            var raw = await connection.QueryAsync<(string SourceField, string RawValue)>(new CommandDefinition(@"
SELECT source_field AS SourceField, raw_value AS RawValue FROM legacy_redis_records
WHERE source_key = @SourceKey AND disposition IN ('retained', 'quarantined')",
                new { SourceKey = sourceKey }, cancellationToken: cancellationToken));
            foreach (var field in raw)
                if (!string.IsNullOrEmpty(field.SourceField)) result[field.SourceField] = field.RawValue;
            return result;
        }

        internal async Task<long> MutateCanonicalAsync(long chatId, string settingGroup, string operation,
            string field, string value, bool mirrorRedis, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId,
                cancellationToken: cancellationToken);
            await EnsureSettingsAsync(connection, transaction, group.GroupId, cancellationToken);
            var current = await ReadTypedAsync(connection, group.GroupId, settingGroup, cancellationToken, transaction);
            var sourceKey = RedisKey(chatId, settingGroup);
            var rawCurrent = await connection.ExecuteScalarAsync<string>(new CommandDefinition(@"
SELECT raw_value FROM legacy_redis_records WHERE source_key = @SourceKey AND source_field = @Field
AND disposition IN ('retained', 'quarantined')", new { SourceKey = sourceKey, Field = field },
                transaction, cancellationToken: cancellationToken));
            if (rawCurrent != null) current[field] = rawCurrent;

            long result;
            string resultingValue = null;
            if (operation == "delete")
            {
                result = current.ContainsKey(field) ? 1 : 0;
                await ResetFieldAsync(connection, transaction, group.GroupId, settingGroup, field, cancellationToken);
                await MarkRawDeletedAsync(connection, transaction, sourceKey, field, cancellationToken);
            }
            else
            {
                if (operation == "increment")
                {
                    current.TryGetValue(field, out var existing);
                    long.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
                    result = checked(number + long.Parse(value, CultureInfo.InvariantCulture));
                    resultingValue = result.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    result = current.ContainsKey(field) ? 0 : 1;
                    resultingValue = value;
                }
                var target = await ApplyFieldAsync(connection, transaction, group.GroupId, chatId,
                    settingGroup, field, resultingValue, cancellationToken);
                await PreserveSourceAsync(connection, transaction, sourceKey, field, resultingValue,
                    target == null ? "quarantined" : "migrated", target, cancellationToken);
            }

            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_settings SET version = version + 1, updated_at = now() WHERE group_id = @GroupId",
                new { group.GroupId }, transaction, cancellationToken: cancellationToken));
            if (mirrorRedis)
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compatibility_outbox (id, domain, ordering_key, operation, payload, created_at, next_attempt_at)
VALUES (@Id, 'group-settings', @OrderingKey, @Operation, CAST(@Payload AS jsonb), clock_timestamp(), clock_timestamp())",
                    new { Id = Guid.NewGuid(), OrderingKey = $"group-settings:{chatId}:{settingGroup}", Operation = operation,
                        Payload = JsonConvert.SerializeObject(new { chatId, settingGroup, field, value, resultValue = resultingValue }) },
                    transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        internal async Task<bool> UpsertAsync(GroupSettingsSnapshot snapshot,
            CancellationToken cancellationToken = default, bool force = false,
            long? expectedCurrentRevision = null)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var accepted = await UpsertAsync(connection, transaction, snapshot, cancellationToken, force,
                expectedCurrentRevision);
            if (accepted) await transaction.CommitAsync(cancellationToken);
            else await transaction.RollbackAsync(cancellationToken);
            return accepted;
        }

        internal static async Task<bool> UpsertAsync(DbConnection connection, DbTransaction transaction,
            GroupSettingsSnapshot snapshot, CancellationToken cancellationToken = default,
            bool force = false, long? expectedCurrentRevision = null)
        {
            var marker = await connection.QuerySingleOrDefaultAsync<SourceMarker>(new CommandDefinition(@"
SELECT target_reference AS TargetReference FROM legacy_redis_records
WHERE source_key = @RedisKey AND source_field = '' FOR UPDATE",
                new { snapshot.RedisKey }, transaction, cancellationToken: cancellationToken));
            var currentRevision = ParseRevision(marker?.TargetReference);
            if (!force && currentRevision.HasValue && currentRevision.Value >= snapshot.SourceRevision) return false;
            if (force && expectedCurrentRevision.HasValue && currentRevision != expectedCurrentRevision) return false;

            var group = await PrincipalResolver.EnsureGroupAsync(connection, transaction, snapshot.ChatId,
                cancellationToken: cancellationToken);
            await EnsureSettingsAsync(connection, transaction, group.GroupId, cancellationToken);
            await ResetGroupAsync(connection, transaction, group.GroupId, snapshot.SettingGroup, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE legacy_redis_records SET disposition = 'excluded'
WHERE source_key = @RedisKey AND source_field <> ''", new { snapshot.RedisKey }, transaction,
                cancellationToken: cancellationToken));

            if (snapshot.SourceExists)
                foreach (var pair in snapshot.Fields)
                {
                    var target = await ApplyFieldAsync(connection, transaction, group.GroupId, snapshot.ChatId,
                        snapshot.SettingGroup, pair.Key, pair.Value, cancellationToken);
                    await PreserveSourceAsync(connection, transaction, snapshot.RedisKey, pair.Key, pair.Value,
                        target == null ? "quarantined" : "migrated", target, cancellationToken);
                }

            await PreserveSourceAsync(connection, transaction, snapshot.RedisKey, string.Empty,
                snapshot.Checksum, snapshot.SourceExists ? "migrated" : "excluded",
                $"group:{group.GroupId};revision:{snapshot.SourceRevision}", cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_settings SET version = version + 1, updated_at = now() WHERE group_id = @GroupId",
                new { group.GroupId }, transaction, cancellationToken: cancellationToken));
            return true;
        }

        internal async Task<bool> MatchesAsync(GroupSettingsSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            var marker = await connection.QuerySingleOrDefaultAsync<SourceMarker>(new CommandDefinition(@"
SELECT raw_value AS SourceChecksum, disposition AS Disposition, target_reference AS TargetReference
FROM legacy_redis_records WHERE source_key = @RedisKey AND source_field = ''",
                new { snapshot.RedisKey }, cancellationToken: cancellationToken));
            return marker != null &&
                   string.Equals(marker.SourceChecksum, snapshot.Checksum, StringComparison.Ordinal) &&
                   snapshot.SourceExists == !string.Equals(marker.Disposition, "excluded", StringComparison.Ordinal);
        }

        private static async Task<Dictionary<string, string>> ReadTypedAsync(DbConnection connection,
            long groupId, string settingGroup, CancellationToken cancellationToken,
            DbTransaction transaction = null)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (settingGroup == GroupSettingGroups.Settings || settingGroup == GroupSettingGroups.Flood ||
                settingGroup == GroupSettingGroups.Warn || settingGroup == GroupSettingGroups.Other ||
                settingGroup == GroupSettingGroups.Welcome || settingGroup == GroupSettingGroups.Media)
            {
                var row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(new CommandDefinition(@"
SELECT s.*, c.telegram_chat_id AS LogTelegramChatId FROM group_settings s
LEFT JOIN telegram_chats c ON c.chat_id = s.log_chat_id WHERE s.group_id = @GroupId",
                    new { GroupId = groupId }, transaction, cancellationToken: cancellationToken));
                if (row != null) AddScalarFields(values, settingGroup, row);
            }
            if (settingGroup == GroupSettingGroups.FloodExceptions)
                foreach (var row in await connection.QueryAsync<(string ContentType, bool Excluded)>(new CommandDefinition(
                    "SELECT content_type AS ContentType, excluded AS Excluded FROM group_flood_exceptions WHERE group_id = @GroupId",
                    new { GroupId = groupId }, transaction, cancellationToken: cancellationToken)))
                    values[LegacyMediaName(row.ContentType)] = YesNo(!row.Excluded);
            if (settingGroup == GroupSettingGroups.Media)
                foreach (var row in await connection.QueryAsync<(string MediaType, bool Allowed)>(new CommandDefinition(
                    "SELECT media_type AS MediaType, allowed AS Allowed FROM group_media_policies WHERE group_id = @GroupId",
                    new { GroupId = groupId }, transaction, cancellationToken: cancellationToken)))
                    values[LegacyMediaName(row.MediaType)] = row.Allowed ? "allowed" : "blocked";
            if (settingGroup == GroupSettingGroups.Characters)
                foreach (var row in await connection.QueryAsync<(string CharacterClass, string Action)>(new CommandDefinition(
                    "SELECT character_class AS CharacterClass, action AS Action FROM group_character_policies WHERE group_id = @GroupId",
                    new { GroupId = groupId }, transaction, cancellationToken: cancellationToken)))
                    values[row.CharacterClass] = LegacyAction(row.Action);
            if (settingGroup == GroupSettingGroups.TextLength)
            {
                var row = await connection.QuerySingleOrDefaultAsync<LengthRow>(new CommandDefinition(@"
SELECT enabled AS Enabled, maximum_characters AS MaximumCharacters, maximum_lines AS MaximumLines, action AS Action
FROM group_length_policies WHERE group_id = @GroupId AND scope = 'message'", new { GroupId = groupId },
                    transaction, cancellationToken: cancellationToken));
                if (row != null) { values["enabled"] = YesNo(row.Enabled); values["maxlength"] = row.MaximumCharacters.ToString(CultureInfo.InvariantCulture); values["maxlines"] = row.MaximumLines.ToString(CultureInfo.InvariantCulture); values["action"] = LegacyAction(row.Action); }
            }
            return values;
        }

        private static void AddScalarFields(Dictionary<string, string> values, string group, SettingsRow row)
        {
            if (group == GroupSettingGroups.Settings)
            {
                values["Flagged"] = YesNo(row.ReportsEnabled); values["Report"] = YesNo(row.ReportsEnabled);
                values["Welcome"] = YesNo(row.WelcomeEnabled); values["Flood"] = YesNo(row.FloodProtectionEnabled);
                values["DeleteLastWelcome"] = YesNo(row.DeleteLastWelcome); values["MuteOnJoin"] = YesNo(row.MuteOnJoin);
                values["Rules"] = YesNo(row.RulesPrivateOnly); values["About"] = YesNo(row.AboutPrivateOnly);
                values["Modlist"] = YesNo(row.ModlistPrivateOnly); values["Extra"] = YesNo(row.ExtraPrivateOnly);
                values["Help"] = YesNo(row.HelpPrivateOnly);
                if (row.LogTelegramChatId.HasValue) values["logchat"] = row.LogTelegramChatId.Value.ToString(CultureInfo.InvariantCulture);
            }
            else if (group == GroupSettingGroups.Flood) { values["MaxFlood"] = row.MaximumMessages.ToString(CultureInfo.InvariantCulture); values["ActionFlood"] = LegacyAction(row.FloodAction); }
            else if (group == GroupSettingGroups.Warn) { values["max"] = row.MaximumWarnings.ToString(CultureInfo.InvariantCulture); values["type"] = LegacyAction(row.WarningAction); values["mediamax"] = row.MediaWarningLimit.ToString(CultureInfo.InvariantCulture); }
            else if (group == GroupSettingGroups.Other) { values["tempbanTime"] = row.TempbanMinutes.ToString(CultureInfo.InvariantCulture); values["tempMuteTime"] = row.TempmuteMinutes.ToString(CultureInfo.InvariantCulture); }
            else if (group == GroupSettingGroups.Welcome) { if (row.WelcomeFormat != null) values["type"] = row.WelcomeFormat; if (row.WelcomeContent != null) values["content"] = row.WelcomeContent; if (row.WelcomeMediaFileId != null) { values["media"] = row.WelcomeMediaFileId; values["hasmedia"] = "true"; } }
            else if (group == GroupSettingGroups.Media) values["action"] = LegacyAction(row.MediaAction);
        }

        private static async Task<string> ApplyFieldAsync(DbConnection connection, DbTransaction transaction,
            long groupId, long telegramChatId, string group, string field, string value,
            CancellationToken cancellationToken)
        {
            string column = null; object converted = value;
            if (group == GroupSettingGroups.Settings)
            {
                column = field switch { "Flagged" or "Report" => "reports_enabled", "Welcome" => "welcome_enabled", "Flood" => "flood_protection_enabled", "DeleteLastWelcome" => "delete_last_welcome", "MuteOnJoin" => "mute_on_join", "Rules" => "rules_private_only", "About" => "about_private_only", "Modlist" => "modlist_private_only", "Extra" => "extra_private_only", "Help" => "help_private_only", _ => null };
                if (column != null) converted = IsEnabled(value);
                else if (field == "logchat" && long.TryParse(value, out var logChat))
                {
                    var logChatId = await PrincipalResolver.EnsureChatAsync(connection, transaction, logChat,
                        cancellationToken: cancellationToken);
                    await UpdateColumnAsync(connection, transaction, groupId, "log_chat_id", logChatId, cancellationToken);
                    return "group_settings.log_chat_id";
                }
            }
            else if (group == GroupSettingGroups.Flood) { column = field == "MaxFlood" ? "maximum_messages" : field == "ActionFlood" ? "flood_action" : null; converted = field == "MaxFlood" ? ParseShort(value) : NormalizeAction(value); }
            else if (group == GroupSettingGroups.Warn) { column = field == "max" ? "maximum_warnings" : field == "type" ? "warning_action" : field == "mediamax" ? "media_warning_limit" : null; converted = field is "max" or "mediamax" ? ParseShort(value) : NormalizeAction(value); }
            else if (group == GroupSettingGroups.Other) { column = field == "tempbanTime" ? "tempban_minutes" : field == "tempMuteTime" ? "tempmute_minutes" : null; converted = ParseInt(value); }
            else if (group == GroupSettingGroups.Welcome)
            {
                if (field == "media")
                {
                    await UpdateWelcomeMediaAsync(connection, transaction, groupId, value,
                        StorageRouting.GroupPostgresIsPrimary(groupId)
                            ? Operations.OperationsStore.CurrentEdition
                            : "legacy_unknown", cancellationToken);
                    return "group_settings.welcome_media_file_id";
                }
                column = field == "content" ? "welcome_content" :
                    field == "type" && value is "text" or "markdown" or "html" ? "welcome_format" : null;
            }
            else if (group == GroupSettingGroups.Media && field == "action") { column = "media_action"; converted = NormalizeAction(value); }
            if (column != null) { await UpdateColumnAsync(connection, transaction, groupId, column, converted, cancellationToken); return $"group_settings.{column}"; }

            if (group == GroupSettingGroups.FloodExceptions)
            {
                await UpsertPolicyAsync(connection, transaction, "group_flood_exceptions", "content_type", groupId,
                    NormalizeMedia(field), "excluded", !IsEnabled(value), cancellationToken); return "group_flood_exceptions";
            }
            if (group == GroupSettingGroups.Media && IsSupportedMedia(field))
            {
                await UpsertPolicyAsync(connection, transaction, "group_media_policies", "media_type", groupId,
                    NormalizeMedia(field), "allowed", value.Equals("allowed", StringComparison.OrdinalIgnoreCase), cancellationToken); return "group_media_policies";
            }
            if (group == GroupSettingGroups.Characters)
            {
                await UpsertPolicyAsync(connection, transaction, "group_character_policies", "character_class", groupId,
                    field, "action", NormalizeAction(value, true), cancellationToken); return "group_character_policies";
            }
            if (group == GroupSettingGroups.TextLength && field is "enabled" or "maxlength" or "maxlines" or "action")
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_length_policies (group_id, scope, enabled, maximum_characters, maximum_lines, action)
VALUES (@GroupId, 'message', FALSE, 0, 0, 'allow') ON CONFLICT (group_id, scope) DO NOTHING",
                    new { GroupId = groupId }, transaction, cancellationToken: cancellationToken));
                var lengthColumn = field switch { "enabled" => "enabled", "maxlength" => "maximum_characters", "maxlines" => "maximum_lines", _ => "action" };
                object lengthValue = field == "enabled" ? IsEnabled(value) :
                    field is "maxlength" or "maxlines" ? ParseInt(value) : NormalizeAction(value, true);
                await connection.ExecuteAsync(new CommandDefinition($"UPDATE group_length_policies SET {lengthColumn} = @Value WHERE group_id = @GroupId AND scope = 'message'",
                    new { GroupId = groupId, Value = lengthValue }, transaction, cancellationToken: cancellationToken));
                return $"group_length_policies.{lengthColumn}";
            }
            return null;
        }

        private static async Task ResetGroupAsync(DbConnection connection, DbTransaction transaction, long groupId,
            string group, CancellationToken cancellationToken)
        {
            if (group == GroupSettingGroups.FloodExceptions) await DeletePoliciesAsync(connection, transaction, "group_flood_exceptions", groupId, cancellationToken);
            else if (group == GroupSettingGroups.Media) { await DeletePoliciesAsync(connection, transaction, "group_media_policies", groupId, cancellationToken); await UpdateColumnAsync(connection, transaction, groupId, "media_action", "none", cancellationToken); }
            else if (group == GroupSettingGroups.Characters) await DeletePoliciesAsync(connection, transaction, "group_character_policies", groupId, cancellationToken);
            else if (group == GroupSettingGroups.TextLength) await connection.ExecuteAsync(new CommandDefinition("DELETE FROM group_length_policies WHERE group_id = @GroupId AND scope = 'message'", new { GroupId = groupId }, transaction, cancellationToken: cancellationToken));
            else if (group == GroupSettingGroups.NameLength) { }
            else foreach (var field in KnownFields(group)) await ResetFieldAsync(connection, transaction, groupId, group, field, cancellationToken);
        }

        private static async Task ResetFieldAsync(DbConnection connection, DbTransaction transaction, long groupId,
            string group, string field, CancellationToken cancellationToken)
        {
            var defaults = DefaultFor(group, field);
            if (group == GroupSettingGroups.Welcome && field == "media")
                await UpdateWelcomeMediaAsync(connection, transaction, groupId, null, null, cancellationToken);
            else if (defaults.Column != null)
                await UpdateColumnAsync(connection, transaction, groupId, defaults.Column, defaults.Value, cancellationToken);
            if (group == GroupSettingGroups.FloodExceptions) await DeletePolicyAsync(connection, transaction, "group_flood_exceptions", "content_type", groupId, NormalizeMedia(field), cancellationToken);
            if (group == GroupSettingGroups.Media && field != "action") await DeletePolicyAsync(connection, transaction, "group_media_policies", "media_type", groupId, NormalizeMedia(field), cancellationToken);
            if (group == GroupSettingGroups.Characters) await DeletePolicyAsync(connection, transaction, "group_character_policies", "character_class", groupId, field, cancellationToken);
            if (group == GroupSettingGroups.TextLength) await DeletePolicyAsync(connection, transaction, "group_length_policies", "scope", groupId, "message", cancellationToken);
        }

        private static async Task EnsureSettingsAsync(DbConnection connection, DbTransaction transaction, long groupId,
            CancellationToken cancellationToken) => await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_settings (group_id, reports_enabled, welcome_enabled, flood_protection_enabled,
delete_last_welcome, mute_on_join, maximum_messages, flood_action, maximum_warnings, warning_action,
media_warning_limit, media_action, tempban_minutes, tempmute_minutes, rules_private_only,
about_private_only, modlist_private_only, extra_private_only, help_private_only, version)
VALUES (@GroupId, FALSE, FALSE, FALSE, FALSE, FALSE, 0, 'none', 0, 'none', 0, 'none', 0, 0,
FALSE, FALSE, FALSE, FALSE, FALSE, 1) ON CONFLICT (group_id) DO NOTHING",
                new { GroupId = groupId }, transaction, cancellationToken: cancellationToken));

        private static Task UpdateColumnAsync(DbConnection connection, DbTransaction transaction, long groupId,
            string column, object value, CancellationToken cancellationToken) => connection.ExecuteAsync(
                new CommandDefinition($"UPDATE group_settings SET {column} = @Value WHERE group_id = @GroupId",
                    new { GroupId = groupId, Value = value }, transaction, cancellationToken: cancellationToken));

        private static Task UpdateWelcomeMediaAsync(DbConnection connection, DbTransaction transaction,
            long groupId, string fileId, string ownerEdition, CancellationToken cancellationToken) =>
            connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_settings SET welcome_media_file_id = @FileId,
welcome_media_file_owner_edition = @OwnerEdition WHERE group_id = @GroupId",
                new { GroupId = groupId, FileId = fileId, OwnerEdition = ownerEdition }, transaction,
                cancellationToken: cancellationToken));

        private static Task UpsertPolicyAsync(DbConnection connection, DbTransaction transaction, string table,
            string keyColumn, long groupId, string key, string valueColumn, object value,
            CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO {table} (group_id, {keyColumn}, {valueColumn}) VALUES (@GroupId, @Key, @Value) ON CONFLICT (group_id, {keyColumn}) DO UPDATE SET {valueColumn} = EXCLUDED.{valueColumn}",
                new { GroupId = groupId, Key = key, Value = value }, transaction, cancellationToken: cancellationToken));

        private static Task DeletePoliciesAsync(DbConnection c, DbTransaction t, string table, long groupId,
            CancellationToken token) => c.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE group_id = @GroupId", new { GroupId = groupId }, t, cancellationToken: token));
        private static Task DeletePolicyAsync(DbConnection c, DbTransaction t, string table, string keyColumn,
            long groupId, string key, CancellationToken token) => c.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE group_id = @GroupId AND {keyColumn} = @Key", new { GroupId = groupId, Key = key }, t, cancellationToken: token));

        private static Task PreserveSourceAsync(DbConnection connection, DbTransaction transaction, string sourceKey,
            string field, string value, string disposition, string target, CancellationToken cancellationToken) =>
            connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO legacy_redis_records (source_key, source_field, source_type, raw_value, source_checksum,
captured_at, disposition, target_reference) VALUES (@SourceKey, @Field, 'hash', @Value, @Checksum, now(), @Disposition, @Target)
ON CONFLICT (source_key, source_field) DO UPDATE SET raw_value = EXCLUDED.raw_value,
source_checksum = EXCLUDED.source_checksum, captured_at = EXCLUDED.captured_at,
disposition = EXCLUDED.disposition, target_reference = EXCLUDED.target_reference",
                new { SourceKey = sourceKey, Field = field, Value = value, Checksum = Checksum(value), Disposition = disposition, Target = target }, transaction, cancellationToken: cancellationToken));

        private static Task MarkRawDeletedAsync(DbConnection connection, DbTransaction transaction, string sourceKey,
            string field, CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition(@"
UPDATE legacy_redis_records SET disposition = 'excluded', captured_at = now()
WHERE source_key = @SourceKey AND source_field = @Field", new { SourceKey = sourceKey, Field = field }, transaction,
                cancellationToken: cancellationToken));

        private static (string Column, object Value) DefaultFor(string group, string field) => (group, field) switch
        {
            ("settings", "Flagged" or "Report") => ("reports_enabled", false), ("settings", "Welcome") => ("welcome_enabled", false),
            ("settings", "Flood") => ("flood_protection_enabled", false), ("settings", "DeleteLastWelcome") => ("delete_last_welcome", false),
            ("settings", "MuteOnJoin") => ("mute_on_join", false), ("settings", "Rules") => ("rules_private_only", false),
            ("settings", "About") => ("about_private_only", false), ("settings", "Modlist") => ("modlist_private_only", false),
            ("settings", "Extra") => ("extra_private_only", false), ("settings", "Help") => ("help_private_only", false),
            ("settings", "logchat") => ("log_chat_id", null), ("flood", "MaxFlood") => ("maximum_messages", (short)0),
            ("flood", "ActionFlood") => ("flood_action", "none"), ("warnsettings", "max") => ("maximum_warnings", (short)0),
            ("warnsettings", "type") => ("warning_action", "none"), ("warnsettings", "mediamax") => ("media_warning_limit", (short)0),
            ("otherSettings", "tempbanTime") => ("tempban_minutes", 0), ("otherSettings", "tempMuteTime") => ("tempmute_minutes", 0),
            ("welcome", "type") => ("welcome_format", null), ("welcome", "content") => ("welcome_content", null),
            ("welcome", "media") => ("welcome_media_file_id", null), ("media", "action") => ("media_action", "none"), _ => (null, null)
        };

        private static IEnumerable<string> KnownFields(string group) => group switch
        {
            "settings" => new[] { "Flagged", "Report", "Welcome", "Flood", "DeleteLastWelcome", "MuteOnJoin", "Rules", "About", "Modlist", "Extra", "Help", "logchat" },
            "flood" => new[] { "MaxFlood", "ActionFlood" }, "warnsettings" => new[] { "max", "type", "mediamax" },
            "otherSettings" => new[] { "tempbanTime", "tempMuteTime" }, "welcome" => new[] { "type", "content", "media" },
            _ => Array.Empty<string>()
        };

        private static string RedisKey(long chatId, string group) => $"chat:{chatId}:{group}";
        private static bool IsEnabled(string value) => value is "yes" or "true" or "1" or "allowed";
        private static string YesNo(bool value) => value ? "yes" : "no";
        private static short ParseShort(string value) => short.TryParse(value, out var result) && result >= 0 ? result : (short)0;
        private static int ParseInt(string value) => int.TryParse(value, out var result) && result >= 0 ? result : 0;
        private static string NormalizeAction(string value, bool allow = false) => value?.ToLowerInvariant() switch
        { "warn" => "warn", "kick" => "kick", "ban" => "ban", "mute" => "mute", "tempban" => "tempban", "tempmute" => "tempmute", "delete" => "delete", "allowed" or "allow" when allow => "allow", _ => allow ? "allow" : "none" };
        private static string LegacyAction(string value) => value == "allow" ? "allowed" : value;
        private static string NormalizeMedia(string field) => field?.ToLowerInvariant() switch { "image" => "photo", "gif" => "animation", "file" => "document", _ => field?.ToLowerInvariant() };
        private static string LegacyMediaName(string field) => field switch { "photo" => "image", "animation" => "gif", "document" => "file", _ => field };
        private static bool IsSupportedMedia(string field) => new[] { "animation", "audio", "contact", "document", "game", "location", "photo", "poll", "sticker", "video", "video_note", "voice" }.Contains(NormalizeMedia(field));
        private static string Checksum(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
        private static long? ParseRevision(string target) { const string token = ";revision:"; var index = target?.IndexOf(token, StringComparison.Ordinal) ?? -1; return index < 0 ? null : long.TryParse(target.Substring(index + token.Length), out var value) ? value : null; }

        private sealed class SourceMarker { public string SourceChecksum { get; set; } public string Disposition { get; set; } public string TargetReference { get; set; } }
        private sealed class LengthRow { public bool Enabled { get; set; } public int MaximumCharacters { get; set; } public int MaximumLines { get; set; } public string Action { get; set; } }
        private sealed class SettingsRow
        {
            public long? LogTelegramChatId { get; set; } public bool ReportsEnabled { get; set; } public bool WelcomeEnabled { get; set; }
            public bool FloodProtectionEnabled { get; set; } public bool DeleteLastWelcome { get; set; } public bool MuteOnJoin { get; set; }
            public short MaximumMessages { get; set; } public string FloodAction { get; set; } public short MaximumWarnings { get; set; }
            public string WarningAction { get; set; } public short MediaWarningLimit { get; set; } public string MediaAction { get; set; }
            public int TempbanMinutes { get; set; } public int TempmuteMinutes { get; set; } public string WelcomeFormat { get; set; }
            public string WelcomeContent { get; set; } public string WelcomeMediaFileId { get; set; } public bool RulesPrivateOnly { get; set; }
            public bool AboutPrivateOnly { get; set; } public bool ModlistPrivateOnly { get; set; } public bool ExtraPrivateOnly { get; set; }
            public bool HelpPrivateOnly { get; set; }
        }
    }
}
