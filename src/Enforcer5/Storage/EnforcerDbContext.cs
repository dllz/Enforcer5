using Microsoft.EntityFrameworkCore;

namespace Enforcer5.Storage
{
    public sealed class EnforcerDbContext : DbContext
    {
        public EnforcerDbContext(DbContextOptions<EnforcerDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureModel(modelBuilder);
        }

        internal static void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MigrationRun>(entity =>
            {
                entity.ToTable("migration_runs", table => table.HasCheckConstraint(
                    "migration_runs_status_check", "status IN ('running', 'completed', 'failed')"));
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
                entity.Property(x => x.Mode).HasColumnName("mode").IsRequired();
                entity.Property(x => x.StartedAt).HasColumnName("started_at").HasDefaultValueSql("now()");
                entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
                entity.Property(x => x.Status).HasColumnName("status").IsRequired();
                entity.Property(x => x.SourceSnapshot).HasColumnName("source_snapshot");
                entity.Property(x => x.Details).HasColumnName("details").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            });

            modelBuilder.Entity<MigrationException>(entity =>
            {
                entity.ToTable("migration_exceptions", table => table.HasCheckConstraint(
                    "migration_exceptions_severity_check", "severity IN ('info', 'warning', 'blocking')"));
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
                entity.Property(x => x.MigrationRunId).HasColumnName("migration_run_id");
                entity.Property(x => x.Domain).HasColumnName("domain").IsRequired();
                entity.Property(x => x.SourceKey).HasColumnName("source_key");
                entity.Property(x => x.SourceField).HasColumnName("source_field");
                entity.Property(x => x.Severity).HasColumnName("severity").IsRequired();
                entity.Property(x => x.Reason).HasColumnName("reason").IsRequired();
                entity.Property(x => x.RawValue).HasColumnName("raw_value");
                entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
                entity.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
                entity.Property(x => x.Resolution).HasColumnName("resolution");
                entity.HasOne<MigrationRun>().WithMany().HasForeignKey(x => x.MigrationRunId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(x => new { x.Severity, x.Domain }, "migration_exceptions_open_idx")
                    .HasFilter("resolved_at IS NULL");
            });

            modelBuilder.Entity<LegacyRedisRecord>(entity =>
            {
                entity.ToTable("legacy_redis_records", table => table.HasCheckConstraint(
                    "legacy_redis_records_disposition_check",
                    "disposition IN ('pending', 'migrated', 'retained', 'excluded', 'quarantined')"));
                entity.HasKey(x => new { x.SourceKey, x.SourceField });
                entity.Property(x => x.SourceKey).HasColumnName("source_key");
                entity.Property(x => x.SourceField).HasColumnName("source_field").HasDefaultValue("");
                entity.Property(x => x.SourceType).HasColumnName("source_type").IsRequired();
                entity.Property(x => x.RawValue).HasColumnName("raw_value");
                entity.Property(x => x.SourceTtlMs).HasColumnName("source_ttl_ms");
                entity.Property(x => x.SourceChecksum).HasColumnName("source_checksum").IsRequired();
                entity.Property(x => x.CapturedAt).HasColumnName("captured_at");
                entity.Property(x => x.MigrationRunId).HasColumnName("migration_run_id");
                entity.Property(x => x.Disposition).HasColumnName("disposition").HasDefaultValue("pending");
                entity.Property(x => x.TargetReference).HasColumnName("target_reference");
                entity.HasOne<MigrationRun>().WithMany().HasForeignKey(x => x.MigrationRunId).OnDelete(DeleteBehavior.SetNull);
                entity.HasIndex(x => new { x.Disposition, x.SourceKey }, "legacy_redis_records_disposition_idx");
            });

            modelBuilder.Entity<CompatibilityOutboxEntry>(entity =>
            {
                entity.ToTable("compatibility_outbox", table => table.HasCheckConstraint(
                    "compatibility_outbox_attempt_count_check", "attempt_count >= 0"));
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).HasColumnName("id");
                entity.Property(x => x.Domain).HasColumnName("domain").IsRequired();
                entity.Property(x => x.OrderingKey).HasColumnName("ordering_key").IsRequired();
                entity.Property(x => x.Operation).HasColumnName("operation").IsRequired();
                entity.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
                entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
                entity.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasDefaultValueSql("now()");
                entity.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
                entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
                entity.Property(x => x.LastError).HasColumnName("last_error");
                entity.HasIndex(x => new { x.NextAttemptAt, x.CreatedAt }, "compatibility_outbox_pending_idx")
                    .HasFilter("completed_at IS NULL");
                entity.HasIndex(x => new { x.OrderingKey, x.CreatedAt }, "compatibility_outbox_ordering_idx")
                    .HasFilter("completed_at IS NULL");
            });

            modelBuilder.Entity<GroupSettingsEntity>(entity =>
            {
                entity.ToTable("group_settings", table =>
                {
                    table.HasCheckConstraint("group_settings_group_check", "setting_group <> ''");
                    table.HasCheckConstraint("group_settings_version_check", "version > 0");
                    table.HasCheckConstraint("group_settings_values_check", "jsonb_typeof(values) = 'object'");
                });
                entity.HasKey(x => new { x.ChatId, x.SettingGroup });
                entity.Property(x => x.ChatId).HasColumnName("chat_id");
                entity.Property(x => x.SettingGroup).HasColumnName("setting_group");
                entity.Property(x => x.Values).HasColumnName("values").HasColumnType("jsonb").IsRequired();
                entity.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1L).IsConcurrencyToken();
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            });

            modelBuilder.Entity<GroupSettingsRedisSync>(entity =>
            {
                entity.ToTable("group_settings_redis_sync", table => table.HasCheckConstraint(
                    "group_settings_redis_sync_revision_check", "source_revision >= 0"));
                entity.HasKey(x => new { x.ChatId, x.SettingGroup });
                entity.Property(x => x.ChatId).HasColumnName("chat_id");
                entity.Property(x => x.SettingGroup).HasColumnName("setting_group");
                entity.Property(x => x.RedisKey).HasColumnName("redis_key").IsRequired();
                entity.Property(x => x.SourceExists).HasColumnName("source_exists");
                entity.Property(x => x.SourceRevision).HasColumnName("source_revision");
                entity.Property(x => x.SourceChecksum).HasColumnName("source_checksum").HasMaxLength(64).IsFixedLength().IsRequired();
                entity.Property(x => x.ObservedAt).HasColumnName("observed_at");
                entity.Property(x => x.PersistedAt).HasColumnName("persisted_at").HasDefaultValueSql("now()");
                entity.HasIndex(x => x.RedisKey).IsUnique();
                entity.HasIndex(x => x.ObservedAt, "group_settings_redis_sync_observed_idx");
            });

            modelBuilder.Entity<UserEntity>(entity =>
            {
                entity.ToTable("users", table => table.HasCheckConstraint("users_id_check", "user_id <> 0"));
                entity.HasKey(x => x.UserId);
                entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.Name).HasColumnName("name");
                entity.Property(x => x.Username).HasColumnName("username");
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
                entity.HasIndex(x => x.Username).HasFilter("username IS NOT NULL");
            });

            modelBuilder.Entity<GroupEntity>(entity =>
            {
                entity.ToTable("groups", table => table.HasCheckConstraint("groups_id_check", "chat_id <> 0"));
                entity.HasKey(x => x.ChatId);
                entity.Property(x => x.ChatId).HasColumnName("chat_id");
                entity.Property(x => x.Name).HasColumnName("name");
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            });

            modelBuilder.Entity<GroupEntitlementEntity>(entity =>
            {
                entity.ToTable("group_entitlements");
                entity.HasKey(x => new { x.ChatId, x.Entitlement });
                entity.Property(x => x.ChatId).HasColumnName("chat_id");
                entity.Property(x => x.Entitlement).HasColumnName("entitlement");
                entity.Property(x => x.Enabled).HasColumnName("enabled");
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GroupContentEntity>(entity =>
            {
                entity.ToTable("group_content_items", table => table.HasCheckConstraint(
                    "group_content_type_check", "content_type IN ('rules', 'about', 'language', 'link')"));
                entity.HasKey(x => new { x.ChatId, x.ContentType });
                entity.Property(x => x.ChatId).HasColumnName("chat_id");
                entity.Property(x => x.ContentType).HasColumnName("content_type");
                entity.Property(x => x.Content).HasColumnName("content").IsRequired();
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<CustomCommandEntity>(entity =>
            {
                entity.ToTable("custom_commands");
                entity.HasKey(x => new { x.ChatId, x.Trigger });
                entity.Property(x => x.ChatId).HasColumnName("chat_id");
                entity.Property(x => x.Trigger).HasColumnName("trigger");
                entity.Property(x => x.Content).HasColumnName("content");
                entity.Property(x => x.MediaType).HasColumnName("media_type");
                entity.Property(x => x.FileId).HasColumnName("file_id");
                entity.Property(x => x.Caption).HasColumnName("caption");
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GroupRoleAssignmentEntity>(entity =>
            {
                entity.ToTable("group_role_assignments", table => table.HasCheckConstraint(
                    "group_role_assignments_role_check", "role IN ('authorized', 'moderator')"));
                entity.HasKey(x => new { x.GroupId, x.UserId, x.Role });
                entity.Property(x => x.GroupId).HasColumnName("group_id");
                entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.Role).HasColumnName("role");
                entity.Property(x => x.AssignedAt).HasColumnName("assigned_at");
                entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<GroupElevationBlockEntity>(entity =>
            {
                entity.ToTable("group_elevation_blocks");
                entity.HasKey(x => new { x.GroupId, x.UserId });
                entity.Property(x => x.GroupId).HasColumnName("group_id");
                entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.BlockedAt).HasColumnName("blocked_at");
                entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<GlobalRoleEntity>(entity =>
            {
                entity.ToTable("global_roles");
                entity.HasKey(x => new { x.UserId, x.Role });
                entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.Role).HasColumnName("role");
                entity.Property(x => x.Active).HasColumnName("active");
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            });

            modelBuilder.Entity<GroupUserPolicyEntity>(entity =>
            {
                entity.ToTable("group_user_policies");
                entity.HasKey(x => new { x.GroupId, x.UserId, x.Policy });
                entity.Property(x => x.GroupId).HasColumnName("group_id");
                entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.Policy).HasColumnName("policy");
                entity.Property(x => x.Enabled).HasColumnName("enabled");
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<WarningBalanceEntity>(entity =>
            {
                entity.ToTable("warning_balances", table =>
                {
                    table.HasCheckConstraint("warning_balances_active_check", "active_quantity >= 0");
                    table.HasCheckConstraint("warning_balances_lifetime_check", "lifetime_quantity >= 0");
                    table.HasCheckConstraint("warning_balances_type_check", "warning_type IN ('standard', 'prewarning', 'media')");
                });
                entity.HasKey(x => new { x.GroupId, x.UserId, x.WarningType });
                entity.Property(x => x.GroupId).HasColumnName("group_id");
                entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.WarningType).HasColumnName("warning_type");
                entity.Property(x => x.ActiveQuantity).HasColumnName("active_quantity");
                entity.Property(x => x.LifetimeQuantity).HasColumnName("lifetime_quantity");
                entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
                entity.HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<WarningEventEntity>(entity =>
            {
                entity.ToTable("warning_events", table => table.HasCheckConstraint(
                    "warning_events_quantity_check", "quantity >= 0"));
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
                entity.Property(x => x.GroupId).HasColumnName("group_id");
                entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.WarningType).HasColumnName("warning_type");
                entity.Property(x => x.Operation).HasColumnName("operation");
                entity.Property(x => x.Quantity).HasColumnName("quantity");
                entity.Property(x => x.ActiveQuantity).HasColumnName("active_quantity");
                entity.Property(x => x.LifetimeQuantity).HasColumnName("lifetime_quantity");
                entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
                entity.HasIndex(x => new { x.GroupId, x.UserId, x.WarningType, x.CreatedAt });
            });

            modelBuilder.Entity<SanctionEntity>(entity =>
            {
                entity.ToTable("sanctions", table =>
                {
                    table.HasCheckConstraint("sanctions_kind_check", "kind IN ('ban','mute','tempban','tempmute')");
                    table.HasCheckConstraint("sanctions_status_check", "status IN ('active','due','revoked','completed')");
                });
                entity.HasKey(x => x.Id); entity.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
                entity.Property(x => x.GroupId).HasColumnName("group_id"); entity.Property(x => x.UserId).HasColumnName("user_id");
                entity.Property(x => x.Kind).HasColumnName("kind"); entity.Property(x => x.Status).HasColumnName("status");
                entity.Property(x => x.Edition).HasColumnName("edition"); entity.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
                entity.Property(x => x.Reason).HasColumnName("reason"); entity.Property(x => x.CreatedAt).HasColumnName("created_at");
                entity.Property(x => x.ExpiresAt).HasColumnName("expires_at"); entity.Property(x => x.ClaimedAt).HasColumnName("claimed_at");
                entity.Property(x => x.ClaimedBy).HasColumnName("claimed_by"); entity.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
                entity.Property(x => x.LegacyKey).HasColumnName("legacy_key");
                entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL");
                entity.HasIndex(x => new { x.Edition, x.Status, x.ExpiresAt });
            });

            modelBuilder.Entity<GlobalSanctionEntity>(entity =>
            {
                entity.ToTable("global_sanctions"); entity.HasKey(x => x.UserId);
                entity.Property(x => x.UserId).HasColumnName("user_id"); entity.Property(x => x.Active).HasColumnName("active");
                entity.Property(x => x.Reason).HasColumnName("reason"); entity.Property(x => x.CreatedAt).HasColumnName("created_at");
                entity.Property(x => x.RevocationReason).HasColumnName("revocation_reason"); entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                entity.Property(x => x.LegacyKey).HasColumnName("legacy_key");
            });

            modelBuilder.Entity<ReportEntity>(entity =>
            {
                entity.ToTable("reports", table => table.HasCheckConstraint("reports_status_check", "status IN ('open','solved','dismissed')"));
                entity.HasKey(x => x.Id); entity.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
                entity.Property(x => x.GroupId).HasColumnName("group_id"); entity.Property(x => x.ReportMessageId).HasColumnName("report_message_id");
                entity.Property(x => x.ReporterUserId).HasColumnName("reporter_user_id"); entity.Property(x => x.ReporterDisplay).HasColumnName("reporter_display");
                entity.Property(x => x.ReportedUserId).HasColumnName("reported_user_id"); entity.Property(x => x.ReportedMessageId).HasColumnName("reported_message_id");
                entity.Property(x => x.Status).HasColumnName("status"); entity.Property(x => x.CreatedAt).HasColumnName("created_at");
                entity.Property(x => x.SolvedByUserId).HasColumnName("solved_by_user_id"); entity.Property(x => x.SolvedByDisplay).HasColumnName("solved_by_display");
                entity.Property(x => x.SolvedAt).HasColumnName("solved_at"); entity.Property(x => x.LegacyKey).HasColumnName("legacy_key");
                entity.HasIndex(x => new { x.GroupId, x.ReportMessageId }).IsUnique();
                entity.HasIndex(x => new { x.GroupId, x.Status, x.CreatedAt });
            });

            modelBuilder.Entity<ReportDeliveryEntity>(entity =>
            {
                entity.ToTable("report_deliveries"); entity.HasKey(x => new { x.ReportId, x.DeliveryIndex });
                entity.Property(x => x.ReportId).HasColumnName("report_id"); entity.Property(x => x.DeliveryIndex).HasColumnName("delivery_index");
                entity.Property(x => x.AdminChatId).HasColumnName("admin_chat_id"); entity.Property(x => x.AdminMessageId).HasColumnName("admin_message_id");
                entity.Property(x => x.ForwardedMessageId).HasColumnName("forwarded_message_id"); entity.Property(x => x.LegacyPayload).HasColumnName("legacy_payload");
                entity.HasOne<ReportEntity>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UserStatisticEntity>(entity => { entity.ToTable("user_statistics", t => t.HasCheckConstraint("user_statistics_messages_check", "messages >= 0")); entity.HasKey(x => x.UserId); entity.Property(x => x.UserId).HasColumnName("user_id"); entity.Property(x => x.Messages).HasColumnName("messages"); entity.Property(x => x.CapturedAt).HasColumnName("captured_at"); });
            modelBuilder.Entity<GroupUserStatisticEntity>(entity => { entity.ToTable("group_user_statistics", t => t.HasCheckConstraint("group_user_statistics_messages_check", "messages >= 0")); entity.HasKey(x => new { x.GroupId, x.UserId }); entity.Property(x => x.GroupId).HasColumnName("group_id"); entity.Property(x => x.UserId).HasColumnName("user_id"); entity.Property(x => x.Messages).HasColumnName("messages"); entity.Property(x => x.LastMessageAt).HasColumnName("last_message_at"); entity.Property(x => x.CapturedAt).HasColumnName("captured_at"); });
            modelBuilder.Entity<BotStatisticBaselineEntity>(entity => { entity.ToTable("bot_statistic_baselines"); entity.HasKey(x => x.Edition); entity.Property(x => x.Edition).HasColumnName("edition"); entity.Property(x => x.Messages).HasColumnName("messages"); entity.Property(x => x.Users).HasColumnName("users"); entity.Property(x => x.Bans).HasColumnName("bans"); entity.Property(x => x.Kicks).HasColumnName("kicks"); entity.Property(x => x.CapturedAt).HasColumnName("captured_at"); });
            modelBuilder.Entity<AuditEventEntity>(entity => { entity.ToTable("audit_events"); entity.HasKey(x => x.Id); entity.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn(); entity.Property(x => x.Edition).HasColumnName("edition"); entity.Property(x => x.EventType).HasColumnName("event_type"); entity.Property(x => x.GroupId).HasColumnName("group_id"); entity.Property(x => x.ActorUserId).HasColumnName("actor_user_id"); entity.Property(x => x.SubjectUserId).HasColumnName("subject_user_id"); entity.Property(x => x.Details).HasColumnName("details").HasColumnType("jsonb"); entity.Property(x => x.OccurredAt).HasColumnName("occurred_at"); entity.Property(x => x.LegacyKey).HasColumnName("legacy_key"); entity.HasIndex(x => x.LegacyKey).IsUnique().HasFilter("legacy_key IS NOT NULL"); entity.HasIndex(x => new { x.GroupId, x.OccurredAt }); });
        }
    }
}
