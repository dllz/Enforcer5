using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DetectionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "detection_policies",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    check_key = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    action = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<short>(type: "smallint", nullable: true),
                    settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_detection_policies", x => new { x.group_id, x.check_key });
                    table.CheckConstraint("detection_policies_action_check", "action IS NULL OR action IN ('none', 'score', 'report', 'delete', 'warn', 'mute', 'tempmute', 'kick', 'tempban', 'ban')");
                    table.CheckConstraint("detection_policies_check_key_check", "check_key ~ '^[a-z][a-z0-9_:]*$'");
                    table.ForeignKey(
                        name: "FK_detection_policies_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_filters",
                columns: table => new
                {
                    filter_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    target = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    preset = table.Column<string>(type: "text", nullable: true),
                    pattern = table.Column<string>(type: "text", nullable: true),
                    max_characters = table.Column<int>(type: "integer", nullable: true),
                    max_lines = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false, defaultValue: "live"),
                    trial_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    hits = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_filters", x => x.filter_id);
                    table.CheckConstraint("group_filters_action_check", "action IS NULL OR action IN ('none', 'score', 'report', 'delete', 'warn', 'mute', 'tempmute', 'kick', 'tempban', 'ban')");
                    table.CheckConstraint("group_filters_hits_check", "hits >= 0");
                    table.CheckConstraint("group_filters_kind_check", "kind IN ('preset', 'length', 'regex', 'username', 'exact')");
                    table.CheckConstraint("group_filters_length_check", "kind <> 'length' OR target IN ('text', 'name')");
                    table.CheckConstraint("group_filters_limits_check", "(max_characters IS NULL OR max_characters > 0) AND (max_lines IS NULL OR max_lines > 0)");
                    table.CheckConstraint("group_filters_mode_check", "mode IN ('live', 'trial')");
                    table.CheckConstraint("group_filters_pattern_check", "(kind IN ('regex', 'username', 'exact')) = (pattern IS NOT NULL)");
                    table.CheckConstraint("group_filters_preset_check", "(kind = 'preset') = (preset IS NOT NULL)");
                    table.CheckConstraint("group_filters_target_check", "target IS NULL OR target IN ('name', 'username', 'bio', 'text', 'channel', 'inline_bot', 'sticker_set')");
                    table.CheckConstraint("group_filters_trial_check", "mode <> 'trial' OR trial_until IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_group_filters_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_filters_telegram_accounts_created_by_account_id",
                        column: x => x.created_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "group_settings",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    maximum_warnings = table.Column<short>(type: "smallint", nullable: false),
                    warning_action = table.Column<string>(type: "text", nullable: false),
                    tempban_minutes = table.Column<int>(type: "integer", nullable: false),
                    tempmute_minutes = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_settings", x => x.group_id);
                    table.CheckConstraint("group_settings_actions_check", "warning_action IN ('none', 'kick', 'ban', 'mute', 'tempban', 'tempmute')");
                    table.CheckConstraint("group_settings_limits_check", "maximum_warnings >= 0 AND tempban_minutes >= 0 AND tempmute_minutes >= 0");
                    table.CheckConstraint("group_settings_version_check", "version > 0");
                    table.ForeignKey(
                        name: "FK_group_settings_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "group_filters_length_idx",
                table: "group_filters",
                columns: new[] { "group_id", "target" },
                unique: true,
                filter: "kind = 'length'");

            migrationBuilder.CreateIndex(
                name: "group_filters_pattern_idx",
                table: "group_filters",
                columns: new[] { "group_id", "target", "kind", "pattern" },
                unique: true,
                filter: "pattern IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "group_filters_preset_idx",
                table: "group_filters",
                columns: new[] { "group_id", "preset" },
                unique: true,
                filter: "preset IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_group_filters_created_by_account_id",
                table: "group_filters",
                column: "created_by_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "detection_policies");

            migrationBuilder.DropTable(
                name: "group_filters");

            migrationBuilder.DropTable(
                name: "group_settings");
        }
    }
}
