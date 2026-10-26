using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GlobalSanctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "global_sanctions",
                columns: table => new
                {
                    global_sanction_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id_at_time = table.Column<long>(type: "bigint", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    imposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_reason = table.Column<string>(type: "text", nullable: true),
                    legacy_key = table.Column<string>(type: "text", nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_sanctions", x => x.global_sanction_id);
                    table.CheckConstraint("global_sanctions_kind_check", "kind IN ('ban', 'mute')");
                    table.CheckConstraint("global_sanctions_period_check", "(expires_at IS NULL OR expires_at >= imposed_at) AND (resolved_at IS NULL OR resolved_at >= imposed_at)");
                    table.CheckConstraint("global_sanctions_status_check", "status IN ('active', 'revoked', 'completed')");
                    table.ForeignKey(
                        name: "FK_global_sanctions_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_global_sanctions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_global_sanctions_users_user_id_at_time",
                        column: x => x.user_id_at_time,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "global_sanctions_active_idx",
                table: "global_sanctions",
                column: "account_id",
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_global_sanctions_actor_user_id",
                table: "global_sanctions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_global_sanctions_legacy_key",
                table: "global_sanctions",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_global_sanctions_user_id_at_time",
                table: "global_sanctions",
                column: "user_id_at_time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "global_sanctions");
        }
    }
}
