using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GroupBots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bot_events",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_account_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_account_id = table.Column<long>(type: "bigint", nullable: true),
                    actor_was_admin = table.Column<bool>(type: "boolean", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    edition = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_events", x => x.event_id);
                    table.CheckConstraint("bot_events_action_check", "action IN ('none', 'banned')");
                    table.CheckConstraint("bot_events_edition_check", "edition IN ('normal', 'premium')");
                    table.CheckConstraint("bot_events_kind_check", "kind IN ('added', 'summoned', 'trusted', 'untrusted')");
                    table.ForeignKey(
                        name: "FK_bot_events_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_bot_events_telegram_accounts_actor_account_id",
                        column: x => x.actor_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bot_events_telegram_accounts_bot_account_id",
                        column: x => x.bot_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_bots",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    set_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_bots", x => new { x.group_id, x.account_id });
                    table.CheckConstraint("group_bots_source_check", "source IN ('admin_added', 'command', 'present', 'member_added', 'summoned')");
                    table.CheckConstraint("group_bots_status_check", "status IN ('allowed', 'blocked')");
                    table.ForeignKey(
                        name: "FK_group_bots_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_bots_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_bots_telegram_accounts_set_by_account_id",
                        column: x => x.set_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "bot_events_actor_idx",
                table: "bot_events",
                columns: new[] { "actor_account_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "bot_events_group_idx",
                table: "bot_events",
                columns: new[] { "group_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_bot_events_bot_account_id",
                table: "bot_events",
                column: "bot_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_bots_account_id",
                table: "group_bots",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_bots_set_by_account_id",
                table: "group_bots",
                column: "set_by_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bot_events");

            migrationBuilder.DropTable(
                name: "group_bots");
        }
    }
}
