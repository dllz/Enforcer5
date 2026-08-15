using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedStorageBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_flush_batches",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    edition = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_flush_batches", x => x.batch_id);
                });

            migrationBuilder.CreateTable(
                name: "bot_statistic_baselines",
                columns: table => new
                {
                    edition = table.Column<string>(type: "text", nullable: false),
                    message_count = table.Column<long>(type: "bigint", nullable: false),
                    user_count = table.Column<long>(type: "bigint", nullable: false),
                    ban_count = table.Column<long>(type: "bigint", nullable: false),
                    kick_count = table.Column<long>(type: "bigint", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_statistic_baselines", x => x.edition);
                    table.CheckConstraint("bot_statistic_baselines_counts_check", "message_count >= 0 AND user_count >= 0 AND ban_count >= 0 AND kick_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "compatibility_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: false),
                    ordering_key = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compatibility_outbox", x => x.id);
                    table.CheckConstraint("compatibility_outbox_attempt_count_check", "attempt_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "migration_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    mode = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    source_snapshot = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_runs", x => x.id);
                    table.CheckConstraint("migration_runs_status_check", "status IN ('running', 'completed', 'failed')");
                });

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
                name: "legacy_redis_records",
                columns: table => new
                {
                    source_key = table.Column<string>(type: "text", nullable: false),
                    source_field = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    raw_value = table.Column<string>(type: "text", nullable: true),
                    source_ttl_ms = table.Column<long>(type: "bigint", nullable: true),
                    source_checksum = table.Column<string>(type: "text", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    migration_run_id = table.Column<long>(type: "bigint", nullable: true),
                    disposition = table.Column<string>(type: "text", nullable: true, defaultValue: "pending"),
                    target_reference = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_redis_records", x => new { x.source_key, x.source_field });
                    table.CheckConstraint("legacy_redis_records_disposition_check", "disposition IN ('pending', 'migrated', 'retained', 'excluded', 'quarantined')");
                    table.ForeignKey(
                        name: "FK_legacy_redis_records_migration_runs_migration_run_id",
                        column: x => x.migration_run_id,
                        principalTable: "migration_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "migration_exceptions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    migration_run_id = table.Column<long>(type: "bigint", nullable: true),
                    domain = table.Column<string>(type: "text", nullable: false),
                    source_key = table.Column<string>(type: "text", nullable: true),
                    source_field = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    raw_value = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_exceptions", x => x.id);
                    table.CheckConstraint("migration_exceptions_severity_check", "severity IN ('info', 'warning', 'blocking')");
                    table.ForeignKey(
                        name: "FK_migration_exceptions_migration_runs_migration_run_id",
                        column: x => x.migration_run_id,
                        principalTable: "migration_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "account_message_totals",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    message_count = table.Column<long>(type: "bigint", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_message_totals", x => x.account_id);
                    table.CheckConstraint("account_message_totals_count_check", "message_count >= 0");
                    table.ForeignKey(
                        name: "FK_account_message_totals_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "global_role_assignments",
                columns: table => new
                {
                    assignment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "text", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_role_assignments", x => x.assignment_id);
                    table.CheckConstraint("global_role_assignments_period_check", "revoked_at IS NULL OR revoked_at >= assigned_at");
                    table.CheckConstraint("global_role_assignments_role_check", "role IN ('developer', 'global_admin', 'global_ban_admin')");
                    table.ForeignKey(
                        name: "FK_global_role_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                    legacy_key = table.Column<string>(type: "text", nullable: true)
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
                name: "analytics_flush_batch_items",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    message_delta = table.Column<long>(type: "bigint", nullable: false),
                    active_user_delta = table.Column<long>(type: "bigint", nullable: false),
                    join_delta = table.Column<long>(type: "bigint", nullable: false),
                    leave_delta = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_flush_batch_items", x => new { x.batch_id, x.group_id, x.account_id, x.day });
                    table.CheckConstraint("analytics_flush_batch_items_deltas_check", "message_delta >= 0 AND active_user_delta >= 0 AND join_delta >= 0 AND leave_delta >= 0");
                    table.ForeignKey(
                        name: "FK_analytics_flush_batch_items_analytics_flush_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "analytics_flush_batches",
                        principalColumn: "batch_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_analytics_flush_batch_items_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_analytics_flush_batch_items_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_commands",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    media_type = table.Column<string>(type: "text", nullable: true),
                    file_id = table.Column<string>(type: "text", nullable: true),
                    file_owner_edition = table.Column<string>(type: "text", nullable: true),
                    caption = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_commands", x => new { x.group_id, x.trigger });
                    table.CheckConstraint("custom_commands_file_owner_check", "(file_id IS NULL) = (file_owner_edition IS NULL) AND (file_owner_edition IS NULL OR file_owner_edition IN ('normal', 'premium', 'legacy_unknown'))");
                    table.CheckConstraint("custom_commands_media_type_check", "media_type IS NULL OR media_type IN ('animation', 'audio', 'document', 'photo', 'sticker', 'video', 'video_note', 'voice')");
                    table.ForeignKey(
                        name: "FK_custom_commands_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_account_daily_analytics",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    message_count = table.Column<long>(type: "bigint", nullable: false),
                    first_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_account_daily_analytics", x => new { x.group_id, x.account_id, x.day });
                    table.CheckConstraint("group_account_daily_analytics_count_check", "message_count >= 0");
                    table.ForeignKey(
                        name: "FK_group_account_daily_analytics_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_account_daily_analytics_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_account_message_totals",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    message_count = table.Column<long>(type: "bigint", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_account_message_totals", x => new { x.group_id, x.account_id });
                    table.CheckConstraint("group_account_message_totals_count_check", "message_count >= 0");
                    table.ForeignKey(
                        name: "FK_group_account_message_totals_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_account_message_totals_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_account_observations",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_account_observations", x => new { x.group_id, x.account_id });
                    table.CheckConstraint("group_account_observations_seen_check", "last_seen_at >= first_seen_at");
                    table.ForeignKey(
                        name: "FK_group_account_observations_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_account_observations_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_audit_events",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    subject_user_id = table.Column<long>(type: "bigint", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_audit_events", x => x.event_id);
                    table.ForeignKey(
                        name: "FK_group_audit_events_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_audit_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_group_audit_events_users_subject_user_id",
                        column: x => x.subject_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_character_policies",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    character_class = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_character_policies", x => new { x.group_id, x.character_class });
                    table.CheckConstraint("group_character_policies_action_check", "action IN ('allow', 'delete', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute')");
                    table.ForeignKey(
                        name: "FK_group_character_policies_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_content_items",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_content_items", x => new { x.group_id, x.content_type });
                    table.CheckConstraint("group_content_items_type_check", "content_type IN ('rules', 'about', 'language', 'link')");
                    table.ForeignKey(
                        name: "FK_group_content_items_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_daily_analytics",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    message_count = table.Column<long>(type: "bigint", nullable: false),
                    active_user_count = table.Column<long>(type: "bigint", nullable: false),
                    join_count = table.Column<long>(type: "bigint", nullable: false),
                    leave_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_daily_analytics", x => new { x.group_id, x.day });
                    table.CheckConstraint("group_daily_analytics_counts_check", "message_count >= 0 AND active_user_count >= 0 AND join_count >= 0 AND leave_count >= 0");
                    table.ForeignKey(
                        name: "FK_group_daily_analytics_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
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
                    table.ForeignKey(
                        name: "FK_group_entitlements_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_flood_exceptions",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    excluded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_flood_exceptions", x => new { x.group_id, x.content_type });
                    table.ForeignKey(
                        name: "FK_group_flood_exceptions_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_length_policies",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    maximum_characters = table.Column<int>(type: "integer", nullable: false),
                    maximum_lines = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_length_policies", x => new { x.group_id, x.scope });
                    table.CheckConstraint("group_length_policies_action_check", "action IN ('allow', 'delete', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute')");
                    table.CheckConstraint("group_length_policies_limits_check", "maximum_characters >= 0 AND maximum_lines >= 0");
                    table.CheckConstraint("group_length_policies_scope_check", "scope IN ('message', 'caption')");
                    table.ForeignKey(
                        name: "FK_group_length_policies_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_media_policies",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    media_type = table.Column<string>(type: "text", nullable: false),
                    allowed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_media_policies", x => new { x.group_id, x.media_type });
                    table.CheckConstraint("group_media_policies_type_check", "media_type IN ('animation', 'audio', 'contact', 'document', 'game', 'location', 'photo', 'poll', 'sticker', 'video', 'video_note', 'voice')");
                    table.ForeignKey(
                        name: "FK_group_media_policies_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_membership_periods",
                columns: table => new
                {
                    membership_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id_at_time = table.Column<long>(type: "bigint", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_membership_periods", x => x.membership_id);
                    table.CheckConstraint("group_membership_periods_joined_left_check", "left_at IS NULL OR joined_at IS NULL OR left_at >= joined_at");
                    table.CheckConstraint("group_membership_periods_seen_check", "last_seen_at >= first_seen_at");
                    table.ForeignKey(
                        name: "FK_group_membership_periods_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_membership_periods_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_group_membership_periods_users_user_id_at_time",
                        column: x => x.user_id_at_time,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_role_assignments",
                columns: table => new
                {
                    assignment_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "text", nullable: true),
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
                name: "group_settings",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    log_chat_id = table.Column<long>(type: "bigint", nullable: true),
                    reports_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    welcome_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    flood_protection_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    delete_last_welcome = table.Column<bool>(type: "boolean", nullable: false),
                    mute_on_join = table.Column<bool>(type: "boolean", nullable: false),
                    maximum_messages = table.Column<short>(type: "smallint", nullable: false),
                    flood_action = table.Column<string>(type: "text", nullable: true),
                    maximum_warnings = table.Column<short>(type: "smallint", nullable: false),
                    warning_action = table.Column<string>(type: "text", nullable: true),
                    media_warning_limit = table.Column<short>(type: "smallint", nullable: false),
                    media_action = table.Column<string>(type: "text", nullable: true),
                    tempban_minutes = table.Column<int>(type: "integer", nullable: false),
                    tempmute_minutes = table.Column<int>(type: "integer", nullable: false),
                    welcome_format = table.Column<string>(type: "text", nullable: true),
                    welcome_content = table.Column<string>(type: "text", nullable: true),
                    welcome_media_file_id = table.Column<string>(type: "text", nullable: true),
                    welcome_media_file_owner_edition = table.Column<string>(type: "text", nullable: true),
                    rules_private_only = table.Column<bool>(type: "boolean", nullable: false),
                    about_private_only = table.Column<bool>(type: "boolean", nullable: false),
                    modlist_private_only = table.Column<bool>(type: "boolean", nullable: false),
                    extra_private_only = table.Column<bool>(type: "boolean", nullable: false),
                    help_private_only = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_settings", x => x.group_id);
                    table.CheckConstraint("group_settings_actions_check", "flood_action IN ('none', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute', 'delete') AND warning_action IN ('none', 'kick', 'ban', 'mute', 'tempban', 'tempmute') AND media_action IN ('none', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute', 'delete')");
                    table.CheckConstraint("group_settings_limits_check", "maximum_messages >= 0 AND maximum_warnings >= 0 AND media_warning_limit >= 0 AND tempban_minutes >= 0 AND tempmute_minutes >= 0");
                    table.CheckConstraint("group_settings_version_check", "version > 0");
                    table.CheckConstraint("group_settings_welcome_file_owner_check", "(welcome_media_file_id IS NULL) = (welcome_media_file_owner_edition IS NULL) AND (welcome_media_file_owner_edition IS NULL OR welcome_media_file_owner_edition IN ('normal', 'premium', 'legacy_unknown'))");
                    table.CheckConstraint("group_settings_welcome_format_check", "welcome_format IS NULL OR welcome_format IN ('text', 'markdown', 'html')");
                    table.ForeignKey(
                        name: "FK_group_settings_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_settings_telegram_chats_log_chat_id",
                        column: x => x.log_chat_id,
                        principalTable: "telegram_chats",
                        principalColumn: "chat_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "group_user_daily_analytics",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    message_count = table.Column<long>(type: "bigint", nullable: false),
                    first_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_user_daily_analytics", x => new { x.group_id, x.user_id, x.day });
                    table.CheckConstraint("group_user_daily_analytics_count_check", "message_count >= 0");
                    table.ForeignKey(
                        name: "FK_group_user_daily_analytics_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_user_daily_analytics_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_user_observations",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_user_observations", x => new { x.group_id, x.user_id });
                    table.CheckConstraint("group_user_observations_seen_check", "last_seen_at >= first_seen_at");
                    table.ForeignKey(
                        name: "FK_group_user_observations_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_user_observations_users_user_id",
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

            migrationBuilder.CreateTable(
                name: "message_metadata",
                columns: table => new
                {
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_message_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id_at_time = table.Column<long>(type: "bigint", nullable: true),
                    reply_to_message_id = table.Column<long>(type: "bigint", nullable: true),
                    message_type = table.Column<string>(type: "text", nullable: true),
                    has_media = table.Column<bool>(type: "boolean", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_metadata", x => new { x.group_id, x.telegram_message_id });
                    table.CheckConstraint("message_metadata_dates_check", "(edited_at IS NULL OR edited_at >= sent_at) AND (deleted_at IS NULL OR deleted_at >= sent_at)");
                    table.ForeignKey(
                        name: "FK_message_metadata_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_message_metadata_message_metadata_group_id_reply_to_message~",
                        columns: x => new { x.group_id, x.reply_to_message_id },
                        principalTable: "message_metadata",
                        principalColumns: new[] { "group_id", "telegram_message_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_metadata_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_metadata_users_user_id_at_time",
                        column: x => x.user_id_at_time,
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
                    lifetime_quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warning_balances", x => new { x.group_id, x.user_id, x.warning_type });
                    table.CheckConstraint("warning_balances_quantity_check", "active_quantity >= 0 AND lifetime_quantity >= 0");
                    table.CheckConstraint("warning_balances_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                    table.ForeignKey(
                        name: "FK_warning_balances_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_warning_balances_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    report_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    reporter_user_id = table.Column<long>(type: "bigint", nullable: true),
                    reporter_account_id = table.Column<long>(type: "bigint", nullable: true),
                    reporter_display = table.Column<string>(type: "text", nullable: true),
                    target_user_id = table.Column<long>(type: "bigint", nullable: true),
                    target_account_id = table.Column<long>(type: "bigint", nullable: true),
                    target_display = table.Column<string>(type: "text", nullable: true),
                    solver_user_id = table.Column<long>(type: "bigint", nullable: true),
                    solver_display = table.Column<string>(type: "text", nullable: true),
                    report_message_id = table.Column<long>(type: "bigint", nullable: false),
                    target_message_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    resolution_action = table.Column<string>(type: "text", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.report_id);
                    table.CheckConstraint("reports_resolution_action_check", "resolution_action IS NULL OR resolution_action IN ('solved', 'dismissed', 'warned', 'kicked', 'banned', 'deleted')");
                    table.CheckConstraint("reports_resolution_check", "(status = 'open' AND resolved_at IS NULL AND resolution_action IS NULL) OR (status = 'resolved' AND resolved_at IS NOT NULL AND resolution_action IS NOT NULL)");
                    table.CheckConstraint("reports_status_check", "status IN ('open', 'resolved')");
                    table.ForeignKey(
                        name: "FK_reports_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reports_message_metadata_group_id_report_message_id",
                        columns: x => new { x.group_id, x.report_message_id },
                        principalTable: "message_metadata",
                        principalColumns: new[] { "group_id", "telegram_message_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_message_metadata_group_id_target_message_id",
                        columns: x => new { x.group_id, x.target_message_id },
                        principalTable: "message_metadata",
                        principalColumns: new[] { "group_id", "telegram_message_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_telegram_accounts_reporter_account_id",
                        column: x => x.reporter_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_telegram_accounts_target_account_id",
                        column: x => x.target_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_users_reporter_user_id",
                        column: x => x.reporter_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_users_solver_user_id",
                        column: x => x.solver_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_users_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "warning_events",
                columns: table => new
                {
                    warning_event_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id_at_time = table.Column<long>(type: "bigint", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    report_id = table.Column<long>(type: "bigint", nullable: true),
                    warning_type = table.Column<string>(type: "text", nullable: true),
                    operation = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legacy_key = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warning_events", x => x.warning_event_id);
                    table.UniqueConstraint("AK_warning_events_group_id_account_id_warning_event_id", x => new { x.group_id, x.account_id, x.warning_event_id });
                    table.CheckConstraint("warning_events_operation_check", "operation IN ('add', 'remove', 'reset', 'decay')");
                    table.CheckConstraint("warning_events_quantity_check", "quantity > 0");
                    table.CheckConstraint("warning_events_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                    table.ForeignKey(
                        name: "FK_warning_events_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_warning_events_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "reports",
                        principalColumn: "report_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warning_events_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warning_events_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_warning_events_users_user_id_at_time",
                        column: x => x.user_id_at_time,
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
                        name: "FK_sanctions_reports_report_id",
                        column: x => x.report_id,
                        principalTable: "reports",
                        principalColumn: "report_id",
                        onDelete: ReferentialAction.Restrict);
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
                    table.ForeignKey(
                        name: "FK_sanctions_warning_events_group_id_account_id_trigger_warnin~",
                        columns: x => new { x.group_id, x.account_id, x.trigger_warning_event_id },
                        principalTable: "warning_events",
                        principalColumns: new[] { "group_id", "account_id", "warning_event_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_flush_batch_items_account_id",
                table: "analytics_flush_batch_items",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_flush_batch_items_day",
                table: "analytics_flush_batch_items",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_flush_batch_items_group_id",
                table: "analytics_flush_batch_items",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "compatibility_outbox_ordering_idx",
                table: "compatibility_outbox",
                columns: new[] { "ordering_key", "created_at" },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "compatibility_outbox_pending_idx",
                table: "compatibility_outbox",
                columns: new[] { "next_attempt_at", "created_at" },
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "global_role_assignments_active_idx",
                table: "global_role_assignments",
                columns: new[] { "user_id", "role" },
                unique: true,
                filter: "revoked_at IS NULL");

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

            migrationBuilder.CreateIndex(
                name: "IX_group_account_daily_analytics_account_id",
                table: "group_account_daily_analytics",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_account_daily_analytics_day",
                table: "group_account_daily_analytics",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "IX_group_account_message_totals_account_id",
                table: "group_account_message_totals",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_account_observations_account_id",
                table: "group_account_observations",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_audit_events_actor_user_id",
                table: "group_audit_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_audit_events_group_id_occurred_at",
                table: "group_audit_events",
                columns: new[] { "group_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_group_audit_events_legacy_key",
                table: "group_audit_events",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_group_audit_events_subject_user_id",
                table: "group_audit_events",
                column: "subject_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_daily_analytics_day",
                table: "group_daily_analytics",
                column: "day");

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
                name: "group_membership_periods_open_idx",
                table: "group_membership_periods",
                columns: new[] { "group_id", "account_id" },
                unique: true,
                filter: "left_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_group_membership_periods_account_id",
                table: "group_membership_periods",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_membership_periods_user_id_at_time",
                table: "group_membership_periods",
                column: "user_id_at_time");

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
                name: "IX_group_settings_log_chat_id",
                table: "group_settings",
                column: "log_chat_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_user_daily_analytics_day",
                table: "group_user_daily_analytics",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "IX_group_user_daily_analytics_user_id",
                table: "group_user_daily_analytics",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_user_observations_user_id",
                table: "group_user_observations",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_group_user_preferences_user_id",
                table: "group_user_preferences",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_groups_chat_id",
                table: "groups",
                column: "chat_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legacy_redis_records_migration_run_id",
                table: "legacy_redis_records",
                column: "migration_run_id");

            migrationBuilder.CreateIndex(
                name: "legacy_redis_records_disposition_idx",
                table: "legacy_redis_records",
                columns: new[] { "disposition", "source_key" });

            migrationBuilder.CreateIndex(
                name: "IX_message_metadata_account_id",
                table: "message_metadata",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_message_metadata_group_id_reply_to_message_id",
                table: "message_metadata",
                columns: new[] { "group_id", "reply_to_message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_message_metadata_user_id_at_time",
                table: "message_metadata",
                column: "user_id_at_time");

            migrationBuilder.CreateIndex(
                name: "message_metadata_sent_at_idx",
                table: "message_metadata",
                column: "sent_at");

            migrationBuilder.CreateIndex(
                name: "IX_migration_exceptions_migration_run_id",
                table: "migration_exceptions",
                column: "migration_run_id");

            migrationBuilder.CreateIndex(
                name: "migration_exceptions_open_idx",
                table: "migration_exceptions",
                columns: new[] { "severity", "domain" },
                filter: "resolved_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reports_group_id_report_message_id",
                table: "reports",
                columns: new[] { "group_id", "report_message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_reports_legacy_key",
                table: "reports",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reports_reporter_account_id",
                table: "reports",
                column: "reporter_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_reporter_user_id",
                table: "reports",
                column: "reporter_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_solver_user_id",
                table: "reports",
                column: "solver_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_target_account_id",
                table: "reports",
                column: "target_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_reports_target_user_id",
                table: "reports",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "reports_open_target_idx",
                table: "reports",
                columns: new[] { "group_id", "target_message_id" },
                unique: true,
                filter: "status = 'open' AND target_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_account_id",
                table: "sanctions",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_actor_user_id",
                table: "sanctions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_group_id_account_id_trigger_warning_event_id",
                table: "sanctions",
                columns: new[] { "group_id", "account_id", "trigger_warning_event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_legacy_key",
                table: "sanctions",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_report_id",
                table: "sanctions",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_sanctions_user_id_at_time",
                table: "sanctions",
                column: "user_id_at_time");

            migrationBuilder.CreateIndex(
                name: "sanctions_due_idx",
                table: "sanctions",
                columns: new[] { "edition", "status", "expires_at" });

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

            migrationBuilder.CreateIndex(
                name: "IX_warning_balances_user_id",
                table: "warning_balances",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_account_id",
                table: "warning_events",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_actor_user_id",
                table: "warning_events",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_group_id_account_id_occurred_at",
                table: "warning_events",
                columns: new[] { "group_id", "account_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_legacy_key",
                table: "warning_events",
                column: "legacy_key",
                unique: true,
                filter: "legacy_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_report_id",
                table: "warning_events",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "IX_warning_events_user_id_at_time",
                table: "warning_events",
                column: "user_id_at_time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_message_totals");

            migrationBuilder.DropTable(
                name: "analytics_flush_batch_items");

            migrationBuilder.DropTable(
                name: "bot_statistic_baselines");

            migrationBuilder.DropTable(
                name: "compatibility_outbox");

            migrationBuilder.DropTable(
                name: "custom_commands");

            migrationBuilder.DropTable(
                name: "global_role_assignments");

            migrationBuilder.DropTable(
                name: "global_sanctions");

            migrationBuilder.DropTable(
                name: "group_account_daily_analytics");

            migrationBuilder.DropTable(
                name: "group_account_message_totals");

            migrationBuilder.DropTable(
                name: "group_account_observations");

            migrationBuilder.DropTable(
                name: "group_audit_events");

            migrationBuilder.DropTable(
                name: "group_character_policies");

            migrationBuilder.DropTable(
                name: "group_content_items");

            migrationBuilder.DropTable(
                name: "group_daily_analytics");

            migrationBuilder.DropTable(
                name: "group_elevation_blocks");

            migrationBuilder.DropTable(
                name: "group_entitlements");

            migrationBuilder.DropTable(
                name: "group_flood_exceptions");

            migrationBuilder.DropTable(
                name: "group_length_policies");

            migrationBuilder.DropTable(
                name: "group_media_policies");

            migrationBuilder.DropTable(
                name: "group_membership_periods");

            migrationBuilder.DropTable(
                name: "group_role_assignments");

            migrationBuilder.DropTable(
                name: "group_settings");

            migrationBuilder.DropTable(
                name: "group_user_daily_analytics");

            migrationBuilder.DropTable(
                name: "group_user_observations");

            migrationBuilder.DropTable(
                name: "group_user_preferences");

            migrationBuilder.DropTable(
                name: "legacy_redis_records");

            migrationBuilder.DropTable(
                name: "migration_exceptions");

            migrationBuilder.DropTable(
                name: "sanctions");

            migrationBuilder.DropTable(
                name: "telegram_account_profiles");

            migrationBuilder.DropTable(
                name: "telegram_chat_profiles");

            migrationBuilder.DropTable(
                name: "user_identity_links");

            migrationBuilder.DropTable(
                name: "warning_balances");

            migrationBuilder.DropTable(
                name: "analytics_flush_batches");

            migrationBuilder.DropTable(
                name: "migration_runs");

            migrationBuilder.DropTable(
                name: "warning_events");

            migrationBuilder.DropTable(
                name: "reports");

            migrationBuilder.DropTable(
                name: "message_metadata");

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
