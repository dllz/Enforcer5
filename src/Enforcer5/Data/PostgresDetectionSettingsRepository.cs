using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Enforcer5.Models;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// A group's <see cref="DetectionPolicy"/> in PostgreSQL:
    /// <list type="bullet">
    /// <item><c>group_settings</c>: max warns and their action, the tempban and tempmute lengths,
    /// and the version every change of the policy bumps. Its row is what says the group was
    /// imported.</item>
    /// <item><c>detection_policies</c>: one row per check (<c>flood</c>, <c>media</c>,
    /// <c>mute_on_join</c>, <c>channel_posts</c>) with its thresholds and lists in <c>settings</c>.</item>
    /// <item><c>group_filters</c>: the Arabic and right-to-left presets, the text and name length
    /// rules, and the blocked inline bots and sticker packs, one row each.</item>
    /// </list>
    /// A save rewrites the rows that changed and keeps the others, so a filter keeps its id.
    /// </summary>
    internal sealed class PostgresDetectionSettingsRepository : IDetectionSettingsRepository
    {
        internal const string Flood = "flood";
        internal const string Media = "media";
        internal const string MuteOnJoin = "mute_on_join";
        internal const string ChannelPosts = "channel_posts";

        internal const string Arabic = "arabic";
        internal const string RightToLeft = "rtl";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private readonly Func<PostgresDatabase> _database;

        internal PostgresDetectionSettingsRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        public async Task<DetectionPolicy> LoadAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await LoadAsync(connection, null, chatId);
        }

        public async Task<DetectionPolicy> ImportAsync(long chatId, DetectionPolicy policy)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // Most groups are in groups already (the principal flush adds them once a minute), and
            // then the import touches only its own rows. Only a group missing there takes the lock
            // every other writer of groups and accounts takes: right after the deploy every active
            // group imports at once, and they must not all queue on one lock.
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId, transaction);
            if (groupId == null)
            {
                await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                    new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
                groupId = (await PrincipalResolver.EnsureGroupAsync(connection, transaction, chatId, chatType: "unknown")).GroupId;
            }
            var version = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
INSERT INTO group_settings (group_id, maximum_warnings, warning_action, tempban_minutes, tempmute_minutes, version, updated_at)
VALUES (@GroupId, @MaximumWarnings, @WarningAction, @TempbanMinutes, @TempmuteMinutes, 1, now())
ON CONFLICT (group_id) DO NOTHING
RETURNING version", GroupSettingsParameters(groupId.Value, policy), transaction));
            if (version == null)
            {
                // Imported by someone else first: theirs stands.
                var existing = await LoadAsync(connection, transaction, chatId);
                await transaction.CommitAsync();
                return existing;
            }

            await WriteChecksAsync(connection, transaction, groupId.Value, policy);
            await transaction.CommitAsync();
            return policy with { Version = version.Value };
        }

        public async Task<DetectionPolicy> SaveAsync(long chatId, DetectionPolicy policy, long expectedVersion)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId, transaction);
            if (groupId == null) return null;
            var parameters = GroupSettingsParameters(groupId.Value, policy);
            parameters.Add("Expected", expectedVersion);
            var version = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
UPDATE group_settings SET maximum_warnings = @MaximumWarnings, warning_action = @WarningAction,
    tempban_minutes = @TempbanMinutes, tempmute_minutes = @TempmuteMinutes,
    version = version + 1, updated_at = now()
WHERE group_id = @GroupId AND version = @Expected
RETURNING version", parameters, transaction));
            if (version == null) return null;

            await WriteChecksAsync(connection, transaction, groupId.Value, policy);
            await transaction.CommitAsync();
            return policy with { Version = version.Value };
        }

        private static DynamicParameters GroupSettingsParameters(long groupId, DetectionPolicy policy)
        {
            var parameters = new DynamicParameters();
            parameters.Add("GroupId", groupId);
            parameters.Add("MaximumWarnings", (short)Math.Clamp(policy.Warns.MaxWarns, 1, short.MaxValue));
            parameters.Add("WarningAction", policy.Warns.Action == WarnSettings.Ban ? WarnSettings.Ban : WarnSettings.Kick);
            parameters.Add("TempbanMinutes", Math.Max(1, policy.TempbanMinutes));
            parameters.Add("TempmuteMinutes", Math.Max(1, policy.TempmuteMinutes));
            return parameters;
        }

        private static async Task WriteChecksAsync(DbConnection connection, DbTransaction transaction, long groupId,
            DetectionPolicy policy)
        {
            async Task Check(string key, bool enabled, string action, object settings) =>
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO detection_policies (group_id, check_key, enabled, action, settings, updated_at)
VALUES (@GroupId, @Key, @Enabled, @Action, CAST(@Settings AS jsonb), now())
ON CONFLICT (group_id, check_key) DO UPDATE SET enabled = EXCLUDED.enabled, action = EXCLUDED.action,
    settings = EXCLUDED.settings, updated_at = EXCLUDED.updated_at
WHERE detection_policies.enabled IS DISTINCT FROM EXCLUDED.enabled
   OR detection_policies.action IS DISTINCT FROM EXCLUDED.action
   OR detection_policies.settings IS DISTINCT FROM EXCLUDED.settings",
                    new { GroupId = groupId, Key = key, Enabled = enabled, Action = action, Settings = JsonSerializer.Serialize(settings, Json) },
                    transaction));

            await Check(Flood, policy.Flood.Enabled, policy.Flood.Action,
                new FloodJson { MaxMessages = policy.Flood.MaxMessages, Counted = new Dictionary<string, bool>(policy.Flood.Counted) });
            await Check(Media, true, policy.Media.Action,
                new MediaJson { Blocked = new Dictionary<string, bool>(policy.Media.Blocked), WarnLimit = policy.Media.WarnLimit });
            await Check(MuteOnJoin, policy.MuteOnJoin, null, new { });
            await Check(ChannelPosts, policy.ChannelPosts.Enabled, null, new ChannelPostsJson
            {
                Allowed = policy.ChannelPosts.Allowed.Select(c => new ChannelJson { Id = c.Id, Title = c.Title }).ToList()
            });

            async Task Preset(string preset, string action) =>
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_filters (group_id, kind, preset, enabled, action, mode, created_at)
VALUES (@GroupId, 'preset', @Preset, @Enabled, @Action, 'live', now())
ON CONFLICT (group_id, preset) WHERE preset IS NOT NULL
DO UPDATE SET enabled = EXCLUDED.enabled, action = EXCLUDED.action",
                    new { GroupId = groupId, Preset = preset, Enabled = action != null, Action = action }, transaction));
            await Preset(Arabic, policy.Scripts.Arabic);
            await Preset(RightToLeft, policy.Scripts.Rtl);

            async Task Length(string target, LengthSettings length) =>
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_filters (group_id, target, kind, enabled, max_characters, max_lines, action, mode, created_at)
VALUES (@GroupId, @Target, 'length', @Enabled, @MaxCharacters, @MaxLines, @Action, 'live', now())
ON CONFLICT (group_id, target) WHERE kind = 'length'
DO UPDATE SET enabled = EXCLUDED.enabled, max_characters = EXCLUDED.max_characters,
    max_lines = EXCLUDED.max_lines, action = EXCLUDED.action",
                    new
                    {
                        GroupId = groupId, Target = target, length.Enabled, length.MaxCharacters,
                        length.MaxLines, length.Action
                    }, transaction));
            await Length("text", policy.TextLength);
            await Length("name", policy.NameLength);

            async Task List(string target, string[] kinds, string[] patterns)
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM group_filters f
WHERE f.group_id = @GroupId AND f.target = @Target
  AND NOT EXISTS (SELECT 1 FROM unnest(@Kinds, @Patterns) AS n(kind, pattern)
                  WHERE n.kind = f.kind AND n.pattern = f.pattern)",
                    new { GroupId = groupId, Target = target, Kinds = kinds, Patterns = patterns }, transaction));
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_filters (group_id, target, kind, pattern, enabled, mode, created_at)
SELECT @GroupId, @Target, n.kind, n.pattern, TRUE, 'live', now()
FROM unnest(@Kinds, @Patterns) WITH ORDINALITY AS n(kind, pattern, position)
ORDER BY n.position
ON CONFLICT (group_id, target, kind, pattern) WHERE pattern IS NOT NULL DO NOTHING",
                    new { GroupId = groupId, Target = target, Kinds = kinds, Patterns = patterns }, transaction));
            }

            await List("inline_bot",
                policy.InlineBots.Select(b => b.Kind == InlineBotBlockKind.Pattern ? "regex" : "username").ToArray(),
                policy.InlineBots.Select(b => b.Value).ToArray());
            await List("sticker_set", policy.StickerSets.Select(_ => "exact").ToArray(), policy.StickerSets.ToArray());
        }

        private static async Task<DetectionPolicy> LoadAsync(DbConnection connection, DbTransaction transaction, long chatId)
        {
            var group = await connection.QuerySingleOrDefaultAsync<GroupRow>(new CommandDefinition(@"
SELECT gs.group_id AS GroupId, gs.version AS Version, gs.maximum_warnings AS MaximumWarnings,
       gs.warning_action AS WarningAction, gs.tempban_minutes AS TempbanMinutes, gs.tempmute_minutes AS TempmuteMinutes
FROM group_settings gs
JOIN groups g ON g.group_id = gs.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId", new { ChatId = chatId }, transaction));
            if (group == null) return null;

            var checks = (await connection.QueryAsync<CheckRow>(new CommandDefinition(@"
SELECT check_key AS CheckKey, enabled AS Enabled, action AS Action, settings::text AS Settings
FROM detection_policies WHERE group_id = @GroupId", new { group.GroupId }, transaction)))
                .ToDictionary(r => r.CheckKey);
            var filters = (await connection.QueryAsync<FilterRow>(new CommandDefinition(@"
SELECT target AS Target, kind AS Kind, preset AS Preset, pattern AS Pattern, max_characters AS MaxCharacters,
       max_lines AS MaxLines, action AS Action, enabled AS Enabled
FROM group_filters WHERE group_id = @GroupId AND mode = 'live'
ORDER BY filter_id", new { group.GroupId }, transaction))).ToList();

            var empty = DetectionPolicy.Empty;
            T Settings<T>(string key) where T : new() =>
                checks.TryGetValue(key, out var row) && row.Settings != null
                    ? JsonSerializer.Deserialize<T>(row.Settings, Json) ?? new T()
                    : new T();

            var flood = Settings<FloodJson>(Flood);
            var media = Settings<MediaJson>(Media);
            var channels = Settings<ChannelPostsJson>(ChannelPosts);
            string Preset(string preset) =>
                filters.FirstOrDefault(f => f.Kind == "preset" && f.Preset == preset) is { Enabled: true } row ? row.Action : null;
            LengthSettings Length(string target, LengthSettings fallback) =>
                filters.FirstOrDefault(f => f.Kind == "length" && f.Target == target) is { } row
                    ? new LengthSettings(row.Enabled, row.MaxCharacters, row.MaxLines, row.Action)
                    : fallback;

            return new DetectionPolicy(
                Version: group.Version,
                Flood: checks.TryGetValue(Flood, out var floodRow)
                    ? new FloodSettings(floodRow.Enabled, flood.MaxMessages, floodRow.Action, flood.Counted ?? new Dictionary<string, bool>())
                    : empty.Flood,
                Media: checks.TryGetValue(Media, out var mediaRow)
                    ? new MediaSettings(media.Blocked ?? new Dictionary<string, bool>(), mediaRow.Action, media.WarnLimit)
                    : empty.Media,
                MuteOnJoin: checks.TryGetValue(MuteOnJoin, out var muteRow) && muteRow.Enabled,
                Scripts: new ScriptSettings(Preset(Arabic), Preset(RightToLeft)),
                TextLength: Length("text", empty.TextLength),
                NameLength: Length("name", empty.NameLength),
                Warns: new WarnSettings(group.MaximumWarnings,
                    group.WarningAction == WarnSettings.Ban ? WarnSettings.Ban : WarnSettings.Kick),
                TempbanMinutes: group.TempbanMinutes,
                TempmuteMinutes: group.TempmuteMinutes,
                InlineBots: filters.Where(f => f.Target == "inline_bot" && f.Enabled && f.Pattern != null)
                    .Select(f => new InlineBotBlock(f.Kind == "regex" ? InlineBotBlockKind.Pattern : InlineBotBlockKind.Username, f.Pattern))
                    .ToList(),
                StickerSets: filters.Where(f => f.Target == "sticker_set" && f.Enabled && f.Pattern != null)
                    .Select(f => f.Pattern).ToList(),
                ChannelPosts: new ChannelPostSettings(
                    checks.TryGetValue(ChannelPosts, out var channelRow) && channelRow.Enabled,
                    (channels.Allowed ?? new List<ChannelJson>()).Select(c => new AllowedChannel(c.Id, c.Title)).ToList()));
        }

        private sealed class FloodJson
        {
            public int? MaxMessages { get; set; }
            public Dictionary<string, bool> Counted { get; set; }
        }

        private sealed class MediaJson
        {
            public Dictionary<string, bool> Blocked { get; set; }
            public int? WarnLimit { get; set; }
        }

        private sealed class ChannelPostsJson
        {
            public List<ChannelJson> Allowed { get; set; }
        }

        private sealed class ChannelJson
        {
            public long Id { get; set; }
            public string Title { get; set; }
        }

        private sealed class GroupRow
        {
            public long GroupId { get; set; }
            public long Version { get; set; }
            public short MaximumWarnings { get; set; }
            public string WarningAction { get; set; }
            public int TempbanMinutes { get; set; }
            public int TempmuteMinutes { get; set; }
        }

        private sealed class CheckRow
        {
            public string CheckKey { get; set; }
            public bool Enabled { get; set; }
            public string Action { get; set; }
            public string Settings { get; set; }
        }

        private sealed class FilterRow
        {
            public string Target { get; set; }
            public string Kind { get; set; }
            public string Preset { get; set; }
            public string Pattern { get; set; }
            public int? MaxCharacters { get; set; }
            public int? MaxLines { get; set; }
            public string Action { get; set; }
            public bool Enabled { get; set; }
        }
    }
}
