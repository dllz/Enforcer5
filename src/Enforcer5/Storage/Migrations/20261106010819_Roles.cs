using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Roles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bot_bans",
                columns: table => new
                {
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    banned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    banned_by_account_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_bans", x => x.telegram_id);
                    table.ForeignKey(
                        name: "FK_bot_bans_telegram_accounts_banned_by_account_id",
                        column: x => x.banned_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "global_role_assignments",
                columns: table => new
                {
                    assignment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_role_assignments", x => x.assignment_id);
                    table.CheckConstraint("global_role_assignments_period_check", "revoked_at IS NULL OR revoked_at >= assigned_at");
                    table.CheckConstraint("global_role_assignments_role_check", "role IN ('developer', 'global_admin', 'global_ban_admin', 'language_admin')");
                    table.ForeignKey(
                        name: "FK_global_role_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_elevation_blocks",
                columns: table => new
                {
                    block_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_elevation_blocks", x => x.block_id);
                    table.CheckConstraint("group_elevation_blocks_period_check", "revoked_at IS NULL OR revoked_at >= blocked_at");
                    table.ForeignKey(
                        name: "FK_group_elevation_blocks_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_elevation_blocks_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_entitlements",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    entitlement = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_entitlements", x => new { x.group_id, x.entitlement });
                    table.CheckConstraint("group_entitlements_entitlement_check", "entitlement IN ('premium')");
                    table.ForeignKey(
                        name: "FK_group_entitlements_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_role_assignments",
                columns: table => new
                {
                    assignment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_role_assignments", x => x.assignment_id);
                    table.CheckConstraint("group_role_assignments_period_check", "revoked_at IS NULL OR revoked_at >= assigned_at");
                    table.CheckConstraint("group_role_assignments_role_check", "role IN ('authorized', 'moderator')");
                    table.ForeignKey(
                        name: "FK_group_role_assignments_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_role_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_user_preferences",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    admin_notifications_disabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    reports_blocked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    watch_disabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    tag_all_subscribed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_user_preferences", x => new { x.group_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_group_user_preferences_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_user_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bot_bans_banned_by_account_id",
                table: "bot_bans",
                column: "banned_by_account_id");

            migrationBuilder.CreateIndex(
                name: "global_role_assignments_active_idx",
                table: "global_role_assignments",
                columns: new[] { "user_id", "role" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "group_elevation_blocks_active_idx",
                table: "group_elevation_blocks",
                columns: new[] { "group_id", "user_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_group_elevation_blocks_user_id",
                table: "group_elevation_blocks",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "group_role_assignments_active_idx",
                table: "group_role_assignments",
                columns: new[] { "group_id", "user_id", "role" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_group_role_assignments_user_id",
                table: "group_role_assignments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_user_preferences_user_id",
                table: "group_user_preferences",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bot_bans");

            migrationBuilder.DropTable(
                name: "global_role_assignments");

            migrationBuilder.DropTable(
                name: "group_elevation_blocks");

            migrationBuilder.DropTable(
                name: "group_entitlements");

            migrationBuilder.DropTable(
                name: "group_role_assignments");

            migrationBuilder.DropTable(
                name: "group_user_preferences");
        }
    }
}
