using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class BatchLeftOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "batch_items_outcome_check",
                table: "batch_items");

            migrationBuilder.AddColumn<bool>(
                name: "left_out",
                table: "batch_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "batch_items_outcome_check",
                table: "batch_items",
                sql: "outcome IS NULL OR outcome IN ('done', 'refused', 'blocked', 'nothing', 'skipped', 'admin')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "batch_items_outcome_check",
                table: "batch_items");

            migrationBuilder.DropColumn(
                name: "left_out",
                table: "batch_items");

            migrationBuilder.AddCheckConstraint(
                name: "batch_items_outcome_check",
                table: "batch_items",
                sql: "outcome IS NULL OR outcome IN ('done', 'refused', 'blocked', 'nothing', 'skipped')");
        }
    }
}
