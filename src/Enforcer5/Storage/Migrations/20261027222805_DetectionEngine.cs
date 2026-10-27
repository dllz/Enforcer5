using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DetectionEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "member_labels",
                columns: table => new
                {
                    label_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    label = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    decided_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    message_type = table.Column<string>(type: "text", nullable: true),
                    text_hash = table.Column<string>(type: "text", nullable: true),
                    media_unique_id = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    missed = table.Column<bool>(type: "boolean", nullable: true),
                    undone_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    undone_by_label_id = table.Column<long>(type: "bigint", nullable: true),
                    label_key = table.Column<string>(type: "text", nullable: true),
                    edition = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_labels", x => x.label_id);
                    table.CheckConstraint("member_labels_action_check", "action IN ('ban', 'tempban', 'kick', 'mute', 'tempmute', 'warn', 'delete', 'unban', 'unmute', 'unwarn', 'fine')");
                    table.CheckConstraint("member_labels_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("member_labels_label_check", "label IN ('bad', 'fine', 'undo')");
                    table.CheckConstraint("member_labels_source_check", "source IN ('command', 'button', 'report', 'app', 'auto', 'raid', 'global', 'established')");
                    table.CheckConstraint("member_labels_undone_check", "(undone_at IS NULL) = (undone_by_label_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_member_labels_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_member_labels_member_labels_undone_by_label_id",
                        column: x => x.undone_by_label_id,
                        principalTable: "member_labels",
                        principalColumn: "label_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_member_labels_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_member_labels_telegram_accounts_decided_by_account_id",
                        column: x => x.decided_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "detections",
                columns: table => new
                {
                    detection_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    sign = table.Column<string>(type: "text", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: false),
                    weight = table.Column<int>(type: "integer", nullable: false),
                    score_before = table.Column<int>(type: "integer", nullable: false),
                    score_after = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false),
                    reported = table.Column<bool>(type: "boolean", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    message_type = table.Column<string>(type: "text", nullable: true),
                    text_hash = table.Column<string>(type: "text", nullable: true),
                    media_unique_id = table.Column<string>(type: "text", nullable: true),
                    trust_level = table.Column<string>(type: "text", nullable: true),
                    member_messages = table.Column<long>(type: "bigint", nullable: true),
                    member_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evidence = table.Column<string>(type: "jsonb", nullable: true),
                    edition = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    label_id = table.Column<long>(type: "bigint", nullable: true),
                    decided_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_detections", x => x.detection_id);
                    table.CheckConstraint("detections_action_check", "action IN ('none', 'score', 'report', 'delete', 'warn', 'mute', 'tempmute', 'kick', 'tempban', 'ban')");
                    table.CheckConstraint("detections_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("detections_mode_check", "mode IN ('live', 'observe', 'trial')");
                    table.CheckConstraint("detections_outcome_check", "outcome IS NULL OR outcome IN ('ban', 'tempban', 'kick', 'mute', 'tempmute', 'warn', 'delete', 'unban', 'unmute', 'unwarn', 'fine', 'undone')");
                    table.CheckConstraint("detections_phase_check", "phase IN ('inline', 'enriched')");
                    table.CheckConstraint("detections_score_check", "score_before >= 0 AND score_after >= score_before");
                    table.CheckConstraint("detections_trust_check", "trust_level IS NULL OR trust_level IN ('admin', 'approved', 'established', 'new')");
                    table.ForeignKey(
                        name: "FK_detections_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_detections_member_labels_label_id",
                        column: x => x.label_id,
                        principalTable: "member_labels",
                        principalColumn: "label_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_detections_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_detections_telegram_accounts_decided_by_account_id",
                        column: x => x.decided_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "detections_group_time_idx",
                table: "detections",
                columns: new[] { "group_id", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "detections_label_idx",
                table: "detections",
                column: "label_id",
                filter: "label_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "detections_member_idx",
                table: "detections",
                columns: new[] { "group_id", "account_id", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "detections_sign_time_idx",
                table: "detections",
                columns: new[] { "sign", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "IX_detections_account_id",
                table: "detections",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_detections_decided_by_account_id",
                table: "detections",
                column: "decided_by_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_member_labels_account_id",
                table: "member_labels",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_member_labels_decided_by_account_id",
                table: "member_labels",
                column: "decided_by_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_member_labels_undone_by_label_id",
                table: "member_labels",
                column: "undone_by_label_id");

            migrationBuilder.CreateIndex(
                name: "member_labels_group_time_idx",
                table: "member_labels",
                columns: new[] { "group_id", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "member_labels_key_idx",
                table: "member_labels",
                column: "label_key",
                unique: true,
                filter: "label_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "member_labels_member_idx",
                table: "member_labels",
                columns: new[] { "group_id", "account_id", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "detections");

            migrationBuilder.DropTable(
                name: "member_labels");
        }
    }
}
