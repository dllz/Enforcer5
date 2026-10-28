using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class BannedMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "banned_media",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    media_unique_id = table.Column<string>(type: "text", nullable: false),
                    media_type = table.Column<string>(type: "text", nullable: false),
                    pdq_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    added_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_banned_media", x => new { x.group_id, x.media_unique_id });
                    table.CheckConstraint("banned_media_source_check", "source IN ('ban', 'report', 'command')");
                    table.ForeignKey(
                        name: "FK_banned_media_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_banned_media_telegram_accounts_added_by_account_id",
                        column: x => x.added_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "banned_media_group_added_idx",
                table: "banned_media",
                columns: new[] { "group_id", "added_at" });

            migrationBuilder.CreateIndex(
                name: "IX_banned_media_added_by_account_id",
                table: "banned_media",
                column: "added_by_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "banned_media");
        }
    }
}
