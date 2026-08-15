using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CustomCommandConflictResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_command_conflict_candidates",
                columns: table => new
                {
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    source_trigger = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    media_type = table.Column<string>(type: "text", nullable: true),
                    file_id = table.Column<string>(type: "text", nullable: true),
                    file_owner_edition = table.Column<string>(type: "text", nullable: true),
                    caption = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    selected_candidate_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_command_conflict_candidates", x => x.candidate_id);
                    table.CheckConstraint("custom_command_conflicts_file_owner_check", "(file_id IS NULL) = (file_owner_edition IS NULL) AND (file_owner_edition IS NULL OR file_owner_edition IN ('normal', 'premium', 'legacy_unknown'))");
                    table.CheckConstraint("custom_command_conflicts_media_type_check", "media_type IS NULL OR media_type IN ('gif', 'animation', 'audio', 'document', 'photo', 'sticker', 'video', 'video_note', 'voice')");
                    table.CheckConstraint("custom_command_conflicts_resolution_check", "(resolved_at IS NULL AND resolution IS NULL AND selected_candidate_id IS NULL) OR (resolved_at IS NOT NULL AND resolution = 'deleted' AND selected_candidate_id IS NULL) OR (resolved_at IS NOT NULL AND resolution = 'selected' AND selected_candidate_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_custom_command_conflict_candidates_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_command_conflict_candidates_group_id_trigger",
                table: "custom_command_conflict_candidates",
                columns: new[] { "group_id", "trigger" },
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_custom_command_conflict_candidates_group_id_trigger_source_~",
                table: "custom_command_conflict_candidates",
                columns: new[] { "group_id", "trigger", "source_trigger" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_command_conflict_candidates");
        }
    }
}
