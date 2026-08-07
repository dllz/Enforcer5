using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PrincipalContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    chat_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.chat_id);
                    table.CheckConstraint("groups_id_check", "chat_id <> 0");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: true),
                    username = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.user_id);
                    table.CheckConstraint("users_id_check", "user_id <> 0");
                });

            migrationBuilder.CreateTable(
                name: "custom_commands",
                columns: table => new
                {
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    media_type = table.Column<string>(type: "text", nullable: true),
                    file_id = table.Column<string>(type: "text", nullable: true),
                    caption = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_commands", x => new { x.chat_id, x.trigger });
                    table.ForeignKey(
                        name: "FK_custom_commands_groups_chat_id",
                        column: x => x.chat_id,
                        principalTable: "groups",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_content_items",
                columns: table => new
                {
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_content_items", x => new { x.chat_id, x.content_type });
                    table.CheckConstraint("group_content_type_check", "content_type IN ('rules', 'about', 'language', 'link')");
                    table.ForeignKey(
                        name: "FK_group_content_items_groups_chat_id",
                        column: x => x.chat_id,
                        principalTable: "groups",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_entitlements",
                columns: table => new
                {
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    entitlement = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_entitlements", x => new { x.chat_id, x.entitlement });
                    table.ForeignKey(
                        name: "FK_group_entitlements_groups_chat_id",
                        column: x => x.chat_id,
                        principalTable: "groups",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                filter: "username IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_commands");

            migrationBuilder.DropTable(
                name: "group_content_items");

            migrationBuilder.DropTable(
                name: "group_entitlements");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "groups");
        }
    }
}
