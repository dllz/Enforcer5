using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class KeyspaceInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_scan_generations",
                columns: table => new
                {
                    generation_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "running"),
                    key_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    unclassified_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    cursor = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_scan_generations", x => x.generation_id);
                    table.CheckConstraint("migration_scan_generations_completed_check", "(status = 'completed') = (completed_at IS NOT NULL)");
                    table.CheckConstraint("migration_scan_generations_counts_check", "key_count >= 0 AND unclassified_count >= 0");
                    table.CheckConstraint("migration_scan_generations_status_check", "status IN ('running', 'completed', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "migration_key_inventory",
                columns: table => new
                {
                    generation_id = table.Column<long>(type: "bigint", nullable: false),
                    redis_key = table.Column<string>(type: "text", nullable: false),
                    key_class = table.Column<string>(type: "text", nullable: false),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: true),
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_key_inventory", x => new { x.generation_id, x.redis_key });
                    table.ForeignKey(
                        name: "FK_migration_key_inventory_migration_scan_generations_generati~",
                        column: x => x.generation_id,
                        principalTable: "migration_scan_generations",
                        principalColumn: "generation_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "migration_key_inventory_class_idx",
                table: "migration_key_inventory",
                columns: new[] { "generation_id", "key_class" });

            migrationBuilder.CreateIndex(
                name: "migration_key_inventory_group_idx",
                table: "migration_key_inventory",
                columns: new[] { "generation_id", "telegram_chat_id" },
                filter: "telegram_chat_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "migration_scan_generations_completed_idx",
                table: "migration_scan_generations",
                column: "completed_at",
                filter: "status = 'completed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "migration_key_inventory");

            migrationBuilder.DropTable(
                name: "migration_scan_generations");
        }
    }
}
