using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AccountModerationCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_moderation_counters",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_moderation_counters", x => new { x.account_id, x.kind });
                    table.CheckConstraint("account_moderation_counters_count_check", "count >= 0");
                    table.ForeignKey(
                        name: "FK_account_moderation_counters_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_moderation_counters");
        }
    }
}
