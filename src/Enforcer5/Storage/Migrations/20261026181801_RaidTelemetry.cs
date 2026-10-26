using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RaidTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "raid_incidents",
                columns: table => new
                {
                    incident_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    incident_key = table.Column<string>(type: "text", nullable: false),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    edition = table.Column<string>(type: "text", nullable: true),
                    trigger = table.Column<string>(type: "text", nullable: true),
                    phase_reached = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<string>(type: "text", nullable: true),
                    peak_score = table.Column<int>(type: "integer", nullable: false),
                    reasons = table.Column<string[]>(type: "text[]", nullable: true),
                    joined = table.Column<int>(type: "integer", nullable: false),
                    muted = table.Column<int>(type: "integer", nullable: false),
                    banned = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raid_incidents", x => x.incident_id);
                    table.CheckConstraint("raid_incidents_ended_by_check", "ended_by IS NULL OR ended_by IN ('quiet', 'admin', 'ignored')");
                    table.CheckConstraint("raid_incidents_phase_check", "phase_reached IN ('alert', 'active')");
                    table.ForeignKey(
                        name: "FK_raid_incidents_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "raid_events",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    incident_id = table.Column<long>(type: "bigint", nullable: false),
                    offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raid_events", x => x.event_id);
                    table.CheckConstraint("raid_events_kind_check", "kind IN ('join', 'message', 'signal', 'action', 'rate_limit', 'phase')");
                    table.ForeignKey(
                        name: "FK_raid_events_raid_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "raid_incidents",
                        principalColumn: "incident_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "raid_events_timeline_idx",
                table: "raid_events",
                columns: new[] { "incident_id", "offset_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_raid_incidents_incident_key",
                table: "raid_incidents",
                column: "incident_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "raid_incidents_group_time_idx",
                table: "raid_incidents",
                columns: new[] { "group_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "raid_events");

            migrationBuilder.DropTable(
                name: "raid_incidents");
        }
    }
}
