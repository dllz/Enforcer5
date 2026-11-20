using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WarnReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "recent_warn_reasons",
                table: "group_settings",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<short>(
                name: "warn_reason_mode",
                table: "group_settings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string[]>(
                name: "warn_reasons",
                table: "group_settings",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "group_settings_warn_reason_mode_check",
                table: "group_settings",
                sql: "warn_reason_mode BETWEEN 0 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "group_settings_warn_reason_mode_check",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "recent_warn_reasons",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "warn_reason_mode",
                table: "group_settings");

            migrationBuilder.DropColumn(
                name: "warn_reasons",
                table: "group_settings");
        }
    }
}
