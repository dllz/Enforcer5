using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GroupMutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_mutes",
                columns: table => new
                {
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    until_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    muted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    source = table.Column<string>(type: "text", nullable: false, defaultValue: "bot")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_mutes", x => new { x.telegram_chat_id, x.telegram_user_id });
                    table.CheckConstraint("group_mutes_kind_check", "kind IN ('mute', 'tempmute', 'joiner')");
                    table.CheckConstraint("group_mutes_source_check", "source IN ('bot', 'redis')");
                    table.CheckConstraint("group_mutes_until_check", "(kind = 'tempmute') = (until_at IS NOT NULL)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_mutes");
        }
    }
}
