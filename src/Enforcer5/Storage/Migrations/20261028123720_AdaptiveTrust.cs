using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AdaptiveTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies");

            migrationBuilder.DropCheckConstraint(
                name: "report_copies_kind_check",
                table: "report_copies");

            migrationBuilder.AddColumn<long>(
                name: "adjustment_id",
                table: "report_copies",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "detection_adjustments",
                columns: table => new
                {
                    adjustment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    from_value = table.Column<int>(type: "integer", nullable: true),
                    from_days = table.Column<int>(type: "integer", nullable: true),
                    to_value = table.Column<int>(type: "integer", nullable: true),
                    to_days = table.Column<int>(type: "integer", nullable: true),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    label_id = table.Column<long>(type: "bigint", nullable: true),
                    applied = table.Column<bool>(type: "boolean", nullable: false),
                    undone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    undone_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    edition = table.Column<string>(type: "text", nullable: false),
                    made_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_detection_adjustments", x => x.adjustment_id);
                    table.CheckConstraint("detection_adjustments_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("detection_adjustments_kind_check", "kind IN ('established', 'report_at')");
                    table.CheckConstraint("detection_adjustments_reason_check", "reason IN ('decision', 'untrust', 'takeover', 'decay', 'decisions', 'reset')");
                    table.CheckConstraint("detection_adjustments_undone_check", "undone_at IS NULL OR applied");
                    table.ForeignKey(
                        name: "FK_detection_adjustments_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_detection_adjustments_member_labels_label_id",
                        column: x => x.label_id,
                        principalTable: "member_labels",
                        principalColumn: "label_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_detection_adjustments_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_detection_adjustments_telegram_accounts_undone_by_account_id",
                        column: x => x.undone_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "member_trust_overrides",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    set_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    messages_at_set = table.Column<long>(type: "bigint", nullable: false),
                    restore_prompted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_trust_overrides", x => new { x.group_id, x.account_id });
                    table.CheckConstraint("member_trust_overrides_messages_check", "messages_at_set >= 0");
                    table.ForeignKey(
                        name: "FK_member_trust_overrides_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_member_trust_overrides_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_member_trust_overrides_telegram_accounts_set_by_account_id",
                        column: x => x.set_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "report_copies_adjustment_idx",
                table: "report_copies",
                column: "adjustment_id",
                filter: "adjustment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "report_copies_adjustment_check",
                table: "report_copies",
                sql: "(kind = 'learn') = (adjustment_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies",
                sql: "decision IS NULL OR decision IN ('ban', 'mute', 'fine', 'live', 'extend', 'delete', 'undo', 'keep', 'restore', 'keepnew')");

            migrationBuilder.AddCheckConstraint(
                name: "report_copies_kind_check",
                table: "report_copies",
                sql: "kind IN ('member', 'trial', 'learn', 'restore')");

            migrationBuilder.CreateIndex(
                name: "detection_adjustments_group_idx",
                table: "detection_adjustments",
                columns: new[] { "group_id", "made_at" });

            migrationBuilder.CreateIndex(
                name: "IX_detection_adjustments_account_id",
                table: "detection_adjustments",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_detection_adjustments_label_id",
                table: "detection_adjustments",
                column: "label_id");

            migrationBuilder.CreateIndex(
                name: "IX_detection_adjustments_undone_by_account_id",
                table: "detection_adjustments",
                column: "undone_by_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_member_trust_overrides_account_id",
                table: "member_trust_overrides",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_member_trust_overrides_set_by_account_id",
                table: "member_trust_overrides",
                column: "set_by_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "detection_adjustments");

            migrationBuilder.DropTable(
                name: "member_trust_overrides");

            migrationBuilder.DropIndex(
                name: "report_copies_adjustment_idx",
                table: "report_copies");

            migrationBuilder.DropCheckConstraint(
                name: "report_copies_adjustment_check",
                table: "report_copies");

            migrationBuilder.DropCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies");

            migrationBuilder.DropCheckConstraint(
                name: "report_copies_kind_check",
                table: "report_copies");

            migrationBuilder.DropColumn(
                name: "adjustment_id",
                table: "report_copies");

            migrationBuilder.AddCheckConstraint(
                name: "report_copies_decision_check",
                table: "report_copies",
                sql: "decision IS NULL OR decision IN ('ban', 'mute', 'fine', 'live', 'extend', 'delete')");

            migrationBuilder.AddCheckConstraint(
                name: "report_copies_kind_check",
                table: "report_copies",
                sql: "kind IN ('member', 'trial')");
        }
    }
}
