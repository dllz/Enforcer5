using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ReportDecisionsAndPrewarnMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies");

            migrationBuilder.DropCheckConstraint(
                name: "member_labels_action_check",
                table: "member_labels");

            migrationBuilder.DropCheckConstraint(
                name: "detections_outcome_check",
                table: "detections");

            migrationBuilder.AddColumn<string>(
                name: "prewarn_mode",
                table: "group_settings",
                type: "text",
                nullable: false,
                defaultValue: "convert");

            migrationBuilder.AddCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies",
                sql: "decision IS NULL OR decision IN ('ban', 'mute', 'fine', 'live', 'extend', 'delete', 'undo', 'keep', 'restore', 'keepnew', 'warn', 'prewarn', 'handled')");

            migrationBuilder.AddCheckConstraint(
                name: "member_labels_action_check",
                table: "member_labels",
                sql: "action IN ('ban', 'tempban', 'kick', 'mute', 'tempmute', 'warn', 'delete', 'unban', 'unmute', 'unwarn', 'fine', 'prewarn', 'handled')");

            migrationBuilder.AddCheckConstraint(
                name: "group_settings_prewarn_mode_check",
                table: "group_settings",
                sql: "prewarn_mode IN ('convert', 'half')");

            migrationBuilder.AddCheckConstraint(
                name: "detections_outcome_check",
                table: "detections",
                sql: "outcome IS NULL OR outcome IN ('ban', 'tempban', 'kick', 'mute', 'tempmute', 'warn', 'delete', 'unban', 'unmute', 'unwarn', 'fine', 'prewarn', 'handled', 'undone')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies");

            migrationBuilder.DropCheckConstraint(
                name: "member_labels_action_check",
                table: "member_labels");

            migrationBuilder.DropCheckConstraint(
                name: "group_settings_prewarn_mode_check",
                table: "group_settings");

            migrationBuilder.DropCheckConstraint(
                name: "detections_outcome_check",
                table: "detections");

            migrationBuilder.DropColumn(
                name: "prewarn_mode",
                table: "group_settings");

            migrationBuilder.AddCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies",
                sql: "decision IS NULL OR decision IN ('ban', 'mute', 'fine', 'live', 'extend', 'delete', 'undo', 'keep', 'restore', 'keepnew')");

            migrationBuilder.AddCheckConstraint(
                name: "member_labels_action_check",
                table: "member_labels",
                sql: "action IN ('ban', 'tempban', 'kick', 'mute', 'tempmute', 'warn', 'delete', 'unban', 'unmute', 'unwarn', 'fine')");

            migrationBuilder.AddCheckConstraint(
                name: "detections_outcome_check",
                table: "detections",
                sql: "outcome IS NULL OR outcome IN ('ban', 'tempban', 'kick', 'mute', 'tempmute', 'warn', 'delete', 'unban', 'unmute', 'unwarn', 'fine', 'undone')");
        }
    }
}
