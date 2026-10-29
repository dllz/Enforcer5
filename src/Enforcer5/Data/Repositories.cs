using System;
using Enforcer5.Helpers;

namespace Enforcer5.Data
{
    /// <summary>
    /// The one place that decides which store backs each repository. Commands are bound by
    /// reflection as static methods, so there is no constructor to inject through; they and the
    /// update path take their repositories from here instead. Moving a repository to another
    /// database means changing the line below and nothing else. Settable so a test can substitute
    /// a fake.
    /// </summary>
    internal static class Repositories
    {
        // The group's detection settings (PostgreSQL, cached; see Detection/DetectionSettings). The
        // Redis implementations are only read by the import (Detection/LegacySettings).
        internal static IInlineBotBlockRepository InlineBotBlocks { get; set; } =
            new PolicyInlineBotBlockRepository();

        internal static IStickerSetBlockRepository StickerSetBlocks { get; set; } =
            new PolicyStickerSetBlockRepository();

        internal static IWarnRepository Warns { get; set; } =
            new RedisWarnRepository(() => Redis.db);

        internal static IWarnReasonRepository WarnReasons { get; set; } =
            new RedisWarnReasonRepository(() => Redis.db);

        internal static IChannelPostRepository ChannelPosts { get; set; } =
            new PolicyChannelPostRepository(() => Redis.db);

        internal static IMuteRepository Mutes { get; set; } =
            new RedisMuteRepository(() => Redis.db);

        // PostgreSQL. Only used once PostgresStorage is ready; see Helpers/HistoryWriter.
        internal static IBanHistoryRepository BanHistory { get; set; } =
            new PostgresBanHistoryRepository(() => Storage.PostgresStorage.Database);

        internal static IWarningHistoryRepository WarningHistory { get; set; } =
            new PostgresWarningHistoryRepository(() => Storage.PostgresStorage.Database);

        internal static IReportHistoryRepository ReportHistory { get; set; } =
            new PostgresReportHistoryRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL only, no Redis copy: global bans run on it. Read through Helpers/GlobalBans.
        internal static IGlobalBanRepository GlobalBans { get; set; } =
            new PostgresGlobalBanRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. Fed by Helpers/PrincipalObserver once PostgresStorage is ready.
        internal static IPrincipalRepository Principals { get; set; } =
            new PostgresPrincipalRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. Fed by Helpers/RaidCapture once PostgresStorage is ready.
        internal static IRaidCaptureRepository RaidCapture { get; set; } =
            new PostgresRaidCaptureRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. What Detection learns from: detections and decisions (Detection/Decisions).
        internal static IDetectionLogRepository DetectionLog { get; set; } =
            new PostgresDetectionLogRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. Detection reports to admins, their copies and decisions (Detection/DetectionReports).
        internal static IDetectionReportRepository DetectionReports { get; set; } =
            new PostgresDetectionReportRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. What Detection learns a group's levels from, and members set back to new (Detection/TrustLearning).
        internal static IDetectionLearningRepository DetectionLearning { get; set; } =
            new PostgresDetectionLearningRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. What the profile and channel lookups found: flags only (Detection/Profiles).
        internal static IProfileRepository Profiles { get; set; } =
            new PostgresProfileRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. The files admins banned in each group (Detection/BannedMedia).
        internal static IBannedMediaRepository BannedMedia { get; set; } =
            new PostgresBannedMediaRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. New members' features, what they pointed at, reputation, retention (Detection/MemberData, Reputation).
        internal static IDetectionDataRepository DetectionData { get; set; } =
            new PostgresDetectionDataRepository(() => Storage.PostgresStorage.Database);

        internal static IRaidTelemetryRepository RaidTelemetry { get; set; } =
            new PostgresRaidTelemetryRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. The admin chats groups linked for their reports (Detection/AdminChats).
        internal static IAdminChatRepository AdminChats { get; set; } =
            new PostgresAdminChatRepository(() => Storage.PostgresStorage.Database);

        // PostgreSQL. Data recovered after a data loss, offered to each group's admins (Recovery/RecoveryOffers).
        internal static IRecoveryRepository Recovery { get; set; } =
            new PostgresRecoveryRepository(() => Storage.PostgresStorage.Database);

        // Redis. The stored values a recovery offer sets once admins apply it.
        internal static IRecoveryValues RecoveryValues { get; set; } = new RedisRecoveryValues(() => Redis.db);

        internal static ITempbanRepository Tempbans { get; set; } =
#if PREMIUM
            new RedisTempbanRepository(() => Redis.db, "tempbannedPremium");
#else
            new RedisTempbanRepository(() => Redis.db, "tempbanned");
#endif

        internal static ISlowLaneRepository SlowLane { get; set; } =
            new RedisSlowLaneRepository(() => Redis.db, TimeSpan.FromHours(24));

        // Per edition: each carries out its own jobs. See Enforcement/EnforcementQueue.
        internal static IEnforcementJournal EnforcementJournal { get; set; } =
            new RedisEnforcementJournal(() => Redis.db, Enforcer5.Helpers.BanHistory.Edition);
    }
}
