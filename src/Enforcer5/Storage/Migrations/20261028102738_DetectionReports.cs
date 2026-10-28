using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DetectionReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_notified_at",
                table: "group_filters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "report_admin_optouts",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    opted_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_admin_optouts", x => new { x.group_id, x.account_id });
                    table.ForeignKey(
                        name: "FK_report_admin_optouts_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_admin_optouts_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_copies",
                columns: table => new
                {
                    copy_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    filter_id = table.Column<long>(type: "bigint", nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    edition = table.Column<string>(type: "text", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: true),
                    decided_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_copies", x => x.copy_id);
                    table.CheckConstraint("report_copies_decided_check", "(decision IS NULL) = (decided_at IS NULL)");
                    table.CheckConstraint("report_copies_decision_check", "decision IS NULL OR decision IN ('ban', 'mute', 'fine', 'live', 'extend', 'delete')");
                    table.CheckConstraint("report_copies_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("report_copies_filter_check", "(kind = 'trial') = (filter_id IS NOT NULL)");
                    table.CheckConstraint("report_copies_kind_check", "kind IN ('member', 'trial')");
                    table.ForeignKey(
                        name: "FK_report_copies_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_copies_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_report_copies_telegram_accounts_decided_by_account_id",
                        column: x => x.decided_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_admin_optouts_account_id",
                table: "report_admin_optouts",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_copies_account_id",
                table: "report_copies",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_report_copies_decided_by_account_id",
                table: "report_copies",
                column: "decided_by_account_id");

            migrationBuilder.CreateIndex(
                name: "report_copies_filter_idx",
                table: "report_copies",
                columns: new[] { "group_id", "filter_id" },
                filter: "filter_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "report_copies_member_idx",
                table: "report_copies",
                columns: new[] { "group_id", "account_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "report_copies_sent_idx",
                table: "report_copies",
                column: "sent_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_admin_optouts");

            migrationBuilder.DropTable(
                name: "report_copies");

            migrationBuilder.DropColumn(
                name: "trial_notified_at",
                table: "group_filters");
        }
    }
}
