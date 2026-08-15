using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GroupMigrationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_group_state",
                columns: table => new
                {
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false, defaultValue: "Redis"),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    chunk_cursor = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    migrated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_group_state", x => x.telegram_chat_id);
                    table.CheckConstraint("migration_group_state_chunk_check", "chunk_cursor >= 0");
                    table.CheckConstraint("migration_group_state_lease_check", "(lease_owner IS NULL) = (lease_expires_at IS NULL)");
                    table.CheckConstraint("migration_group_state_state_check", "state IN ('Redis', 'Draining', 'Verifying', 'Postgres', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_migration_group_state_lease_expires_at",
                table: "migration_group_state",
                column: "lease_expires_at",
                filter: "lease_expires_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_migration_group_state_state",
                table: "migration_group_state",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "migration_group_state");
        }
    }
}
