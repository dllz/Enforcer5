using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class BotMutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bot_mutes",
                columns: table => new
                {
                    edition = table.Column<string>(type: "text", nullable: false),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    muted_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    leave_asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_mutes", x => new { x.edition, x.telegram_chat_id });
                    table.CheckConstraint("bot_mutes_edition_check", "edition IN ('normal', 'premium')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bot_mutes");
        }
    }
}
