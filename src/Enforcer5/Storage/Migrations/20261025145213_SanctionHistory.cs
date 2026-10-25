using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SanctionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "telegram_accounts",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: false),
                    is_bot = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_accounts", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "telegram_chats",
                columns: table => new
                {
                    chat_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_type = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_chats", x => x.chat_id);
                    table.CheckConstraint("telegram_chats_type_check", "chat_type IN ('unknown', 'private', 'group', 'supergroup', 'channel')");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "telegram_account_profiles",
                columns: table => new
                {
                    profile_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    username = table.Column<string>(type: "text", nullable: true),
                    language_code = table.Column<string>(type: "text", nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_account_profiles", x => x.profile_id);
                    table.CheckConstraint("telegram_account_profiles_validity_check", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "FK_telegram_account_profiles_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    username = table.Column<string>(type: "text", nullable: true),
                    public_link = table.Column<string>(type: "text", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    observed_user_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.group_id);
                    table.CheckConstraint("groups_observed_user_count_check", "observed_user_count >= 0");
                    table.ForeignKey(
                        name: "FK_groups_telegram_chats_chat_id",
                        column: x => x.chat_id,
                        principalTable: "telegram_chats",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "telegram_chat_profiles",
                columns: table => new
                {
                    chat_profile_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true),
                    username = table.Column<string>(type: "text", nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_chat_profiles", x => x.chat_profile_id);
                    table.CheckConstraint("telegram_chat_profiles_validity_check", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "FK_telegram_chat_profiles_telegram_chats_chat_id",
                        column: x => x.chat_id,
                        principalTable: "telegram_chats",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_identity_links",
                columns: table => new
                {
                    identity_link_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    linked_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    unlinked_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    unlinked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_identity_links", x => x.identity_link_id);
                    table.CheckConstraint("user_identity_links_period_check", "unlinked_at IS NULL OR unlinked_at >= linked_at");
                    table.ForeignKey(
                        name: "FK_user_identity_links_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_identity_links_users_linked_by_user_id",
                        column: x => x.linked_by_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_identity_links_users_unlinked_by_user_id",
                        column: x => x.unlinked_by_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_identity_links_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sanctions",
                columns: table => new
                {
                    sanction_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id_at_time = table.Column<long>(type: "bigint", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    trigger_warning_event_id = table.Column<long>(type: "bigint", nullable: true),
                    report_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    imposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    edition = table.Column<string>(type: "text", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_by = table.Column<string>(type: "text", nullable: true),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sanctions", x => x.sanction_id);
                    table.CheckConstraint("sanctions_claim_check", "(claimed_at IS NULL) = (claimed_by IS NULL)");
                    table.CheckConstraint("sanctions_kind_check", "kind IN ('ban', 'kick', 'mute', 'tempban', 'tempmute')");
                    table.CheckConstraint("sanctions_period_check", "(expires_at IS NULL OR expires_at >= imposed_at) AND (resolved_at IS NULL OR resolved_at >= imposed_at)");
                    table.CheckConstraint("sanctions_status_check", "status IN ('active', 'due', 'revoked', 'completed')");
                    table.ForeignKey(
                        name: "FK_sanctions_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sanctions_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sanctions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sanctions_users_user_id_at_time",
                        column: x => x.user_id_at_time,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_groups_chat_id",
                table: "groups",
                column: "chat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_account_id",
                table: "sanctions",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_actor_user_id",
                table: "sanctions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_legacy_key",
                table: "sanctions",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_user_id_at_time",
                table: "sanctions",
                column: "user_id_at_time");

            migrationBuilder.CreateIndex(
                name: "sanctions_due_idx",
                table: "sanctions",
                columns: new[] { "edition", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "sanctions_group_account_idx",
                table: "sanctions",
                columns: new[] { "group_id", "account_id", "imposed_at" });

            migrationBuilder.CreateIndex(
                name: "telegram_account_profiles_open_idx",
                table: "telegram_account_profiles",
                column: "account_id",
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "telegram_account_profiles_username_ci_idx",
                table: "telegram_account_profiles",
                column: "username",
                filter: "username IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_accounts_telegram_user_id",
                table: "telegram_accounts",
                column: "telegram_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "telegram_chat_profiles_open_idx",
                table: "telegram_chat_profiles",
                column: "chat_id",
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "telegram_chat_profiles_username_ci_idx",
                table: "telegram_chat_profiles",
                column: "username",
                filter: "username IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_telegram_chats_telegram_chat_id",
                table: "telegram_chats",
                column: "telegram_chat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_identity_links_linked_by_user_id",
                table: "user_identity_links",
                column: "linked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_identity_links_unlinked_by_user_id",
                table: "user_identity_links",
                column: "unlinked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_identity_links_user_id_account_id_linked_at",
                table: "user_identity_links",
                columns: new[] { "user_id", "account_id", "linked_at" });

            migrationBuilder.CreateIndex(
                name: "user_identity_links_active_account_idx",
                table: "user_identity_links",
                column: "account_id",
                unique: true,
                filter: "unlinked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sanctions");

            migrationBuilder.DropTable(
                name: "telegram_account_profiles");

            migrationBuilder.DropTable(
                name: "telegram_chat_profiles");

            migrationBuilder.DropTable(
                name: "user_identity_links");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "telegram_accounts");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "telegram_chats");
        }
    }
}
