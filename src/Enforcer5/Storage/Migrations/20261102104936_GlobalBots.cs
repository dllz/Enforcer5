using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GlobalBots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots");

            migrationBuilder.CreateTable(
                name: "global_bots",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    set_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_bots", x => x.account_id);
                    table.CheckConstraint("global_bots_status_check", "status IN ('allowed', 'blocked')");
                    table.ForeignKey(
                        name: "FK_global_bots_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_global_bots_telegram_accounts_set_by_account_id",
                        column: x => x.set_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots",
                sql: "source IN ('admin_added', 'command', 'present', 'member_added', 'summoned', 'global')");

            migrationBuilder.CreateIndex(
                name: "IX_global_bots_set_by_account_id",
                table: "global_bots",
                column: "set_by_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "global_bots");

            migrationBuilder.DropCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots");

            migrationBuilder.AddCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots",
                sql: "source IN ('admin_added', 'command', 'present', 'member_added', 'summoned')");
        }
    }
}
