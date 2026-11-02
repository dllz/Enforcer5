using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Batches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "member_labels_source_check",
                table: "member_labels");

            migrationBuilder.CreateTable(
                name: "batches",
                columns: table => new
                {
                    batch_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    edition = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: true),
                    command_text = table.Column<string>(type: "text", nullable: false),
                    requested_by_account_id = table.Column<long>(type: "bigint", nullable: false),
                    confirmed_by_account_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "running"),
                    progress_message_id = table.Column<int>(type: "integer", nullable: true),
                    skipped = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batches", x => x.batch_id);
                    table.CheckConstraint("batches_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("batches_kind_check", "kind IN ('ban', 'tempban', 'unban', 'kick', 'mute', 'tempmute', 'unmute', 'warn', 'prewarn', 'removewarn', 'resetwarns')");
                    table.CheckConstraint("batches_status_check", "status IN ('running', 'done', 'stopped')");
                    table.ForeignKey(
                        name: "FK_batches_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_batches_telegram_accounts_confirmed_by_account_id",
                        column: x => x.confirmed_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_batches_telegram_accounts_requested_by_account_id",
                        column: x => x.requested_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "batch_items",
                columns: table => new
                {
                    batch_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch_items", x => new { x.batch_id, x.position });
                    table.CheckConstraint("batch_items_outcome_check", "outcome IS NULL OR outcome IN ('done', 'refused', 'blocked', 'nothing', 'skipped')");
                    table.ForeignKey(
                        name: "FK_batch_items_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "batches",
                        principalColumn: "batch_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "member_labels_source_check",
                table: "member_labels",
                sql: "source IN ('command', 'button', 'report', 'app', 'auto', 'raid', 'global', 'established', 'batch', 'script')");

            migrationBuilder.CreateIndex(
                name: "batches_edition_status_idx",
                table: "batches",
                columns: new[] { "edition", "status" });

            migrationBuilder.CreateIndex(
                name: "batches_one_running_idx",
                table: "batches",
                column: "group_id",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "IX_batches_confirmed_by_account_id",
                table: "batches",
                column: "confirmed_by_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_batches_requested_by_account_id",
                table: "batches",
                column: "requested_by_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "batch_items");

            migrationBuilder.DropTable(
                name: "batches");

            migrationBuilder.DropCheckConstraint(
                name: "member_labels_source_check",
                table: "member_labels");

            migrationBuilder.AddCheckConstraint(
                name: "member_labels_source_check",
                table: "member_labels",
                sql: "source IN ('command', 'button', 'report', 'app', 'auto', 'raid', 'global', 'established')");
        }
    }
}
