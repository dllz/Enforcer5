using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ReportTrustedBots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots");

            migrationBuilder.AddCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots",
                sql: "source IN ('admin_added', 'command', 'present', 'member_added', 'summoned', 'global', 'report')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots");

            migrationBuilder.AddCheckConstraint(
                name: "group_bots_source_check",
                table: "group_bots",
                sql: "source IN ('admin_added', 'command', 'present', 'member_added', 'summoned', 'global')");
        }
    }
}
