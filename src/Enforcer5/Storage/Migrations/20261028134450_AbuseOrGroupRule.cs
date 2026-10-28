using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AbuseOrGroupRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "member_labels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "group_filters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "detections",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "member_labels_category_check",
                table: "member_labels",
                sql: "category IS NULL OR category IN ('abuse', 'group')");

            migrationBuilder.AddCheckConstraint(
                name: "group_filters_category_check",
                table: "group_filters",
                sql: "category IS NULL OR category IN ('abuse', 'group')");

            migrationBuilder.AddCheckConstraint(
                name: "detections_category_check",
                table: "detections",
                sql: "category IS NULL OR category IN ('abuse', 'group')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "member_labels_category_check",
                table: "member_labels");

            migrationBuilder.DropCheckConstraint(
                name: "group_filters_category_check",
                table: "group_filters");

            migrationBuilder.DropCheckConstraint(
                name: "detections_category_check",
                table: "detections");

            migrationBuilder.DropColumn(
                name: "category",
                table: "member_labels");

            migrationBuilder.DropColumn(
                name: "category",
                table: "group_filters");

            migrationBuilder.DropColumn(
                name: "category",
                table: "detections");
        }
    }
}
