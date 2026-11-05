using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class NsfwImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_scores",
                columns: table => new
                {
                    media_unique_id = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    nsfw = table.Column<float>(type: "real", nullable: false),
                    part = table.Column<string>(type: "text", nullable: true),
                    part_score = table.Column<float>(type: "real", nullable: false),
                    scored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_scores", x => new { x.media_unique_id, x.model });
                });

            migrationBuilder.CreateTable(
                name: "profile_scans",
                columns: table => new
                {
                    scan_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: true),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    photos = table.Column<int>(type: "integer", nullable: false),
                    channel_posts = table.Column<int>(type: "integer", nullable: false),
                    scored = table.Column<int>(type: "integer", nullable: false),
                    max_nsfw = table.Column<float>(type: "real", nullable: false),
                    max_part = table.Column<string>(type: "text", nullable: true),
                    max_part_score = table.Column<float>(type: "real", nullable: false),
                    flagged = table.Column<bool>(type: "boolean", nullable: false),
                    flagged_media = table.Column<string>(type: "text", nullable: true),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_scans", x => x.scan_id);
                    table.CheckConstraint("profile_scans_source_check", "source IN ('join', 'first_post', 'eval')");
                    table.ForeignKey(
                        name: "FK_profile_scans_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_profile_scans_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "media_scores_scored_idx",
                table: "media_scores",
                column: "scored_at");

            migrationBuilder.CreateIndex(
                name: "profile_scans_account_idx",
                table: "profile_scans",
                columns: new[] { "account_id", "scanned_at" });

            migrationBuilder.CreateIndex(
                name: "profile_scans_group_idx",
                table: "profile_scans",
                columns: new[] { "group_id", "scanned_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_scores");

            migrationBuilder.DropTable(
                name: "profile_scans");
        }
    }
}
