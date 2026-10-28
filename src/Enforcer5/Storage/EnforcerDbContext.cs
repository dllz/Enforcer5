using System;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Enforcer5.Storage
{
    /// <summary>
    /// Describes the PostgreSQL schema so EF Core can create and migrate it. Nothing queries
    /// through this context at runtime; the repositories use Dapper.
    /// <para>
    /// Each rollout step adds its tables here and a migration under Migrations/ (generate with
    /// <c>dotnet tool restore</c> then
    /// <c>dotnet ef migrations add &lt;Name&gt; -p src/Enforcer5 -o Storage/Migrations</c>). Table and
    /// constraint definitions are copied from the approved design on feature/postgres-migration.
    /// </para>
    /// </summary>
    public sealed class EnforcerDbContext : DbContext
    {
        public EnforcerDbContext(DbContextOptions<EnforcerDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

        internal static void ConfigureModel(ModelBuilder modelBuilder)
        {
            ConfigurePrincipals(modelBuilder);
            ConfigureReports(modelBuilder);
            ConfigureWarnings(modelBuilder);
            ConfigureSanctions(modelBuilder);
            ConfigureGlobalSanctions(modelBuilder);
            ConfigureRaidCapture(modelBuilder);
            ConfigureDetectionSettings(modelBuilder);
            ConfigureDetectionLog(modelBuilder);
            UseSnakeCaseColumns(modelBuilder);
        }

        private const string LabelActions = "'ban', 'tempban', 'kick', 'mute', 'tempmute', 'warn', 'delete', 'unban', 'unmute', 'unwarn', 'fine'";

        private static void ConfigureDetectionLog(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MemberLabelEntity>(entity =>
            {
                entity.ToTable("member_labels", table =>
                {
                    table.HasCheckConstraint("member_labels_label_check", "label IN ('bad', 'fine', 'undo')");
                    table.HasCheckConstraint("member_labels_action_check", $"action IN ({LabelActions})");
                    table.HasCheckConstraint("member_labels_source_check", "source IN ('command', 'button', 'report', 'app', 'auto', 'raid', 'global', 'established')");
                    table.HasCheckConstraint("member_labels_edition_check", "edition IN ('normal', 'premium')");
                    table.HasCheckConstraint("member_labels_undone_check", "(undone_at IS NULL) = (undone_by_label_id IS NULL)");
                    table.HasCheckConstraint("member_labels_category_check", "category IS NULL OR category IN ('abuse', 'group')");
                });
                entity.HasKey(x => x.LabelId);
                entity.Property(x => x.LabelId).UseIdentityAlwaysColumn();
                entity.Property(x => x.Label).IsRequired();
                entity.Property(x => x.Action).IsRequired();
                entity.Property(x => x.Source).IsRequired();
                entity.Property(x => x.Edition).IsRequired();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.DecidedByAccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<MemberLabelEntity>().WithMany().HasForeignKey(x => x.UndoneByLabelId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.GroupId, x.AccountId, x.DecidedAt }, "member_labels_member_idx");
                entity.HasIndex(x => new { x.GroupId, x.DecidedAt }, "member_labels_group_time_idx");
                entity.HasIndex(x => x.LabelKey, "member_labels_key_idx").IsUnique().HasFilter("label_key IS NOT NULL");
            });

            modelBuilder.Entity<DetectionEntity>(entity =>
            {
                entity.ToTable("detections", table =>
                {
                    table.HasCheckConstraint("detections_phase_check", "phase IN ('inline', 'enriched')");
                    table.HasCheckConstraint("detections_mode_check", "mode IN ('live', 'observe', 'trial')");
                    table.HasCheckConstraint("detections_action_check", $"action IN ({DetectionActions})");
                    table.HasCheckConstraint("detections_outcome_check", $"outcome IS NULL OR outcome IN ({LabelActions}, 'undone')");
                    table.HasCheckConstraint("detections_trust_check", "trust_level IS NULL OR trust_level IN ('admin', 'approved', 'established', 'new')");
                    table.HasCheckConstraint("detections_edition_check", "edition IN ('normal', 'premium')");
                    table.HasCheckConstraint("detections_score_check", "score_before >= 0 AND score_after >= score_before");
                    table.HasCheckConstraint("detections_category_check", "category IS NULL OR category IN ('abuse', 'group')");
                });
                entity.HasKey(x => x.DetectionId);
                entity.Property(x => x.DetectionId).UseIdentityAlwaysColumn();
                entity.Property(x => x.Sign).IsRequired();
                entity.Property(x => x.Phase).IsRequired();
                entity.Property(x => x.Action).IsRequired();
                entity.Property(x => x.Mode).IsRequired();
                entity.Property(x => x.Edition).IsRequired();
                entity.Property(x => x.Evidence).HasColumnType("jsonb");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.DecidedByAccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<MemberLabelEntity>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.GroupId, x.AccountId, x.DetectedAt }, "detections_member_idx");
                entity.HasIndex(x => new { x.GroupId, x.DetectedAt }, "detections_group_time_idx");
                entity.HasIndex(x => new { x.Sign, x.DetectedAt }, "detections_sign_time_idx");
                entity.HasIndex(x => x.LabelId, "detections_label_idx").HasFilter("label_id IS NOT NULL");
            });

            modelBuilder.Entity<ReportAdminOptOutEntity>(entity =>
            {
                entity.ToTable("report_admin_optouts");
                entity.HasKey(x => new { x.GroupId, x.AccountId });
                entity.Property(x => x.OptedOutAt).HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ReportCopyEntity>(entity =>
            {
                entity.ToTable("report_copies", table =>
                {
                    table.HasCheckConstraint("report_copies_kind_check", "kind IN ('member', 'trial', 'learn', 'restore')");
                    table.HasCheckConstraint("report_copies_filter_check", "(kind = 'trial') = (filter_id IS NOT NULL)");
                    table.HasCheckConstraint("report_copies_adjustment_check", "(kind = 'learn') = (adjustment_id IS NOT NULL)");
                    table.HasCheckConstraint("report_copies_decision_check",
                        "decision IS NULL OR decision IN ('ban', 'mute', 'fine', 'live', 'extend', 'delete', 'undo', 'keep', 'restore', 'keepnew')");
                    table.HasCheckConstraint("report_copies_decided_check", "(decision IS NULL) = (decided_at IS NULL)");
                    table.HasCheckConstraint("report_copies_edition_check", "edition IN ('normal', 'premium')");
                });
                entity.HasKey(x => x.CopyId);
                entity.Property(x => x.CopyId).UseIdentityAlwaysColumn();
                entity.Property(x => x.Kind).IsRequired();
                entity.Property(x => x.Edition).IsRequired();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.DecidedByAccountId).OnDelete(DeleteBehavior.SetNull);
                // filter_id has no foreign key: a deleted rule's notices still have to be found to say so.
                entity.HasIndex(x => new { x.GroupId, x.AccountId, x.OpenedAt }, "report_copies_member_idx");
                entity.HasIndex(x => new { x.GroupId, x.FilterId }, "report_copies_filter_idx").HasFilter("filter_id IS NOT NULL");
                entity.HasIndex(x => x.SentAt, "report_copies_sent_idx");
                entity.HasIndex(x => x.AdjustmentId, "report_copies_adjustment_idx").HasFilter("adjustment_id IS NOT NULL");
            });

            modelBuilder.Entity<MemberTrustOverrideEntity>(entity =>
            {
                entity.ToTable("member_trust_overrides", table => table.HasCheckConstraint("member_trust_overrides_messages_check", "messages_at_set >= 0"));
                entity.HasKey(x => new { x.GroupId, x.AccountId });
                entity.Property(x => x.SetAt).HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.SetByAccountId).OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<DetectionAdjustmentEntity>(entity =>
            {
                entity.ToTable("detection_adjustments", table =>
                {
                    table.HasCheckConstraint("detection_adjustments_kind_check", "kind IN ('established', 'report_at')");
                    table.HasCheckConstraint("detection_adjustments_reason_check",
                        "reason IN ('decision', 'untrust', 'takeover', 'decay', 'decisions', 'reset')");
                    table.HasCheckConstraint("detection_adjustments_edition_check", "edition IN ('normal', 'premium')");
                    table.HasCheckConstraint("detection_adjustments_undone_check", "undone_at IS NULL OR applied");
                });
                entity.HasKey(x => x.AdjustmentId);
                entity.Property(x => x.AdjustmentId).UseIdentityAlwaysColumn();
                entity.Property(x => x.Kind).IsRequired();
                entity.Property(x => x.Reason).IsRequired();
                entity.Property(x => x.Edition).IsRequired();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.UndoneByAccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<MemberLabelEntity>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.GroupId, x.MadeAt }, "detection_adjustments_group_idx");
            });
        }

        private const string DetectionActions = "'none', 'score', 'report', 'delete', 'warn', 'mute', 'tempmute', 'kick', 'tempban', 'ban'";

        private static void ConfigureDetectionSettings(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GroupSettingsEntity>(entity =>
            {
                // The approved design's constraints, for the columns that exist so far.
                entity.ToTable("group_settings", table =>
                {
                    table.HasCheckConstraint("group_settings_version_check", "version > 0");
                    table.HasCheckConstraint("group_settings_limits_check", "maximum_warnings >= 0 AND tempban_minutes >= 0 AND tempmute_minutes >= 0");
                    table.HasCheckConstraint("group_settings_actions_check", "warning_action IN ('none', 'kick', 'ban', 'mute', 'tempban', 'tempmute')");
                });
                entity.HasKey(x => x.GroupId);
                entity.Property(x => x.WarningAction).IsRequired();
                entity.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
                entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithOne().HasForeignKey<GroupSettingsEntity>(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<DetectionPolicyEntity>(entity =>
            {
                entity.ToTable("detection_policies", table =>
                {
                    table.HasCheckConstraint("detection_policies_action_check", $"action IS NULL OR action IN ({DetectionActions})");
                    table.HasCheckConstraint("detection_policies_check_key_check", "check_key ~ '^[a-z][a-z0-9_:]*$'");
                });
                entity.HasKey(x => new { x.GroupId, x.CheckKey });
                entity.Property(x => x.Settings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").IsRequired();
                entity.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GroupFilterEntity>(entity =>
            {
                entity.ToTable("group_filters", table =>
                {
                    table.HasCheckConstraint("group_filters_kind_check", "kind IN ('preset', 'length', 'regex', 'username', 'exact')");
                    table.HasCheckConstraint("group_filters_target_check", "target IS NULL OR target IN ('name', 'username', 'bio', 'text', 'channel', 'inline_bot', 'sticker_set')");
                    table.HasCheckConstraint("group_filters_mode_check", "mode IN ('live', 'trial')");
                    table.HasCheckConstraint("group_filters_action_check", $"action IS NULL OR action IN ({DetectionActions})");
                    table.HasCheckConstraint("group_filters_preset_check", "(kind = 'preset') = (preset IS NOT NULL)");
                    table.HasCheckConstraint("group_filters_pattern_check", "(kind IN ('regex', 'username', 'exact')) = (pattern IS NOT NULL)");
                    table.HasCheckConstraint("group_filters_length_check", "kind <> 'length' OR target IN ('text', 'name')");
                    table.HasCheckConstraint("group_filters_limits_check", "(max_characters IS NULL OR max_characters > 0) AND (max_lines IS NULL OR max_lines > 0)");
                    table.HasCheckConstraint("group_filters_trial_check", "mode <> 'trial' OR trial_until IS NOT NULL");
                    table.HasCheckConstraint("group_filters_hits_check", "hits >= 0");
                    table.HasCheckConstraint("group_filters_category_check", "category IS NULL OR category IN ('abuse', 'group')");
                });
                entity.HasKey(x => x.FilterId);
                entity.Property(x => x.FilterId).UseIdentityAlwaysColumn();
                entity.Property(x => x.Kind).IsRequired();
                entity.Property(x => x.Mode).IsRequired().HasDefaultValue("live");
                entity.Property(x => x.Hits).HasDefaultValue(0L);
                entity.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.CreatedByAccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.GroupId, x.Preset }, "group_filters_preset_idx").IsUnique().HasFilter("preset IS NOT NULL");
                entity.HasIndex(x => new { x.GroupId, x.Target }, "group_filters_length_idx").IsUnique().HasFilter("kind = 'length'");
                entity.HasIndex(x => new { x.GroupId, x.Target, x.Kind, x.Pattern }, "group_filters_pattern_idx").IsUnique().HasFilter("pattern IS NOT NULL");
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
        }

        private static void ConfigureReports(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ReportEntity>(entity =>
            {
                entity.ToTable("reports", table =>
                {
                    table.HasCheckConstraint("reports_status_check", "status IN ('open', 'resolved')");
                    table.HasCheckConstraint("reports_resolution_action_check", "resolution_action IS NULL OR resolution_action IN ('solved', 'dismissed', 'warned', 'kicked', 'banned', 'deleted')");
                    table.HasCheckConstraint("reports_resolution_check", "(status = 'open' AND resolved_at IS NULL AND resolution_action IS NULL) OR (status = 'resolved' AND resolved_at IS NOT NULL AND resolution_action IS NOT NULL)");
                });
                entity.HasKey(x => x.ReportId);
                entity.Property(x => x.ReportId).UseIdentityAlwaysColumn();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ReporterUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.SolverUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.ReporterAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.TargetAccountId).OnDelete(DeleteBehavior.Restrict);
                // The message_metadata foreign keys arrive with that table.
                entity.HasIndex(x => new { x.GroupId, x.TargetMessageId }, "reports_open_target_idx").IsUnique().HasFilter("status = 'open' AND target_message_id IS NOT NULL");
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
                // Not in the approved design: finding the report a button or /solved names, which
                // is identified by its message.
                entity.HasIndex(x => new { x.GroupId, x.ReportMessageId }, "reports_group_message_idx");
            });

            modelBuilder.Entity<ReportActionEntity>(entity =>
            {
                entity.ToTable("report_actions", table =>
                    table.HasCheckConstraint("report_actions_action_check", "action IN ('deleted', 'warned', 'kicked', 'banned')"));
                entity.HasKey(x => x.ReportActionId);
                entity.Property(x => x.ReportActionId).UseIdentityAlwaysColumn();
                entity.HasOne<ReportEntity>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.Property(x => x.Action).IsRequired();
            });
        }

        private static void ConfigureWarnings(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WarningBalanceEntity>(entity =>
            {
                entity.ToTable("warning_balances", table =>
                {
                    table.HasCheckConstraint("warning_balances_quantity_check", "active_quantity >= 0 AND lifetime_quantity >= 0");
                    table.HasCheckConstraint("warning_balances_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                });
                entity.HasKey(x => new { x.GroupId, x.UserId, x.WarningType });
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<WarningEventEntity>(entity =>
            {
                entity.ToTable("warning_events", table =>
                {
                    table.HasCheckConstraint("warning_events_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                    table.HasCheckConstraint("warning_events_operation_check", "operation IN ('add', 'remove', 'reset', 'decay')");
                    table.HasCheckConstraint("warning_events_quantity_check", "quantity > 0");
                });
                entity.HasKey(x => x.WarningEventId);
                entity.Property(x => x.WarningEventId).UseIdentityAlwaysColumn();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<ReportEntity>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
                entity.HasAlternateKey(x => new { x.GroupId, x.AccountId, x.WarningEventId });
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
                entity.HasIndex(x => new { x.GroupId, x.AccountId, x.OccurredAt });
            });
        }

        private static void ConfigureSanctions(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SanctionEntity>(entity =>
            {
                entity.ToTable("sanctions", table =>
                {
                    table.HasCheckConstraint("sanctions_kind_check", "kind IN ('ban', 'kick', 'mute', 'tempban', 'tempmute')");
                    table.HasCheckConstraint("sanctions_status_check", "status IN ('active', 'due', 'revoked', 'completed')");
                    table.HasCheckConstraint("sanctions_period_check", "(expires_at IS NULL OR expires_at >= imposed_at) AND (resolved_at IS NULL OR resolved_at >= imposed_at)");
                    table.HasCheckConstraint("sanctions_claim_check", "(claimed_at IS NULL) = (claimed_by IS NULL)");
                });
                entity.HasKey(x => x.SanctionId);
                entity.Property(x => x.SanctionId).UseIdentityAlwaysColumn();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<WarningEventEntity>().WithMany()
                    .HasForeignKey(x => new { x.GroupId, x.AccountId, x.TriggerWarningEventId })
                    .HasPrincipalKey(x => new { x.GroupId, x.AccountId, x.WarningEventId })
                    .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<ReportEntity>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
                entity.HasIndex(x => new { x.Edition, x.Status, x.ExpiresAt }, "sanctions_due_idx");
                // Not in the approved design: the history lookup for one member of one group, which
                // /status needs. The design's only group-side index leads with edition.
                entity.HasIndex(x => new { x.GroupId, x.AccountId, x.ImposedAt }, "sanctions_group_account_idx");
            });
        }

        private static void ConfigureGlobalSanctions(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GlobalSanctionEntity>(entity =>
            {
                entity.ToTable("global_sanctions", table =>
                {
                    table.HasCheckConstraint("global_sanctions_kind_check", "kind IN ('ban', 'mute')");
                    table.HasCheckConstraint("global_sanctions_status_check", "status IN ('active', 'revoked', 'completed')");
                    table.HasCheckConstraint("global_sanctions_period_check", "(expires_at IS NULL OR expires_at >= imposed_at) AND (resolved_at IS NULL OR resolved_at >= imposed_at)");
                });
                entity.HasKey(x => x.GlobalSanctionId);
                entity.Property(x => x.GlobalSanctionId).UseIdentityAlwaysColumn();
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserIdAtTime).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => x.AccountId, "global_sanctions_active_idx").IsUnique().HasFilter("status = 'active'");
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
            });
        }

        private static void ConfigureRaidCapture(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GroupMemberEntity>(entity =>
            {
                entity.ToTable("group_members", table =>
                {
                    table.HasCheckConstraint("group_members_count_check", "message_count >= 0");
                    table.HasCheckConstraint("group_members_source_check",
                        "join_source IS NULL OR join_source IN ('link', 'added', 'request', 'unknown')");
                });
                entity.HasKey(x => new { x.GroupId, x.AccountId });
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AddedByAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.Property(x => x.MessageCount).HasDefaultValue(0L);
                entity.HasIndex(x => new { x.GroupId, x.JoinedAt }, "group_members_joined_idx");
                entity.HasIndex(x => new { x.GroupId, x.LastMessageAt }, "group_members_last_message_idx");
            });

            modelBuilder.Entity<GroupJoinEntity>(entity =>
            {
                entity.ToTable("group_joins", table =>
                    table.HasCheckConstraint("group_joins_source_check", "source IN ('link', 'added', 'request', 'unknown')"));
                entity.HasKey(x => x.JoinId);
                entity.Property(x => x.JoinId).UseIdentityAlwaysColumn();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AddedByAccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => new { x.GroupId, x.JoinedAt }, "group_joins_group_time_idx");
                entity.HasIndex(x => new { x.GroupId, x.InviteLink }, "group_joins_link_idx").HasFilter("invite_link IS NOT NULL");
                entity.HasIndex(x => new { x.AccountId, x.JoinedAt }, "group_joins_account_idx");
            });

            modelBuilder.Entity<MessageFingerprintEntity>(entity =>
            {
                entity.ToTable("message_fingerprints", table =>
                    table.HasCheckConstraint("message_fingerprints_reason_check", "sample_reason IN ('first', 'sample', 'raid')"));
                entity.HasKey(x => x.FingerprintId);
                entity.Property(x => x.FingerprintId).UseIdentityAlwaysColumn();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(x => new { x.GroupId, x.SentAt }, "message_fingerprints_group_time_idx");
                entity.HasIndex(x => new { x.GroupId, x.TextHash }, "message_fingerprints_text_idx").HasFilter("text_hash IS NOT NULL");
                entity.HasIndex(x => new { x.GroupId, x.MediaUniqueId }, "message_fingerprints_media_idx").HasFilter("media_unique_id IS NOT NULL");
                entity.HasIndex(x => new { x.AccountId, x.SentAt }, "message_fingerprints_account_idx");
            });

            modelBuilder.Entity<GroupStatsEntity>(entity =>
            {
                entity.ToTable("group_stats");
                entity.HasKey(x => x.GroupId);
                entity.HasOne<GroupEntity>().WithOne().HasForeignKey<GroupStatsEntity>(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<RaidIncidentEntity>(entity =>
            {
                entity.ToTable("raid_incidents", table =>
                {
                    table.HasCheckConstraint("raid_incidents_phase_check", "phase_reached IN ('alert', 'active')");
                    table.HasCheckConstraint("raid_incidents_ended_by_check", "ended_by IS NULL OR ended_by IN ('quiet', 'admin', 'ignored')");
                });
                entity.HasKey(x => x.IncidentId);
                entity.Property(x => x.IncidentId).UseIdentityAlwaysColumn();
                entity.Property(x => x.IncidentKey).IsRequired();
                entity.HasIndex(x => x.IncidentKey).IsUnique();
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.Property(x => x.Reasons).HasColumnType("text[]");
                entity.HasIndex(x => new { x.GroupId, x.StartedAt }, "raid_incidents_group_time_idx");
            });

            modelBuilder.Entity<RaidEventEntity>(entity =>
            {
                entity.ToTable("raid_events", table =>
                    table.HasCheckConstraint("raid_events_kind_check", "kind IN ('join', 'message', 'signal', 'action', 'rate_limit', 'phase')"));
                entity.HasKey(x => x.EventId);
                entity.Property(x => x.EventId).UseIdentityAlwaysColumn();
                entity.HasOne<RaidIncidentEntity>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
                entity.Property(x => x.Detail).HasColumnType("jsonb");
                entity.HasIndex(x => new { x.IncidentId, x.OffsetMs }, "raid_events_timeline_idx");
            });

            modelBuilder.Entity<BannedMediaEntity>(entity =>
            {
                entity.ToTable("banned_media", table =>
                    table.HasCheckConstraint("banned_media_source_check", "source IN ('ban', 'report', 'command')"));
                entity.HasKey(x => new { x.GroupId, x.MediaUniqueId });
                entity.Property(x => x.MediaType).IsRequired();
                entity.Property(x => x.Source).IsRequired();
                entity.Property(x => x.AddedAt).HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AddedByAccountId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.GroupId, x.AddedAt }, "banned_media_group_added_idx");
            });

            modelBuilder.Entity<AccountProfileFlagsEntity>(entity =>
            {
                entity.ToTable("account_profile_flags");
                entity.HasKey(x => x.ProfileFlagsId);
                entity.Property(x => x.ProfileFlagsId).UseIdentityAlwaysColumn();
                entity.Property(x => x.Flags).IsRequired().HasDefaultValueSql("'{}'::text[]");
                entity.HasOne<TelegramAccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => x.AccountId, "account_profile_flags_account_idx").IsUnique();
            });

            modelBuilder.Entity<ChannelProfileEntity>(entity =>
            {
                entity.ToTable("channel_profiles", table =>
                    table.HasCheckConstraint("channel_profiles_type_check", "chat_type IN ('channel', 'group', 'supergroup', 'private')"));
                entity.HasKey(x => x.TelegramChatId);
                entity.Property(x => x.TelegramChatId).ValueGeneratedNever();
                entity.Property(x => x.ChatType).IsRequired();
                entity.Property(x => x.Flags).IsRequired().HasDefaultValueSql("'{}'::text[]");
                entity.HasIndex(x => x.Username, "channel_profiles_username_idx");
            });
        }

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
