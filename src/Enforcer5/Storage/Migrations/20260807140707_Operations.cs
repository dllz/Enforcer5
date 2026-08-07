using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Operations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    edition = table.Column<string>(type: "text", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: true),
                    group_id = table.Column<long>(type: "bigint", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    subject_user_id = table.Column<long>(type: "bigint", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bot_statistic_baselines",
                columns: table => new
                {
                    edition = table.Column<string>(type: "text", nullable: false),
                    messages = table.Column<long>(type: "bigint", nullable: false),
                    users = table.Column<long>(type: "bigint", nullable: false),
                    bans = table.Column<long>(type: "bigint", nullable: false),
                    kicks = table.Column<long>(type: "bigint", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_statistic_baselines", x => x.edition);
                });

            migrationBuilder.CreateTable(
                name: "global_sanctions",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "text", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_sanctions", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "group_user_statistics",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    messages = table.Column<long>(type: "bigint", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_user_statistics", x => new { x.group_id, x.user_id });
                    table.CheckConstraint("group_user_statistics_messages_check", "messages >= 0");
                });

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    report_message_id = table.Column<long>(type: "bigint", nullable: false),
                    reporter_user_id = table.Column<long>(type: "bigint", nullable: true),
                    reporter_display = table.Column<string>(type: "text", nullable: true),
                    reported_user_id = table.Column<long>(type: "bigint", nullable: true),
                    reported_message_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    solved_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    solved_by_display = table.Column<string>(type: "text", nullable: true),
                    solved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.id);
                    table.CheckConstraint("reports_status_check", "status IN ('open','solved','dismissed')");
                });

            migrationBuilder.CreateTable(
                name: "sanctions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    edition = table.Column<string>(type: "text", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_by = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sanctions", x => x.id);
                    table.CheckConstraint("sanctions_kind_check", "kind IN ('ban','mute','tempban','tempmute')");
                    table.CheckConstraint("sanctions_status_check", "status IN ('active','due','revoked','completed')");
                });

            migrationBuilder.CreateTable(
                name: "user_statistics",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    messages = table.Column<long>(type: "bigint", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_statistics", x => x.user_id);
                    table.CheckConstraint("user_statistics_messages_check", "messages >= 0");
                });

            migrationBuilder.CreateTable(
                name: "report_deliveries",
                columns: table => new
                {
                    report_id = table.Column<long>(type: "bigint", nullable: false),
                    delivery_index = table.Column<int>(type: "integer", nullable: false),
                    admin_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    admin_message_id = table.Column<int>(type: "integer", nullable: false),
                    forwarded_message_id = table.Column<int>(type: "integer", nullable: true),
                    legacy_payload = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_deliveries", x => new { x.report_id, x.delivery_index });
                    table.ForeignKey(
                        name: "FK_report_deliveries_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_group_id_occurred_at",
                table: "audit_events",
                columns: new[] { "group_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_legacy_key",
                table: "audit_events",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reports_group_id_report_message_id",
                table: "reports",
                columns: new[] { "group_id", "report_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reports_group_id_status_created_at",
                table: "reports",
                columns: new[] { "group_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_edition_status_expires_at",
                table: "sanctions",
                columns: new[] { "edition", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_legacy_key",
                table: "sanctions",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "bot_statistic_baselines");

            migrationBuilder.DropTable(
                name: "global_sanctions");

            migrationBuilder.DropTable(
                name: "group_user_statistics");

            migrationBuilder.DropTable(
                name: "report_deliveries");

            migrationBuilder.DropTable(
                name: "sanctions");

            migrationBuilder.DropTable(
                name: "user_statistics");

            migrationBuilder.DropTable(
                name: "reports");
        }
    }
}
