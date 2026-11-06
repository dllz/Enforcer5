using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>
    /// A group's <see cref="GroupConfig"/> in PostgreSQL (rollout step 15): the settings and welcome in
    /// <c>group_settings</c> (with <c>config_version</c> and <c>config_imported_at</c>, since the row itself is the
    /// detection settings' import marker), the texts in <c>group_content_items</c>, the extras in <c>custom_commands</c>.
    /// </summary>
    internal sealed class PostgresGroupConfigRepository
    {
        private readonly Func<PostgresDatabase> _database;
        private readonly string _edition;

        internal PostgresGroupConfigRepository(Func<PostgresDatabase> database, string edition)
        {
            _database = database;
            _edition = edition;
        }

        private PostgresDatabase Database =>
            _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        private static readonly (ConfigFlag Flag, string Column)[] FlagColumns =
        {
            (ConfigFlag.Welcome, "welcome_enabled"), (ConfigFlag.DeleteLastWelcome, "delete_last_welcome"),
            (ConfigFlag.Reports, "reports_enabled"), (ConfigFlag.RulesPrivate, "rules_private_only"),
            (ConfigFlag.AboutPrivate, "about_private_only"), (ConfigFlag.ModlistPrivate, "modlist_private_only"),
            (ConfigFlag.ExtraPrivate, "extra_private_only"), (ConfigFlag.HelpPrivate, "help_private_only")
        };

        private static string Column(ConfigFlag flag) => FlagColumns.Single(f => f.Flag == flag).Column;

        private static string ContentValue(ContentKind kind) => kind.ToString().ToLowerInvariant();

        /// <summary>The bot's media names to the design's (<c>gif</c> is <c>animation</c>, ...), and back.</summary>
        private static readonly (string Ours, string Stored)[] MediaTypes =
        {
            ("gif", "animation"), ("file", "document"), ("videoNote", "video_note"), ("photo", "photo"), ("voice", "voice"),
            ("sticker", "sticker"), ("video", "video"), ("audio", "audio")
        };

        internal static string StoredMediaType(string ours) =>
            ours == null ? null : MediaTypes.FirstOrDefault(m => m.Ours == ours).Stored ?? "document";

        internal static string OurMediaType(string stored) =>
            stored == null ? null : MediaTypes.FirstOrDefault(m => m.Stored == stored).Ours ?? "file";

        /// <summary>The group's config, or null when it was never imported.</summary>
        internal async Task<GroupConfig> LoadAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            return await LoadAsync(connection, null, chatId);
        }

        private static async Task<GroupConfig> LoadAsync(DbConnection connection, DbTransaction transaction, long chatId)
        {
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId, transaction);
            if (groupId == null) return null;
            var row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(new CommandDefinition($@"
SELECT {string.Join(", ", FlagColumns.Select(f => $"s.{f.Column} AS {f.Flag}"))},
       lc.telegram_chat_id AS LogChat, s.welcome_type AS WelcomeType, s.welcome_content AS WelcomeContent,
       s.welcome_media_file_id AS WelcomeFile, s.welcome_media_file_owner_edition AS WelcomeEdition, s.config_version AS Version
FROM group_settings s LEFT JOIN telegram_chats lc ON lc.chat_id = s.log_chat_id
WHERE s.group_id = @GroupId AND s.config_imported_at IS NOT NULL", new { GroupId = groupId }, transaction));
            if (row == null) return null;
            var content = await connection.QueryAsync<(string Type, string Content)>(new CommandDefinition(
                "SELECT content_type, content FROM group_content_items WHERE group_id = @GroupId", new { GroupId = groupId }, transaction));
            var extras = await connection.QueryAsync<ExtraRow>(new CommandDefinition(@"
SELECT trigger AS Trigger, content AS Content, media_type AS MediaType, file_id AS FileId, file_owner_edition AS Edition,
       caption AS Caption FROM custom_commands WHERE group_id = @GroupId", new { GroupId = groupId }, transaction));

            var flags = new Dictionary<ConfigFlag, bool>
            {
                [ConfigFlag.Welcome] = row.Welcome, [ConfigFlag.DeleteLastWelcome] = row.DeleteLastWelcome,
                [ConfigFlag.Reports] = row.Reports, [ConfigFlag.RulesPrivate] = row.RulesPrivate,
                [ConfigFlag.AboutPrivate] = row.AboutPrivate, [ConfigFlag.ModlistPrivate] = row.ModlistPrivate,
                [ConfigFlag.ExtraPrivate] = row.ExtraPrivate, [ConfigFlag.HelpPrivate] = row.HelpPrivate
            };
            var texts = new Dictionary<ContentKind, string>();
            foreach (var (type, text) in content)
                if (Enum.TryParse<ContentKind>(type, true, out var kind)) texts[kind] = text;
            var extraMap = ImmutableSortedDictionary<string, ExtraConfig>.Empty.WithComparers(StringComparer.Ordinal).ToBuilder();
            foreach (var e in extras)
                extraMap[e.Trigger] = new ExtraConfig(e.Content, OurMediaType(e.MediaType), e.FileId, e.Edition, e.Caption);
            return new GroupConfig
            {
                Flags = flags.ToImmutableDictionary(),
                Welcome = new WelcomeConfig(row.WelcomeType, row.WelcomeContent, row.WelcomeFile, row.WelcomeEdition),
                LogChat = row.LogChat,
                Content = texts.ToImmutableDictionary(),
                Extras = extraMap.ToImmutable(),
                Version = row.Version
            };
        }

        /// <summary>
        /// Stores <paramref name="config"/> as the group's first config. The group's <c>group_settings</c> row must exist
        /// (its detection settings imported first). When another process imported first, theirs is returned.
        /// </summary>
        internal async Task<GroupConfig> ImportAsync(long chatId, GroupConfig config)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId, transaction)
                          ?? throw new InvalidOperationException($"{chatId} has no group row; its detection settings come first");
            var claimed = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE group_settings SET config_imported_at = now(), config_version = 1
WHERE group_id = @GroupId AND config_imported_at IS NULL", new { GroupId = groupId }, transaction));
            if (claimed == 0)
            {
                var theirs = await LoadAsync(connection, transaction, chatId);
                await transaction.CommitAsync();
                return theirs ?? throw new InvalidOperationException($"{chatId} has no group_settings row; its detection settings come first");
            }
            foreach (var flag in Enum.GetValues<ConfigFlag>())
                await WriteAsync(connection, transaction, groupId, ConfigChange.SetFlag(flag, config.Flag(flag)));
            await WriteAsync(connection, transaction, groupId, ConfigChange.SetWelcome(config.Welcome));
            await WriteAsync(connection, transaction, groupId, ConfigChange.SetLogChat(config.LogChat));
            foreach (var (kind, text) in config.Content)
                await WriteAsync(connection, transaction, groupId, ConfigChange.SetContent(kind, text));
            foreach (var (trigger, extra) in config.Extras)
                await WriteAsync(connection, transaction, groupId, ConfigChange.SetExtra(trigger, extra));
            var imported = await LoadAsync(connection, transaction, chatId);
            await transaction.CommitAsync();
            return imported;
        }

        /// <summary>Makes the change and bumps the version; returns the new version. The group must be imported.</summary>
        internal async Task<long> ApplyAsync(long chatId, ConfigChange change)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var groupId = await PrincipalResolver.ResolveGroupIdAsync(connection, chatId, transaction)
                          ?? throw new InvalidOperationException($"{chatId} has no group row");
            var version = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
UPDATE group_settings SET config_version = config_version + 1, updated_at = now()
WHERE group_id = @GroupId AND config_imported_at IS NOT NULL RETURNING config_version", new { GroupId = groupId }, transaction))
                          ?? throw new InvalidOperationException($"The config of {chatId} was not imported");
            await WriteAsync(connection, transaction, groupId, change);
            await transaction.CommitAsync();
            return version;
        }

        private async Task WriteAsync(DbConnection connection, DbTransaction transaction, long groupId, ConfigChange change)
        {
            CommandDefinition Sql(string sql, object args) => new(sql, args, transaction);
            switch (change.Kind)
            {
                case "flag":
                    await connection.ExecuteAsync(Sql($"UPDATE group_settings SET {Column(change.Flag)} = @On WHERE group_id = @GroupId",
                        new { change.On, GroupId = groupId }));
                    break;
                case "reset":
                    foreach (var flag in Enum.GetValues<ConfigFlag>())
                        await WriteAsync(connection, transaction, groupId, ConfigChange.SetFlag(flag, GroupConfig.Defaults[flag]));
                    await WriteAsync(connection, transaction, groupId, ConfigChange.SetWelcome(WelcomeConfig.Default));
                    break;
                case "welcome":
                    var welcome = change.Welcome ?? WelcomeConfig.Default;
                    await connection.ExecuteAsync(Sql(@"
UPDATE group_settings SET welcome_type = @Type, welcome_content = @Content, welcome_media_file_id = @File,
    welcome_media_file_owner_edition = @Edition WHERE group_id = @GroupId",
                        new
                        {
                            welcome.Type, welcome.Content, File = welcome.MediaFileId,
                            Edition = welcome.MediaFileId == null ? null : welcome.MediaFileEdition ?? _edition, GroupId = groupId
                        }));
                    break;
                case "logchat":
                    long? logChat = null;
                    if (change.LogChat is { } telegramChat)
                        logChat = await PrincipalResolver.EnsureChatAsync(connection, transaction, telegramChat,
                            PostgresBanHistoryRepository.ChatType(telegramChat));
                    await connection.ExecuteAsync(Sql("UPDATE group_settings SET log_chat_id = @LogChat WHERE group_id = @GroupId",
                        new { LogChat = logChat, GroupId = groupId }));
                    break;
                case "content":
                    await connection.ExecuteAsync(Sql(change.Text == null
                            ? "DELETE FROM group_content_items WHERE group_id = @GroupId AND content_type = @Type"
                            : @"INSERT INTO group_content_items (group_id, content_type, content) VALUES (@GroupId, @Type, @Text)
                                ON CONFLICT (group_id, content_type) DO UPDATE SET content = EXCLUDED.content",
                        new { GroupId = groupId, Type = ContentValue(change.Content), change.Text }));
                    break;
                case "extra":
                    var extra = change.Extra;
                    await connection.ExecuteAsync(Sql(extra == null
                            ? "DELETE FROM custom_commands WHERE group_id = @GroupId AND trigger = @Trigger"
                            : @"INSERT INTO custom_commands (group_id, trigger, content, media_type, file_id, file_owner_edition, caption)
                                VALUES (@GroupId, @Trigger, @Content, @MediaType, @FileId, @Edition, @Caption)
                                ON CONFLICT (group_id, trigger) DO UPDATE SET content = EXCLUDED.content, media_type = EXCLUDED.media_type,
                                    file_id = EXCLUDED.file_id, file_owner_edition = EXCLUDED.file_owner_edition, caption = EXCLUDED.caption",
                        new
                        {
                            GroupId = groupId, change.Trigger, Content = extra?.Text,
                            MediaType = extra?.FileId == null ? null : StoredMediaType(extra.MediaType), extra?.FileId,
                            Edition = extra?.FileId == null ? null : extra.FileEdition ?? _edition, extra?.Caption
                        }));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change), change.Kind, null);
            }
        }

        private sealed class SettingsRow
        {
            public bool Welcome { get; set; }
            public bool DeleteLastWelcome { get; set; }
            public bool Reports { get; set; }
            public bool RulesPrivate { get; set; }
            public bool AboutPrivate { get; set; }
            public bool ModlistPrivate { get; set; }
            public bool ExtraPrivate { get; set; }
            public bool HelpPrivate { get; set; }
            public long? LogChat { get; set; }
            public string WelcomeType { get; set; }
            public string WelcomeContent { get; set; }
            public string WelcomeFile { get; set; }
            public string WelcomeEdition { get; set; }
            public long Version { get; set; }
        }

        private sealed class ExtraRow
        {
            public string Trigger { get; set; }
            public string Content { get; set; }
            public string MediaType { get; set; }
            public string FileId { get; set; }
            public string Edition { get; set; }
            public string Caption { get; set; }
        }
    }
}
