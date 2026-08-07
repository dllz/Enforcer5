using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RolesWarnings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_elevation_blocks",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_elevation_blocks", x => new { x.group_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_group_elevation_blocks_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_elevation_blocks_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_role_assignments",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_role_assignments", x => new { x.group_id, x.user_id, x.role });
                    table.CheckConstraint("group_role_assignments_role_check", "role IN ('authorized', 'moderator')");
                    table.ForeignKey(
                        name: "FK_group_role_assignments_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_role_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_user_policies",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    policy = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_user_policies", x => new { x.group_id, x.user_id, x.policy });
                    table.ForeignKey(
                        name: "FK_group_user_policies_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_user_policies_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "warning_balances",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    warning_type = table.Column<string>(type: "text", nullable: false),
                    active_quantity = table.Column<long>(type: "bigint", nullable: false),
                    lifetime_quantity = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warning_balances", x => new { x.group_id, x.user_id, x.warning_type });
                    table.CheckConstraint("warning_balances_active_check", "active_quantity >= 0");
                    table.CheckConstraint("warning_balances_lifetime_check", "lifetime_quantity >= 0");
                    table.CheckConstraint("warning_balances_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                    table.ForeignKey(
                        name: "FK_warning_balances_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "chat_id",
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
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    warning_type = table.Column<string>(type: "text", nullable: true),
                    operation = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    active_quantity = table.Column<long>(type: "bigint", nullable: false),
                    lifetime_quantity = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warning_events", x => x.id);
                    table.CheckConstraint("warning_events_quantity_check", "quantity >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_group_elevation_blocks_user_id",
                table: "group_elevation_blocks",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_role_assignments_user_id",
                table: "group_role_assignments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_user_policies_user_id",
                table: "group_user_policies",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_balances_user_id",
                table: "warning_balances",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_group_id_user_id_warning_type_created_at",
                table: "warning_events",
                columns: new[] { "group_id", "user_id", "warning_type", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_elevation_blocks");

            migrationBuilder.DropTable(
                name: "group_role_assignments");

            migrationBuilder.DropTable(
                name: "group_user_policies");

            migrationBuilder.DropTable(
                name: "warning_balances");

            migrationBuilder.DropTable(
                name: "warning_events");
        }
    }
}
