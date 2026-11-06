using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GroupConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "about_private_only",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "config_imported_at",
                table: "group_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "config_version",
                table: "group_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<bool>(
                name: "delete_last_welcome",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "extra_private_only",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "help_private_only",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "log_chat_id",
                table: "group_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "modlist_private_only",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "reports_enabled",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "rules_private_only",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "welcome_content",
                table: "group_settings",
                type: "text",
                nullable: true,
                defaultValue: "no");

            migrationBuilder.AddColumn<bool>(
                name: "welcome_enabled",
                table: "group_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "welcome_media_file_id",
                table: "group_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "welcome_media_file_owner_edition",
                table: "group_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "welcome_type",
                table: "group_settings",
                type: "text",
                nullable: false,
                defaultValue: "composed");

            migrationBuilder.CreateTable(
                name: "custom_commands",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    media_type = table.Column<string>(type: "text", nullable: true),
                    file_id = table.Column<string>(type: "text", nullable: true),
                    file_owner_edition = table.Column<string>(type: "text", nullable: true),
                    caption = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_commands", x => new { x.group_id, x.trigger });
                    table.CheckConstraint("custom_commands_body_check", "content IS NOT NULL OR file_id IS NOT NULL");
                    table.CheckConstraint("custom_commands_edition_check", "file_owner_edition IS NULL OR file_owner_edition IN ('normal', 'premium', 'legacy_unknown')");
                    table.CheckConstraint("custom_commands_file_check", "(file_id IS NULL) = (file_owner_edition IS NULL) AND (file_id IS NULL) = (media_type IS NULL)");
                    table.CheckConstraint("custom_commands_media_type_check", "media_type IS NULL OR media_type IN ('animation', 'audio', 'document', 'photo', 'sticker', 'video', 'video_note', 'voice')");
                    table.ForeignKey(
                        name: "FK_custom_commands_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_content_items",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_content_items", x => new { x.group_id, x.content_type });
                    table.CheckConstraint("group_content_items_type_check", "content_type IN ('rules', 'about', 'language', 'link')");
                    table.ForeignKey(
                        name: "FK_group_content_items_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_group_settings_log_chat_id",
                table: "group_settings",
                column: "log_chat_id");

            migrationBuilder.AddCheckConstraint(
                name: "group_settings_config_version_check",
                table: "group_settings",
                sql: "config_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "group_settings_welcome_media_check",
                table: "group_settings",
                sql: "(welcome_media_file_id IS NULL) = (welcome_media_file_owner_edition IS NULL) AND (welcome_media_file_owner_edition IS NULL OR welcome_media_file_owner_edition IN ('normal', 'premium', 'legacy_unknown'))");

            migrationBuilder.AddCheckConstraint(
                name: "group_settings_welcome_type_check",
                table: "group_settings",
                sql: "welcome_type IN ('composed', 'custom', 'media')");

            migrationBuilder.AddForeignKey(
                name: "FK_group_settings_telegram_chats_log_chat_id",
                table: "group_settings",
                column: "log_chat_id",
                principalTable: "telegram_chats",
                principalColumn: "chat_id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_group_settings_telegram_chats_log_chat_id",
                table: "group_settings");

            migrationBuilder.DropTable(
                name: "custom_commands");

            migrationBuilder.DropTable(
                name: "group_content_items");

            migrationBuilder.DropIndex(
                name: "IX_group_settings_log_chat_id",
                table: "group_settings");

            migrationBuilder.DropCheckConstraint(
                name: "group_settings_config_version_check",
                table: "group_settings");

            migrationBuilder.DropCheckConstraint(
                name: "group_settings_welcome_media_check",
                table: "group_settings");

            migrationBuilder.DropCheckConstraint(
                name: "group_settings_welcome_type_check",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "about_private_only",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "config_imported_at",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "config_version",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "delete_last_welcome",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "extra_private_only",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "help_private_only",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "log_chat_id",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "modlist_private_only",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "reports_enabled",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "rules_private_only",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "welcome_content",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "welcome_enabled",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "welcome_media_file_id",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "welcome_media_file_owner_edition",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "welcome_type",
                table: "group_settings");
        }
    }
}
