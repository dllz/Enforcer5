using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class UnattributedMessageTotals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "unattributed_count",
                table: "account_message_totals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "account_message_totals_unattributed_bound_check",
                table: "account_message_totals",
                sql: "unattributed_count <= message_count");

            migrationBuilder.AddCheckConstraint(
                name: "account_message_totals_unattributed_check",
                table: "account_message_totals",
                sql: "unattributed_count >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "account_message_totals_unattributed_bound_check",
                table: "account_message_totals");

            migrationBuilder.DropCheckConstraint(
                name: "account_message_totals_unattributed_check",
                table: "account_message_totals");

            migrationBuilder.DropColumn(
                name: "unattributed_count",
                table: "account_message_totals");
        }
    }
}
