using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WarningHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warning_balances",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    warning_type = table.Column<string>(type: "text", nullable: false),
                    active_quantity = table.Column<long>(type: "bigint", nullable: false),
                    lifetime_quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warning_balances", x => new { x.group_id, x.user_id, x.warning_type });
                    table.CheckConstraint("warning_balances_quantity_check", "active_quantity >= 0 AND lifetime_quantity >= 0");
                    table.CheckConstraint("warning_balances_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                    table.ForeignKey(
                        name: "FK_warning_balances_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_warning_balances_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "warning_events",
                columns: table => new
                {
                    warning_event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id_at_time = table.Column<long>(type: "bigint", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    report_id = table.Column<long>(type: "bigint", nullable: true),
                    warning_type = table.Column<string>(type: "text", nullable: true),
                    operation = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warning_events", x => x.warning_event_id);
                    table.UniqueConstraint("AK_warning_events_group_id_account_id_warning_event_id", x => new { x.group_id, x.account_id, x.warning_event_id });
                    table.CheckConstraint("warning_events_operation_check", "operation IN ('add', 'remove', 'reset', 'decay')");
                    table.CheckConstraint("warning_events_quantity_check", "quantity > 0");
                    table.CheckConstraint("warning_events_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                    table.ForeignKey(
                        name: "FK_warning_events_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_warning_events_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warning_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warning_events_users_user_id_at_time",
                        column: x => x.user_id_at_time,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_group_id_account_id_trigger_warning_event_id",
                table: "sanctions",
                columns: new[] { "group_id", "account_id", "trigger_warning_event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_warning_balances_user_id",
                table: "warning_balances",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_account_id",
                table: "warning_events",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_actor_user_id",
                table: "warning_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_group_id_account_id_occurred_at",
                table: "warning_events",
                columns: new[] { "group_id", "account_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_legacy_key",
                table: "warning_events",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_user_id_at_time",
                table: "warning_events",
                column: "user_id_at_time");

            migrationBuilder.AddForeignKey(
                name: "FK_sanctions_warning_events_group_id_account_id_trigger_warnin~",
                table: "sanctions",
                columns: new[] { "group_id", "account_id", "trigger_warning_event_id" },
                principalTable: "warning_events",
                principalColumns: new[] { "group_id", "account_id", "warning_event_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sanctions_warning_events_group_id_account_id_trigger_warnin~",
                table: "sanctions");

            migrationBuilder.DropTable(
                name: "warning_balances");

            migrationBuilder.DropTable(
                name: "warning_events");

            migrationBuilder.DropIndex(
                name: "IX_sanctions_group_id_account_id_trigger_warning_event_id",
                table: "sanctions");
        }
    }
}
