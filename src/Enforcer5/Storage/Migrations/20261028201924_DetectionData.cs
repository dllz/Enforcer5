using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DetectionData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_reputation",
                columns: table => new
                {
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    bad_members = table.Column<int>(type: "integer", nullable: false),
                    fine_members = table.Column<int>(type: "integer", nullable: false),
                    bad_groups = table.Column<int>(type: "integer", nullable: false),
                    groups = table.Column<int>(type: "integer", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_reputation", x => x.channel_id);
                });

            migrationBuilder.CreateTable(
                name: "domain_reputation",
                columns: table => new
                {
                    domain = table.Column<string>(type: "text", nullable: false),
                    bad_members = table.Column<int>(type: "integer", nullable: false),
                    fine_members = table.Column<int>(type: "integer", nullable: false),
                    bad_groups = table.Column<int>(type: "integer", nullable: false),
                    groups = table.Column<int>(type: "integer", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_reputation", x => x.domain);
                });

            migrationBuilder.CreateTable(
                name: "member_pointers",
                columns: table => new
                {
                    pointer_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_pointers", x => x.pointer_id);
                    table.CheckConstraint("member_pointers_kind_check", "kind IN ('channel', 'domain')");
                    table.ForeignKey(
                        name: "FK_member_pointers_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_member_pointers_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "member_signals",
                columns: table => new
                {
                    signal_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    account_id_range = table.Column<int>(type: "integer", nullable: true),
                    has_username = table.Column<bool>(type: "boolean", nullable: true),
                    is_premium = table.Column<bool>(type: "boolean", nullable: true),
                    language_code = table.Column<string>(type: "text", nullable: true),
                    name_script = table.Column<string>(type: "text", nullable: true),
                    name_letters = table.Column<int>(type: "integer", nullable: true),
                    name_emoji = table.Column<bool>(type: "boolean", nullable: true),
                    name_digits = table.Column<bool>(type: "boolean", nullable: true),
                    name_staff_word = table.Column<bool>(type: "boolean", nullable: true),
                    name_lookalikes = table.Column<bool>(type: "boolean", nullable: true),
                    profile_flags = table.Column<string[]>(type: "text[]", nullable: true),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    join_source = table.Column<string>(type: "text", nullable: true),
                    invite_link_name = table.Column<string>(type: "text", nullable: true),
                    added_by_member = table.Column<bool>(type: "boolean", nullable: true),
                    joined_during_raid = table.Column<bool>(type: "boolean", nullable: false),
                    first_post_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_post_type = table.Column<string>(type: "text", nullable: true),
                    first_post_delay_seconds = table.Column<int>(type: "integer", nullable: true),
                    links = table.Column<int>(type: "integer", nullable: false),
                    mentions = table.Column<int>(type: "integer", nullable: false),
                    forwards = table.Column<int>(type: "integer", nullable: false),
                    active_hours = table.Column<int>(type: "integer", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_signals", x => x.signal_id);
                    table.CheckConstraint("member_signals_counts_check", "links >= 0 AND mentions >= 0 AND forwards >= 0 AND max_score >= 0");
                    table.ForeignKey(
                        name: "FK_member_signals_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_member_signals_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_member_pointers_account_id",
                table: "member_pointers",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "member_pointers_member_idx",
                table: "member_pointers",
                columns: new[] { "group_id", "account_id", "kind", "value" },
                unique: true,
                filter: "account_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "member_pointers_value_idx",
                table: "member_pointers",
                columns: new[] { "kind", "value" });

            migrationBuilder.CreateIndex(
                name: "IX_member_signals_account_id",
                table: "member_signals",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "member_signals_member_idx",
                table: "member_signals",
                columns: new[] { "group_id", "account_id" },
                unique: true,
                filter: "account_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "member_signals_updated_idx",
                table: "member_signals",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_reputation");

            migrationBuilder.DropTable(
                name: "domain_reputation");

            migrationBuilder.DropTable(
                name: "member_pointers");

            migrationBuilder.DropTable(
                name: "member_signals");
        }
    }
}
