using System;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Enforcer5.Storage
{
    public sealed class EnforcerDbContext : DbContext
    {
        public EnforcerDbContext(DbContextOptions<EnforcerDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

        internal static void ConfigureModel(ModelBuilder modelBuilder)
        {
            ConfigureMigrationInfrastructure(modelBuilder);
            ConfigurePrincipals(modelBuilder);
            ConfigureGroupConfiguration(modelBuilder);
            ConfigureModeration(modelBuilder);
            ConfigureAnalytics(modelBuilder);
            UseSnakeCaseColumns(modelBuilder);
        }

        private static void ConfigureMigrationInfrastructure(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountModerationCounterEntity>(entity =>
            {
                entity.ToTable("account_moderation_counters", table =>
                    table.HasCheckConstraint("account_moderation_counters_count_check", "count >= 0"));
                entity.HasKey(x => new { x.AccountId, x.Kind });
                entity.Property(x => x.Kind).IsRequired();
                entity.HasOne<TelegramAccountEntity>().WithMany()
                    .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GroupMigrationStateEntity>(entity =>
            {
                entity.ToTable("migration_group_state", table =>
                {
                    table.HasCheckConstraint("migration_group_state_state_check",
                        "state IN ('Redis', 'Draining', 'Verifying', 'Postgres', 'Failed')");
                    table.HasCheckConstraint("migration_group_state_chunk_check", "chunk_cursor >= 0");
                    // A lease without an expiry can never be reclaimed from a crashed migrator.
                    table.HasCheckConstraint("migration_group_state_lease_check",
                        "(lease_owner IS NULL) = (lease_expires_at IS NULL)");
                });
                // Keyed by Telegram chat id, not the internal group id: a group's state has to be
                // resolvable on the message hot path before any PostgreSQL row is guaranteed to exist.
                entity.HasKey(x => x.TelegramChatId);
                entity.Property(x => x.TelegramChatId).ValueGeneratedNever();
                entity.Property(x => x.State).IsRequired().HasDefaultValue("Redis");
                entity.Property(x => x.ChunkCursor).HasDefaultValue(0L);
                entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
                // The crawler picks work by state, and burn-down is a count over it.
                entity.HasIndex(x => x.State);
                entity.HasIndex(x => x.LeaseExpiresAt).HasFilter("lease_expires_at IS NOT NULL");
            });

            modelBuilder.Entity<MigrationRun>(entity =>
            {
                entity.ToTable("migration_runs", table => table.HasCheckConstraint("migration_runs_status_check", "status IN ('running', 'completed', 'failed')"));
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).UseIdentityAlwaysColumn();
                entity.Property(x => x.Mode).IsRequired();
                entity.Property(x => x.StartedAt).HasDefaultValueSql("now()");
                entity.Property(x => x.Status).IsRequired();
                entity.Property(x => x.Details).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            });

            modelBuilder.Entity<MigrationException>(entity =>
            {
                entity.ToTable("migration_exceptions", table => table.HasCheckConstraint("migration_exceptions_severity_check", "severity IN ('info', 'warning', 'blocking')"));
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).UseIdentityAlwaysColumn();
                entity.Property(x => x.Domain).IsRequired();
                entity.Property(x => x.Severity).IsRequired();
                entity.Property(x => x.Reason).IsRequired();
                entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                entity.HasOne<MigrationRun>().WithMany().HasForeignKey(x => x.MigrationRunId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => new { x.Severity, x.Domain }, "migration_exceptions_open_idx").HasFilter("resolved_at IS NULL");
            });

            modelBuilder.Entity<LegacyRedisRecord>(entity =>
            {
                entity.ToTable("legacy_redis_records", table => table.HasCheckConstraint("legacy_redis_records_disposition_check", "disposition IN ('pending', 'migrated', 'retained', 'excluded', 'quarantined')"));
                entity.HasKey(x => new { x.SourceKey, x.SourceField });
                entity.Property(x => x.SourceField).HasDefaultValue("");
                entity.Property(x => x.SourceType).IsRequired();
                entity.Property(x => x.SourceChecksum).IsRequired();
                entity.Property(x => x.Disposition).HasDefaultValue("pending");
                entity.HasOne<MigrationRun>().WithMany().HasForeignKey(x => x.MigrationRunId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.Disposition, x.SourceKey }, "legacy_redis_records_disposition_idx");
            });

            modelBuilder.Entity<CompatibilityOutboxEntry>(entity =>
            {
                entity.ToTable("compatibility_outbox", table => table.HasCheckConstraint("compatibility_outbox_attempt_count_check", "attempt_count >= 0"));
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Domain).IsRequired();
                entity.Property(x => x.OrderingKey).IsRequired();
                entity.Property(x => x.Operation).IsRequired();
                entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
                entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                entity.Property(x => x.NextAttemptAt).HasDefaultValueSql("now()");
                entity.Property(x => x.AttemptCount).HasDefaultValue(0);
                entity.HasIndex(x => new { x.NextAttemptAt, x.CreatedAt }, "compatibility_outbox_pending_idx").HasFilter("completed_at IS NULL");
                entity.HasIndex(x => new { x.OrderingKey, x.CreatedAt }, "compatibility_outbox_ordering_idx").HasFilter("completed_at IS NULL");
            });
        }

        private static void ConfigurePrincipals(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserEntity>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(x => x.UserId);
                entity.Property(x => x.UserId).UseIdentityAlwaysColumn();
                entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            });

            modelBuilder.Entity<TelegramAccountEntity>(entity =>
            {
                entity.ToTable("telegram_accounts");
                entity.HasKey(x => x.AccountId);
                entity.Property(x => x.AccountId).UseIdentityAlwaysColumn();
                entity.Property(x => x.FirstSeenAt).HasDefaultValueSql("now()");
                entity.Property(x => x.LastSeenAt).HasDefaultValueSql("now()");
                entity.HasIndex(x => x.TelegramUserId).IsUnique();
            });

            modelBuilder.Entity<TelegramAccountProfileEntity>(entity =>
            {
                entity.ToTable("telegram_account_profiles", table => table.HasCheckConstraint("telegram_account_profiles_validity_check", "valid_to IS NULL OR valid_to > valid_from"));
                entity.HasKey(x => x.ProfileId);
                entity.Property(x => x.ProfileId).UseIdentityAlwaysColumn();
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => x.AccountId, "telegram_account_profiles_open_idx").IsUnique().HasFilter("valid_to IS NULL");
                entity.HasIndex(x => x.Username, "telegram_account_profiles_username_ci_idx").HasFilter("username IS NOT NULL");
            });

            modelBuilder.Entity<UserIdentityLinkEntity>(entity =>
            {
                entity.ToTable("user_identity_links", table => table.HasCheckConstraint("user_identity_links_period_check", "unlinked_at IS NULL OR unlinked_at >= linked_at"));
                entity.HasKey(x => x.IdentityLinkId);
                entity.Property(x => x.IdentityLinkId).UseIdentityAlwaysColumn();
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.LinkedByUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UnlinkedByUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => x.AccountId, "user_identity_links_active_account_idx").IsUnique().HasFilter("unlinked_at IS NULL");
                entity.HasIndex(x => new { x.UserId, x.AccountId, x.LinkedAt });
            });

            modelBuilder.Entity<TelegramChatEntity>(entity =>
            {
                entity.ToTable("telegram_chats", table => table.HasCheckConstraint("telegram_chats_type_check", "chat_type IN ('unknown', 'private', 'group', 'supergroup', 'channel')"));
                entity.HasKey(x => x.ChatId);
                entity.Property(x => x.ChatId).UseIdentityAlwaysColumn();
                entity.HasIndex(x => x.TelegramChatId).IsUnique();
            });

            modelBuilder.Entity<GroupEntity>(entity =>
            {
                entity.ToTable("groups", table => table.HasCheckConstraint("groups_observed_user_count_check", "observed_user_count >= 0"));
                entity.HasKey(x => x.GroupId);
                entity.Property(x => x.GroupId).UseIdentityAlwaysColumn();
                entity.Property(x => x.Active).HasDefaultValue(true);
                entity.Property(x => x.ObservedUserCount).HasDefaultValue(0L);
                entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
                entity.HasIndex(x => x.ChatId).IsUnique();
                entity.HasOne<TelegramChatEntity>().WithOne().HasForeignKey<GroupEntity>(x => x.ChatId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<TelegramChatProfileEntity>(entity =>
            {
                entity.ToTable("telegram_chat_profiles", table => table.HasCheckConstraint("telegram_chat_profiles_validity_check", "valid_to IS NULL OR valid_to > valid_from"));
                entity.HasKey(x => x.ChatProfileId);
                entity.Property(x => x.ChatProfileId).UseIdentityAlwaysColumn();
                entity.HasOne<TelegramChatEntity>().WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => x.ChatId, "telegram_chat_profiles_open_idx").IsUnique().HasFilter("valid_to IS NULL");
                entity.HasIndex(x => x.Username, "telegram_chat_profiles_username_ci_idx").HasFilter("username IS NOT NULL");
            });

            modelBuilder.Entity<GroupMembershipPeriodEntity>(entity =>
            {
                entity.ToTable("group_membership_periods", table =>
                {
                    table.HasCheckConstraint("group_membership_periods_seen_check", "last_seen_at >= first_seen_at");
                    table.HasCheckConstraint("group_membership_periods_joined_left_check", "left_at IS NULL OR joined_at IS NULL OR left_at >= joined_at");
                });
                entity.HasKey(x => x.MembershipId);
                entity.Property(x => x.MembershipId).UseIdentityAlwaysColumn();
                GroupFk(entity, x => x.GroupId);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => new { x.GroupId, x.AccountId }, "group_membership_periods_open_idx").IsUnique().HasFilter("left_at IS NULL");
            });

            modelBuilder.Entity<GroupAccountObservationEntity>(entity =>
            {
                entity.ToTable("group_account_observations", table => table.HasCheckConstraint("group_account_observations_seen_check", "last_seen_at >= first_seen_at"));
                entity.HasKey(x => new { x.GroupId, x.AccountId });
                GroupFk(entity, x => x.GroupId);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<GroupUserObservationEntity>(entity =>
            {
                entity.ToTable("group_user_observations", table => table.HasCheckConstraint("group_user_observations_seen_check", "last_seen_at >= first_seen_at"));
                entity.HasKey(x => new { x.GroupId, x.UserId });
                GroupFk(entity, x => x.GroupId);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            });
        }

        private static void ConfigureGroupConfiguration(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GroupSettingsEntity>(entity =>
            {
                entity.ToTable("group_settings", table =>
                {
                    table.HasCheckConstraint("group_settings_version_check", "version > 0");
                    table.HasCheckConstraint("group_settings_limits_check", "maximum_messages >= 0 AND maximum_warnings >= 0 AND media_warning_limit >= 0 AND tempban_minutes >= 0 AND tempmute_minutes >= 0");
                    table.HasCheckConstraint("group_settings_actions_check", "flood_action IN ('none', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute', 'delete') AND warning_action IN ('none', 'kick', 'ban', 'mute', 'tempban', 'tempmute') AND media_action IN ('none', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute', 'delete')");
                    table.HasCheckConstraint("group_settings_welcome_format_check", "welcome_format IS NULL OR welcome_format IN ('text', 'markdown', 'html')");
                    table.HasCheckConstraint("group_settings_welcome_file_owner_check", "(welcome_media_file_id IS NULL) = (welcome_media_file_owner_edition IS NULL) AND (welcome_media_file_owner_edition IS NULL OR welcome_media_file_owner_edition IN ('normal', 'premium', 'legacy_unknown'))");
                });
                entity.HasKey(x => x.GroupId);
                entity.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
                entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
                GroupFk(entity, x => x.GroupId);
                entity.HasOne<TelegramChatEntity>().WithMany().HasForeignKey(x => x.LogChatId).OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<GroupFloodExceptionEntity>(entity =>
            {
                entity.ToTable("group_flood_exceptions"); entity.HasKey(x => new { x.GroupId, x.ContentType }); GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<GroupMediaPolicyEntity>(entity =>
            {
                entity.ToTable("group_media_policies", table => table.HasCheckConstraint("group_media_policies_type_check", "media_type IN ('animation', 'audio', 'contact', 'document', 'game', 'location', 'photo', 'poll', 'sticker', 'video', 'video_note', 'voice')"));
                entity.HasKey(x => new { x.GroupId, x.MediaType }); GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<GroupCharacterPolicyEntity>(entity =>
            {
                entity.ToTable("group_character_policies", table => table.HasCheckConstraint("group_character_policies_action_check", "action IN ('allow', 'delete', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute')"));
                entity.HasKey(x => new { x.GroupId, x.CharacterClass }); GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<GroupLengthPolicyEntity>(entity =>
            {
                entity.ToTable("group_length_policies", table =>
                {
                    table.HasCheckConstraint("group_length_policies_scope_check", "scope IN ('message', 'caption')");
                    table.HasCheckConstraint("group_length_policies_limits_check", "maximum_characters >= 0 AND maximum_lines >= 0");
                    table.HasCheckConstraint("group_length_policies_action_check", "action IN ('allow', 'delete', 'warn', 'kick', 'ban', 'mute', 'tempban', 'tempmute')");
                });
                entity.HasKey(x => new { x.GroupId, x.Scope }); GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<GroupContentEntity>(entity =>
            {
                entity.ToTable("group_content_items", table => table.HasCheckConstraint("group_content_items_type_check", "content_type IN ('rules', 'about', 'language', 'link')"));
                entity.HasKey(x => new { x.GroupId, x.ContentType }); entity.Property(x => x.Content).IsRequired(); GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<CustomCommandEntity>(entity =>
            {
                entity.ToTable("custom_commands", table =>
                {
                    table.HasCheckConstraint("custom_commands_media_type_check", "media_type IS NULL OR media_type IN ('animation', 'audio', 'document', 'photo', 'sticker', 'video', 'video_note', 'voice')");
                    table.HasCheckConstraint("custom_commands_file_owner_check", "(file_id IS NULL) = (file_owner_edition IS NULL) AND (file_owner_edition IS NULL OR file_owner_edition IN ('normal', 'premium', 'legacy_unknown'))");
                });
                entity.HasKey(x => new { x.GroupId, x.Trigger }); GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<CustomCommandConflictCandidateEntity>(entity =>
            {
                entity.ToTable("custom_command_conflict_candidates", table =>
                {
                    table.HasCheckConstraint("custom_command_conflicts_media_type_check", "media_type IS NULL OR media_type IN ('gif', 'animation', 'audio', 'document', 'photo', 'sticker', 'video', 'video_note', 'voice')");
                    table.HasCheckConstraint("custom_command_conflicts_file_owner_check", "(file_id IS NULL) = (file_owner_edition IS NULL) AND (file_owner_edition IS NULL OR file_owner_edition IN ('normal', 'premium', 'legacy_unknown'))");
                    table.HasCheckConstraint("custom_command_conflicts_resolution_check", "(resolved_at IS NULL AND resolution IS NULL AND selected_candidate_id IS NULL) OR (resolved_at IS NOT NULL AND resolution = 'deleted' AND selected_candidate_id IS NULL) OR (resolved_at IS NOT NULL AND resolution = 'selected' AND selected_candidate_id IS NOT NULL)");
                });
                entity.HasKey(x => x.CandidateId);
                entity.Property(x => x.Trigger).IsRequired();
                entity.Property(x => x.SourceTrigger).IsRequired();
                entity.HasIndex(x => new { x.GroupId, x.Trigger, x.SourceTrigger }).IsUnique();
                entity.HasIndex(x => new { x.GroupId, x.Trigger }).HasFilter("resolved_at IS NULL");
                GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<GroupEntitlementEntity>(entity =>
            {
                entity.ToTable("group_entitlements"); entity.HasKey(x => new { x.GroupId, x.Entitlement }); GroupFk(entity, x => x.GroupId);
            });
            modelBuilder.Entity<GroupRoleAssignmentEntity>(entity =>
            {
                entity.ToTable("group_role_assignments", table =>
                {
                    table.HasCheckConstraint("group_role_assignments_role_check", "role IN ('authorized', 'moderator')");
                    table.HasCheckConstraint("group_role_assignments_period_check", "revoked_at IS NULL OR revoked_at >= assigned_at");
                });
                entity.HasKey(x => x.AssignmentId); entity.Property(x => x.AssignmentId).UseIdentityAlwaysColumn(); GroupFk(entity, x => x.GroupId); UserFk(entity, x => x.UserId);
                entity.HasIndex(x => new { x.GroupId, x.UserId, x.Role }, "group_role_assignments_active_idx").IsUnique().HasFilter("revoked_at IS NULL");
            });
            modelBuilder.Entity<GroupElevationBlockEntity>(entity =>
            {
                entity.ToTable("group_elevation_blocks", table => table.HasCheckConstraint("group_elevation_blocks_period_check", "revoked_at IS NULL OR revoked_at >= blocked_at"));
                entity.HasKey(x => x.BlockId); entity.Property(x => x.BlockId).UseIdentityAlwaysColumn(); GroupFk(entity, x => x.GroupId); UserFk(entity, x => x.UserId);
                entity.HasIndex(x => new { x.GroupId, x.UserId }, "group_elevation_blocks_active_idx").IsUnique().HasFilter("revoked_at IS NULL");
            });
            modelBuilder.Entity<GroupUserPreferenceEntity>(entity =>
            {
                entity.ToTable("group_user_preferences"); entity.HasKey(x => new { x.GroupId, x.UserId });
                entity.Property(x => x.AdminNotificationsDisabled).HasDefaultValue(false);
                entity.Property(x => x.ReportsBlocked).HasDefaultValue(false);
                entity.Property(x => x.WatchDisabled).HasDefaultValue(false);
                entity.Property(x => x.TagAllSubscribed).HasDefaultValue(false);
                entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()"); GroupFk(entity, x => x.GroupId); UserFk(entity, x => x.UserId);
            });
            modelBuilder.Entity<GlobalRoleAssignmentEntity>(entity =>
            {
                entity.ToTable("global_role_assignments", table =>
                {
                    table.HasCheckConstraint("global_role_assignments_role_check", "role IN ('developer', 'global_admin', 'global_ban_admin')");
                    table.HasCheckConstraint("global_role_assignments_period_check", "revoked_at IS NULL OR revoked_at >= assigned_at");
                });
                entity.HasKey(x => x.AssignmentId); entity.Property(x => x.AssignmentId).UseIdentityAlwaysColumn(); UserFk(entity, x => x.UserId);
                entity.HasIndex(x => new { x.UserId, x.Role }, "global_role_assignments_active_idx").IsUnique().HasFilter("revoked_at IS NULL");
            });
        }

        private static void ConfigureModeration(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WarningBalanceEntity>(entity =>
            {
                entity.ToTable("warning_balances", table =>
                {
                    table.HasCheckConstraint("warning_balances_quantity_check", "active_quantity >= 0 AND lifetime_quantity >= 0");
                    table.HasCheckConstraint("warning_balances_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                });
                entity.HasKey(x => new { x.GroupId, x.UserId, x.WarningType }); GroupFk(entity, x => x.GroupId); UserFk(entity, x => x.UserId);
            });

            modelBuilder.Entity<ReportEntity>(entity =>
            {
                entity.ToTable("reports", table =>
                {
                    table.HasCheckConstraint("reports_status_check", "status IN ('open', 'resolved')");
                    table.HasCheckConstraint("reports_resolution_action_check", "resolution_action IS NULL OR resolution_action IN ('solved', 'dismissed', 'warned', 'kicked', 'banned', 'deleted')");
                    table.HasCheckConstraint("reports_resolution_check", "(status = 'open' AND resolved_at IS NULL AND resolution_action IS NULL) OR (status = 'resolved' AND resolved_at IS NOT NULL AND resolution_action IS NOT NULL)");
                });
                entity.HasKey(x => x.ReportId); entity.Property(x => x.ReportId).UseIdentityAlwaysColumn(); GroupFk(entity, x => x.GroupId);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ReporterUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.SolverUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.ReporterAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.TargetAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => new { x.GroupId, x.TargetMessageId }, "reports_open_target_idx").IsUnique().HasFilter("status = 'open' AND target_message_id IS NOT NULL");
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
            });

            modelBuilder.Entity<WarningEventEntity>(entity =>
            {
                entity.ToTable("warning_events", table =>
                {
                    table.HasCheckConstraint("warning_events_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                    table.HasCheckConstraint("warning_events_operation_check", "operation IN ('add', 'remove', 'reset', 'decay')");
                    table.HasCheckConstraint("warning_events_quantity_check", "quantity > 0");
                });
                entity.HasKey(x => x.WarningEventId); entity.Property(x => x.WarningEventId).UseIdentityAlwaysColumn(); GroupFk(entity, x => x.GroupId);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<ReportEntity>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
                entity.HasAlternateKey(x => new { x.GroupId, x.AccountId, x.WarningEventId });
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
                entity.HasIndex(x => new { x.GroupId, x.AccountId, x.OccurredAt });
            });

            modelBuilder.Entity<SanctionEntity>(entity =>
            {
                entity.ToTable("sanctions", table =>
                {
                    table.HasCheckConstraint("sanctions_kind_check", "kind IN ('ban', 'kick', 'mute', 'tempban', 'tempmute')");
                    table.HasCheckConstraint("sanctions_status_check", "status IN ('active', 'due', 'revoked', 'completed')");
                    table.HasCheckConstraint("sanctions_period_check", "(expires_at IS NULL OR expires_at >= imposed_at) AND (resolved_at IS NULL OR resolved_at >= imposed_at)");
                    table.HasCheckConstraint("sanctions_claim_check", "(claimed_at IS NULL) = (claimed_by IS NULL)");
                });
                entity.HasKey(x => x.SanctionId); entity.Property(x => x.SanctionId).UseIdentityAlwaysColumn(); GroupFk(entity, x => x.GroupId);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<WarningEventEntity>().WithMany().HasForeignKey(x => new { x.GroupId, x.AccountId, x.TriggerWarningEventId }).HasPrincipalKey(x => new { x.GroupId, x.AccountId, x.WarningEventId }).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<ReportEntity>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
                entity.HasIndex(x => new { x.Edition, x.Status, x.ExpiresAt }, "sanctions_due_idx");
            });

            modelBuilder.Entity<GlobalSanctionEntity>(entity =>
            {
                entity.ToTable("global_sanctions", table =>
                {
                    table.HasCheckConstraint("global_sanctions_kind_check", "kind IN ('ban', 'mute')");
                    table.HasCheckConstraint("global_sanctions_status_check", "status IN ('active', 'revoked', 'completed')");
                    table.HasCheckConstraint("global_sanctions_period_check", "(expires_at IS NULL OR expires_at >= imposed_at) AND (resolved_at IS NULL OR resolved_at >= imposed_at)");
                });
                entity.HasKey(x => x.GlobalSanctionId); entity.Property(x => x.GlobalSanctionId).UseIdentityAlwaysColumn();
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => x.AccountId, "global_sanctions_active_idx").IsUnique().HasFilter("status = 'active'");
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
            });

            modelBuilder.Entity<MessageMetadataEntity>(entity =>
            {
                entity.ToTable("message_metadata", table => table.HasCheckConstraint("message_metadata_dates_check", "(edited_at IS NULL OR edited_at >= sent_at) AND (deleted_at IS NULL OR deleted_at >= sent_at)"));
                entity.HasKey(x => new { x.GroupId, x.TelegramMessageId }); GroupFk(entity, x => x.GroupId);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<MessageMetadataEntity>().WithMany().HasForeignKey(x => new { x.GroupId, x.ReplyToMessageId }).HasPrincipalKey(x => new { x.GroupId, x.TelegramMessageId }).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => x.SentAt, "message_metadata_sent_at_idx");
            });

            modelBuilder.Entity<ReportEntity>().HasOne<MessageMetadataEntity>().WithMany().HasForeignKey(x => new { x.GroupId, x.ReportMessageId }).HasPrincipalKey(x => new { x.GroupId, x.TelegramMessageId }).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<ReportEntity>().HasOne<MessageMetadataEntity>().WithMany().HasForeignKey(x => new { x.GroupId, x.TargetMessageId }).HasPrincipalKey(x => new { x.GroupId, x.TelegramMessageId }).OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<GroupAuditEventEntity>(entity =>
            {
                entity.ToTable("group_audit_events"); entity.HasKey(x => x.EventId); entity.Property(x => x.EventId).UseIdentityAlwaysColumn(); GroupFk(entity, x => x.GroupId);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.SubjectUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
                entity.HasIndex(x => new { x.GroupId, x.OccurredAt });
            });
        }

        private static void ConfigureAnalytics(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GroupDailyAnalyticsEntity>(entity =>
            {
                entity.ToTable("group_daily_analytics", table => table.HasCheckConstraint("group_daily_analytics_counts_check", "message_count >= 0 AND active_user_count >= 0 AND join_count >= 0 AND leave_count >= 0"));
                entity.HasKey(x => new { x.GroupId, x.Day }); GroupFk(entity, x => x.GroupId); entity.HasIndex(x => x.Day);
            });
            modelBuilder.Entity<GroupAccountDailyAnalyticsEntity>(entity =>
            {
                entity.ToTable("group_account_daily_analytics", table => table.HasCheckConstraint("group_account_daily_analytics_count_check", "message_count >= 0"));
                entity.HasKey(x => new { x.GroupId, x.AccountId, x.Day }); GroupFk(entity, x => x.GroupId); entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); entity.HasIndex(x => x.Day);
            });
            modelBuilder.Entity<GroupUserDailyAnalyticsEntity>(entity =>
            {
                entity.ToTable("group_user_daily_analytics", table => table.HasCheckConstraint("group_user_daily_analytics_count_check", "message_count >= 0"));
                entity.HasKey(x => new { x.GroupId, x.UserId, x.Day }); GroupFk(entity, x => x.GroupId); UserFk(entity, x => x.UserId); entity.HasIndex(x => x.Day);
            });
            modelBuilder.Entity<AccountMessageTotalEntity>(entity =>
            {
                entity.ToTable("account_message_totals", table =>
                {
                    table.HasCheckConstraint("account_message_totals_count_check", "message_count >= 0");
                    table.HasCheckConstraint("account_message_totals_unattributed_check", "unattributed_count >= 0");
                    // The residual can never exceed the total it is part of.
                    table.HasCheckConstraint("account_message_totals_unattributed_bound_check",
                        "unattributed_count <= message_count");
                }); entity.HasKey(x => x.AccountId); entity.Property(x => x.UnattributedCount).HasDefaultValue(0L); entity.HasOne<TelegramAccountEntity>().WithOne().HasForeignKey<AccountMessageTotalEntity>(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<GroupAccountMessageTotalEntity>(entity =>
            {
                entity.ToTable("group_account_message_totals", table => table.HasCheckConstraint("group_account_message_totals_count_check", "message_count >= 0")); entity.HasKey(x => new { x.GroupId, x.AccountId }); GroupFk(entity, x => x.GroupId); entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<BotStatisticBaselineEntity>(entity =>
            {
                entity.ToTable("bot_statistic_baselines", table => table.HasCheckConstraint("bot_statistic_baselines_counts_check", "message_count >= 0 AND user_count >= 0 AND ban_count >= 0 AND kick_count >= 0")); entity.HasKey(x => x.Edition);
            });
            modelBuilder.Entity<AnalyticsFlushBatchEntity>(entity =>
            {
                entity.ToTable("analytics_flush_batches"); entity.HasKey(x => x.BatchId); entity.Property(x => x.CommittedAt).HasDefaultValueSql("now()");
            });
            modelBuilder.Entity<AnalyticsFlushBatchItemEntity>(entity =>
            {
                entity.ToTable("analytics_flush_batch_items", table => table.HasCheckConstraint("analytics_flush_batch_items_deltas_check", "message_delta >= 0 AND active_user_delta >= 0 AND join_delta >= 0 AND leave_delta >= 0"));
                entity.HasKey(x => new { x.BatchId, x.GroupId, x.AccountId, x.Day });
                entity.HasOne<AnalyticsFlushBatchEntity>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade); GroupFk(entity, x => x.GroupId); entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); entity.HasIndex(x => x.Day);
            });
        }

        private static void GroupFk<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, System.Linq.Expressions.Expression<Func<TEntity, long>> foreignKey) where TEntity : class =>
            entity.HasOne<GroupEntity>().WithMany().HasForeignKey(GetPropertyName(foreignKey)).OnDelete(DeleteBehavior.Cascade);

        private static void UserFk<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity, System.Linq.Expressions.Expression<Func<TEntity, long>> foreignKey) where TEntity : class =>
            entity.HasOne<UserEntity>().WithMany().HasForeignKey(GetPropertyName(foreignKey)).OnDelete(DeleteBehavior.Restrict);

        private static string GetPropertyName<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, long>> expression) =>
            ((System.Linq.Expressions.MemberExpression)expression.Body).Member.Name;

        private static void UseSnakeCaseColumns(ModelBuilder modelBuilder)
        {
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                    property.SetColumnName(ToSnakeCase(property.Name));
        }

        private static string ToSnakeCase(string value)
        {
            var result = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                if (i > 0 && char.IsUpper(value[i])) result.Append('_');
                result.Append(char.ToLowerInvariant(value[i]));
            }
            return result.ToString();
        }
    }
}
