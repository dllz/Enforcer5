using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProfileLookups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_profile_flags",
                columns: table => new
                {
                    profile_flags_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    flags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    personal_channel_id = table.Column<long>(type: "bigint", nullable: true),
                    bio_script = table.Column<string>(type: "text", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_profile_flags", x => x.profile_flags_id);
                    table.ForeignKey(
                        name: "FK_account_profile_flags_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channel_profiles",
                columns: table => new
                {
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_type = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    flags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    member_count = table.Column<int>(type: "integer", nullable: true),
                    linked_chat_id = table.Column<long>(type: "bigint", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_profiles", x => x.telegram_chat_id);
                    table.CheckConstraint("channel_profiles_type_check", "chat_type IN ('channel', 'group', 'supergroup', 'private')");
                });

            migrationBuilder.CreateIndex(
                name: "account_profile_flags_account_idx",
                table: "account_profile_flags",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "channel_profiles_username_idx",
                table: "channel_profiles",
                column: "username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_profile_flags");

            migrationBuilder.DropTable(
                name: "channel_profiles");
        }
    }
}
