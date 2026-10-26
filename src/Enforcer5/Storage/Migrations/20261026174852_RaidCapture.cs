using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RaidCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_joins",
                columns: table => new
                {
                    join_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "text", nullable: true),
                    invite_link = table.Column<string>(type: "text", nullable: true),
                    invite_link_name = table.Column<string>(type: "text", nullable: true),
                    added_by_account_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_joins", x => x.join_id);
                    table.CheckConstraint("group_joins_source_check", "source IN ('link', 'added', 'request', 'unknown')");
                    table.ForeignKey(
                        name: "FK_group_joins_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_joins_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_group_joins_telegram_accounts_added_by_account_id",
                        column: x => x.added_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    join_source = table.Column<string>(type: "text", nullable: true),
                    invite_link_name = table.Column<string>(type: "text", nullable: true),
                    added_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    message_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    first_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_members", x => new { x.group_id, x.account_id });
                    table.CheckConstraint("group_members_count_check", "message_count >= 0");
                    table.CheckConstraint("group_members_source_check", "join_source IS NULL OR join_source IN ('link', 'added', 'request', 'unknown')");
                    table.ForeignKey(
                        name: "FK_group_members_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_members_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_group_members_telegram_accounts_added_by_account_id",
                        column: x => x.added_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_stats",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    active_members = table.Column<int>(type: "integer", nullable: false),
                    median_messages = table.Column<int>(type: "integer", nullable: false),
                    median_tenure_days = table.Column<double>(type: "double precision", nullable: false),
                    established_messages = table.Column<int>(type: "integer", nullable: false),
                    established_days = table.Column<int>(type: "integer", nullable: false),
                    joins_per_hour = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_stats", x => x.group_id);
                    table.ForeignKey(
                        name: "FK_group_stats_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_fingerprints",
                columns: table => new
                {
                    fingerprint_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_message_id = table.Column<long>(type: "bigint", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: true),
                    text_hash = table.Column<string>(type: "text", nullable: true),
                    media_unique_id = table.Column<string>(type: "text", nullable: true),
                    script = table.Column<string>(type: "text", nullable: true),
                    link_count = table.Column<int>(type: "integer", nullable: false),
                    bare_link = table.Column<bool>(type: "boolean", nullable: false),
                    sample_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_fingerprints", x => x.fingerprint_id);
                    table.CheckConstraint("message_fingerprints_reason_check", "sample_reason IN ('first', 'sample', 'raid')");
                    table.ForeignKey(
                        name: "FK_message_fingerprints_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_message_fingerprints_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "group_joins_account_idx",
                table: "group_joins",
                columns: new[] { "account_id", "joined_at" });

            migrationBuilder.CreateIndex(
                name: "group_joins_group_time_idx",
                table: "group_joins",
                columns: new[] { "group_id", "joined_at" });

            migrationBuilder.CreateIndex(
                name: "group_joins_link_idx",
                table: "group_joins",
                columns: new[] { "group_id", "invite_link" },
                filter: "invite_link IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_group_joins_added_by_account_id",
                table: "group_joins",
                column: "added_by_account_id");

            migrationBuilder.CreateIndex(
                name: "group_members_joined_idx",
                table: "group_members",
                columns: new[] { "group_id", "joined_at" });

            migrationBuilder.CreateIndex(
                name: "group_members_last_message_idx",
                table: "group_members",
                columns: new[] { "group_id", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "IX_group_members_account_id",
                table: "group_members",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_members_added_by_account_id",
                table: "group_members",
                column: "added_by_account_id");

            migrationBuilder.CreateIndex(
                name: "message_fingerprints_account_idx",
                table: "message_fingerprints",
                columns: new[] { "account_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "message_fingerprints_group_time_idx",
                table: "message_fingerprints",
                columns: new[] { "group_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "message_fingerprints_media_idx",
                table: "message_fingerprints",
                columns: new[] { "group_id", "media_unique_id" },
                filter: "media_unique_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "message_fingerprints_text_idx",
                table: "message_fingerprints",
                columns: new[] { "group_id", "text_hash" },
                filter: "text_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_joins");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "group_stats");

            migrationBuilder.DropTable(
                name: "message_fingerprints");
        }
    }
}
