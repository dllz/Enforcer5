using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class TempbanSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "storage_imports",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_imports", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "tempban_schedule",
                columns: table => new
                {
                    edition = table.Column<string>(type: "text", nullable: false),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: false),
                    until_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    source = table.Column<string>(type: "text", nullable: false, defaultValue: "bot"),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tempban_schedule", x => new { x.edition, x.telegram_chat_id, x.telegram_user_id });
                    table.CheckConstraint("tempban_schedule_claim_check", "(claimed_at IS NULL) = (claimed_by IS NULL)");
                    table.CheckConstraint("tempban_schedule_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("tempban_schedule_source_check", "source IN ('bot', 'redis')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_tempban_schedule_edition_until_at",
                table: "tempban_schedule",
                columns: new[] { "edition", "until_at" });

            migrationBuilder.CreateIndex(
                name: "IX_tempban_schedule_telegram_user_id",
                table: "tempban_schedule",
                column: "telegram_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_imports");

            migrationBuilder.DropTable(
                name: "tempban_schedule");
        }
    }
}
