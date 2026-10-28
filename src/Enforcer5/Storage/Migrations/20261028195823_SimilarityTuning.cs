using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SimilarityTuning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "similarity",
                table: "detections",
                type: "real",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "detection_near_misses",
                columns: table => new
                {
                    near_miss_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    sign = table.Column<string>(type: "text", nullable: false),
                    similarity = table.Column<float>(type: "real", nullable: false),
                    threshold = table.Column<float>(type: "real", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: true),
                    edition = table.Column<string>(type: "text", nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_detection_near_misses", x => x.near_miss_id);
                    table.CheckConstraint("detection_near_misses_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("detection_near_misses_similarity_check", "similarity >= 0 AND similarity <= 1");
                    table.ForeignKey(
                        name: "FK_detection_near_misses_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_detection_near_misses_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "detection_near_misses_member_idx",
                table: "detection_near_misses",
                columns: new[] { "group_id", "account_id", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "detection_near_misses_sign_time_idx",
                table: "detection_near_misses",
                columns: new[] { "sign", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "IX_detection_near_misses_account_id",
                table: "detection_near_misses",
                column: "account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "detection_near_misses");

            migrationBuilder.DropColumn(
                name: "similarity",
                table: "detections");
        }
    }
}
