using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgresStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "compatibility_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compatibility_outbox", x => x.id);
                    table.CheckConstraint("compatibility_outbox_attempt_count_check", "attempt_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "group_settings",
                columns: table => new
                {
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    setting_group = table.Column<string>(type: "text", nullable: false),
                    values = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_settings", x => new { x.chat_id, x.setting_group });
                    table.CheckConstraint("group_settings_group_check", "setting_group <> ''");
                    table.CheckConstraint("group_settings_values_check", "jsonb_typeof(values) = 'object'");
                    table.CheckConstraint("group_settings_version_check", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "group_settings_redis_sync",
                columns: table => new
                {
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    setting_group = table.Column<string>(type: "text", nullable: false),
                    redis_key = table.Column<string>(type: "text", nullable: false),
                    source_exists = table.Column<bool>(type: "boolean", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    source_checksum = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    persisted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_settings_redis_sync", x => new { x.chat_id, x.setting_group });
                    table.CheckConstraint("group_settings_redis_sync_revision_check", "source_revision >= 0");
                });

            migrationBuilder.CreateTable(
                name: "migration_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    mode = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    source_snapshot = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_runs", x => x.id);
                    table.CheckConstraint("migration_runs_status_check", "status IN ('running', 'completed', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "legacy_redis_records",
                columns: table => new
                {
                    source_key = table.Column<string>(type: "text", nullable: false),
                    source_field = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    raw_value = table.Column<string>(type: "text", nullable: true),
                    source_ttl_ms = table.Column<long>(type: "bigint", nullable: true),
                    source_checksum = table.Column<string>(type: "text", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    migration_run_id = table.Column<long>(type: "bigint", nullable: true),
                    disposition = table.Column<string>(type: "text", nullable: true, defaultValue: "pending"),
                    target_reference = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_redis_records", x => new { x.source_key, x.source_field });
                    table.CheckConstraint("legacy_redis_records_disposition_check", "disposition IN ('pending', 'migrated', 'retained', 'excluded', 'quarantined')");
                    table.ForeignKey(
                        name: "FK_legacy_redis_records_migration_runs_migration_run_id",
                        column: x => x.migration_run_id,
                        principalTable: "migration_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "migration_exceptions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    migration_run_id = table.Column<long>(type: "bigint", nullable: true),
                    domain = table.Column<string>(type: "text", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: true),
                    source_field = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    raw_value = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_exceptions", x => x.id);
                    table.CheckConstraint("migration_exceptions_severity_check", "severity IN ('info', 'warning', 'blocking')");
                    table.ForeignKey(
                        name: "FK_migration_exceptions_migration_runs_migration_run_id",
                        column: x => x.migration_run_id,
                        principalTable: "migration_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "compatibility_outbox_pending_idx",
                table: "compatibility_outbox",
                columns: new[] { "next_attempt_at", "created_at" },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "group_settings_redis_sync_observed_idx",
                table: "group_settings_redis_sync",
                column: "observed_at");

            migrationBuilder.CreateIndex(
                name: "IX_group_settings_redis_sync_redis_key",
                table: "group_settings_redis_sync",
                column: "redis_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_redis_records_migration_run_id",
                table: "legacy_redis_records",
                column: "migration_run_id");

            migrationBuilder.CreateIndex(
                name: "legacy_redis_records_disposition_idx",
                table: "legacy_redis_records",
                columns: new[] { "disposition", "source_key" });

            migrationBuilder.CreateIndex(
                name: "IX_migration_exceptions_migration_run_id",
                table: "migration_exceptions",
                column: "migration_run_id");

            migrationBuilder.CreateIndex(
                name: "migration_exceptions_open_idx",
                table: "migration_exceptions",
                columns: new[] { "severity", "domain" },
                filter: "resolved_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compatibility_outbox");

            migrationBuilder.DropTable(
                name: "group_settings");

            migrationBuilder.DropTable(
                name: "group_settings_redis_sync");

            migrationBuilder.DropTable(
                name: "legacy_redis_records");

            migrationBuilder.DropTable(
                name: "migration_exceptions");

            migrationBuilder.DropTable(
                name: "migration_runs");
        }
    }
}
