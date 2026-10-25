using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    report_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    reporter_user_id = table.Column<long>(type: "bigint", nullable: true),
                    reporter_account_id = table.Column<long>(type: "bigint", nullable: true),
                    reporter_display = table.Column<string>(type: "text", nullable: true),
                    target_user_id = table.Column<long>(type: "bigint", nullable: true),
                    target_account_id = table.Column<long>(type: "bigint", nullable: true),
                    target_display = table.Column<string>(type: "text", nullable: true),
                    solver_user_id = table.Column<long>(type: "bigint", nullable: true),
                    solver_display = table.Column<string>(type: "text", nullable: true),
                    report_message_id = table.Column<long>(type: "bigint", nullable: false),
                    target_message_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    resolution_action = table.Column<string>(type: "text", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.report_id);
                    table.CheckConstraint("reports_resolution_action_check", "resolution_action IS NULL OR resolution_action IN ('solved', 'dismissed', 'warned', 'kicked', 'banned', 'deleted')");
                    table.CheckConstraint("reports_resolution_check", "(status = 'open' AND resolved_at IS NULL AND resolution_action IS NULL) OR (status = 'resolved' AND resolved_at IS NOT NULL AND resolution_action IS NOT NULL)");
                    table.CheckConstraint("reports_status_check", "status IN ('open', 'resolved')");
                    table.ForeignKey(
                        name: "FK_reports_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reports_telegram_accounts_reporter_account_id",
                        column: x => x.reporter_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_telegram_accounts_target_account_id",
                        column: x => x.target_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_users_reporter_user_id",
                        column: x => x.reporter_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_users_solver_user_id",
                        column: x => x.solver_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_users_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report_actions",
                columns: table => new
                {
                    report_action_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    report_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_actions", x => x.report_action_id);
                    table.CheckConstraint("report_actions_action_check", "action IN ('deleted', 'warned', 'kicked', 'banned')");
                    table.ForeignKey(
                        name: "FK_report_actions_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "reports",
                        principalColumn: "report_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_actions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_report_id",
                table: "warning_events",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_report_id",
                table: "sanctions",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_actions_actor_user_id",
                table: "report_actions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_actions_report_id",
                table: "report_actions",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_legacy_key",
                table: "reports",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reports_reporter_account_id",
                table: "reports",
                column: "reporter_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_reporter_user_id",
                table: "reports",
                column: "reporter_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_solver_user_id",
                table: "reports",
                column: "solver_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_target_account_id",
                table: "reports",
                column: "target_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_target_user_id",
                table: "reports",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "reports_group_message_idx",
                table: "reports",
                columns: new[] { "group_id", "report_message_id" });

            migrationBuilder.CreateIndex(
                name: "reports_open_target_idx",
                table: "reports",
                columns: new[] { "group_id", "target_message_id" },
                unique: true,
                filter: "status = 'open' AND target_message_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_sanctions_reports_report_id",
                table: "sanctions",
                column: "report_id",
                principalTable: "reports",
                principalColumn: "report_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_warning_events_reports_report_id",
                table: "warning_events",
                column: "report_id",
                principalTable: "reports",
                principalColumn: "report_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sanctions_reports_report_id",
                table: "sanctions");

            migrationBuilder.DropForeignKey(
                name: "FK_warning_events_reports_report_id",
                table: "warning_events");

            migrationBuilder.DropTable(
                name: "report_actions");

            migrationBuilder.DropTable(
                name: "reports");

            migrationBuilder.DropIndex(
                name: "IX_warning_events_report_id",
                table: "warning_events");

            migrationBuilder.DropIndex(
                name: "IX_sanctions_report_id",
                table: "sanctions");
        }
    }
}
