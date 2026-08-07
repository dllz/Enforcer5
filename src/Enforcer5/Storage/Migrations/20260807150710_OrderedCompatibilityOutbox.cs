using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OrderedCompatibilityOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ordering_key",
                table: "compatibility_outbox",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "compatibility_outbox_ordering_idx",
                table: "compatibility_outbox",
                columns: new[] { "ordering_key", "created_at" },
                filter: "completed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "compatibility_outbox_ordering_idx",
                table: "compatibility_outbox");

            migrationBuilder.DropColumn(
                name: "ordering_key",
                table: "compatibility_outbox");
        }
    }
}
