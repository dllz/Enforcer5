using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CustomEmoji : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "group_filters_kind_check",
                table: "group_filters");

            migrationBuilder.DropCheckConstraint(
                name: "group_filters_pattern_check",
                table: "group_filters");

            migrationBuilder.DropCheckConstraint(
                name: "group_filters_target_check",
                table: "group_filters");

            migrationBuilder.CreateTable(
                name: "custom_emoji",
                columns: table => new
                {
                    custom_emoji_id = table.Column<string>(type: "text", nullable: false),
                    set_name = table.Column<string>(type: "text", nullable: true),
                    file_unique_id = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    nsfw = table.Column<float>(type: "real", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_emoji", x => x.custom_emoji_id);
                    table.CheckConstraint("custom_emoji_format_check", "format IN ('static', 'video', 'animated')");
                });

            migrationBuilder.CreateTable(
                name: "nsfw_emoji_sets",
                columns: table => new
                {
                    set_name = table.Column<string>(type: "text", nullable: false),
                    custom_emoji_id = table.Column<string>(type: "text", nullable: true),
                    nsfw = table.Column<float>(type: "real", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    flagged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleared_by_account_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nsfw_emoji_sets", x => x.set_name);
                    table.CheckConstraint("nsfw_emoji_sets_source_check", "source IN ('auto', 'dev')");
                    table.ForeignKey(
                        name: "FK_nsfw_emoji_sets_telegram_accounts_cleared_by_account_id",
                        column: x => x.cleared_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_emoji_kind_check",
                table: "group_filters",
                sql: "kind NOT IN ('auto', 'allowed') OR target = 'emoji_set'");

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_kind_check",
                table: "group_filters",
                sql: "kind IN ('preset', 'length', 'regex', 'username', 'exact', 'auto', 'allowed')");

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_pattern_check",
                table: "group_filters",
                sql: "(kind IN ('regex', 'username', 'exact', 'auto', 'allowed')) = (pattern IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_target_check",
                table: "group_filters",
                sql: "target IS NULL OR target IN ('name', 'username', 'bio', 'text', 'channel', 'inline_bot', 'sticker_set', 'emoji_set')");

            migrationBuilder.CreateIndex(
                name: "custom_emoji_set_idx",
                table: "custom_emoji",
                column: "set_name");

            migrationBuilder.CreateIndex(
                name: "IX_nsfw_emoji_sets_cleared_by_account_id",
                table: "nsfw_emoji_sets",
                column: "cleared_by_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_emoji");

            migrationBuilder.DropTable(
                name: "nsfw_emoji_sets");

            migrationBuilder.DropCheckConstraint(
                name: "group_filters_emoji_kind_check",
                table: "group_filters");

            migrationBuilder.DropCheckConstraint(
                name: "group_filters_kind_check",
                table: "group_filters");

            migrationBuilder.DropCheckConstraint(
                name: "group_filters_pattern_check",
                table: "group_filters");

            migrationBuilder.DropCheckConstraint(
                name: "group_filters_target_check",
                table: "group_filters");

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_kind_check",
                table: "group_filters",
                sql: "kind IN ('preset', 'length', 'regex', 'username', 'exact')");

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_pattern_check",
                table: "group_filters",
                sql: "(kind IN ('regex', 'username', 'exact')) = (pattern IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_target_check",
                table: "group_filters",
                sql: "target IS NULL OR target IN ('name', 'username', 'bio', 'text', 'channel', 'inline_bot', 'sticker_set')");
        }
    }
}
