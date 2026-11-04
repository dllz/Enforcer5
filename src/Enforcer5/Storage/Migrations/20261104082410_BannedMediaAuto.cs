using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class BannedMediaAuto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "banned_media_source_check",
                table: "banned_media");

            migrationBuilder.AddCheckConstraint(
                name: "banned_media_source_check",
                table: "banned_media",
                sql: "source IN ('ban', 'report', 'command', 'auto')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "banned_media_source_check",
                table: "banned_media");

            migrationBuilder.AddCheckConstraint(
                name: "banned_media_source_check",
                table: "banned_media",
                sql: "source IN ('ban', 'report', 'command')");
        }
    }
}
