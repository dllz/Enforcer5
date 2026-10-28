using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>Where one copy of a report or notice was sent: an admin's private chat or an admin chat.</summary>
    internal sealed record ReportCopy(long ChatId, long MessageId);

    /// <summary>
    /// What a report or an end-of-trial notice is: the group and the member (<c>member</c>), or the
    /// group and the rule (<c>trial</c>), and when it was opened. Each edition sends its own.
    /// </summary>
    internal sealed record ReportKey(string Kind, long ChatId, long? UserId, long? FilterId, DateTimeOffset OpenedAt)
    {
        internal const string Member = "member";
        internal const string Trial = "trial";

        internal static ReportKey ForMember(long chatId, long userId, DateTimeOffset openedAt) =>
            new ReportKey(Member, chatId, userId, null, openedAt);

        internal static ReportKey ForTrial(long chatId, long filterId, DateTimeOffset openedAt) =>
            new ReportKey(Trial, chatId, null, filterId, openedAt);
    }

    /// <summary>A report as stored: its copies and the first decision, if any.</summary>
    internal sealed record StoredReport(ReportKey Key, IReadOnlyList<ReportCopy> Copies, string Decision, long? DecidedBy);

    /// <summary>A rule whose trial is over and whose admins have not been told yet.</summary>
    internal sealed record DueTrial(long ChatId, long FilterId, DateTimeOffset TrialUntil);

    /// <summary>
    /// Detection reports (design section 6, build step 7): the copies sent to admins
    /// (<c>report_copies</c>), the first decision on each, the admins who do not want them in private
    /// (<c>report_admin_optouts</c>), and the rules whose trial is over.
    /// </summary>
    internal interface IDetectionReportRepository
    {
        /// <summary>Records the copies of a report or notice just sent.</summary>
        Task AddCopiesAsync(ReportKey key, IReadOnlyList<ReportCopy> copies);

        /// <summary>
        /// The member's latest report opened since <paramref name="since"/> by this edition, with its copies;
        /// null when there is none. After a restart, so a new sign edits it rather than sending another.
        /// </summary>
        Task<StoredReport> LatestMemberReportAsync(long chatId, long userId, DateTimeOffset since);

        /// <summary>A report or notice by its key, or null.</summary>
        Task<StoredReport> GetAsync(ReportKey key);

        /// <summary>
        /// Writes <paramref name="decision"/> on every copy unless one was decided already. Returns the
        /// decision that stands and who took it: this one, or the earlier one.
        /// </summary>
        Task<(string Decision, long? By)> DecideAsync(ReportKey key, string decision, long actorId, DateTimeOffset at);

        /// <summary>The group's admins who do not want its reports in private.</summary>
        Task<IReadOnlyCollection<long>> OptedOutAsync(long chatId);

        Task SetOptedOutAsync(long chatId, long userId, bool optedOut);

        /// <summary>Rules on trial whose trial ended since <paramref name="since"/> and nobody was told about, oldest first.</summary>
        Task<IReadOnlyList<DueTrial>> DueTrialsAsync(DateTimeOffset now, DateTimeOffset since, int limit);

        /// <summary>
        /// Marks the rule's admins as told about the trial that ended at <paramref name="trialUntil"/>.
        /// False when it was already (the other edition), or the rule changed meanwhile.
        /// </summary>
        Task<bool> ClaimTrialAsync(long filterId, DateTimeOffset trialUntil, DateTimeOffset now);
    }
}
