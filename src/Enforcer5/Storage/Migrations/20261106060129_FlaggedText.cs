using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class FlaggedText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "detail",
                table: "detections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "message_text",
                table: "detections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "detail",
                table: "detections");

            migrationBuilder.DropColumn(
                name: "message_text",
                table: "detections");
        }
    }
}
