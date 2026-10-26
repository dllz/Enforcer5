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
            UseSnakeCaseColumns(modelBuilder);
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
