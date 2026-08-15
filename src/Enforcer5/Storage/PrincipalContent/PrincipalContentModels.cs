using System;
using System.Collections.Generic;

namespace Enforcer5.Storage.PrincipalContent
{
    internal sealed record UserPrincipal(long UserId, string Name, string Username, bool IsBot = false);

    internal sealed record GroupPrincipal(long ChatId, string Name, string Username = null,
        string ChatType = "group");

    internal sealed record GroupEntitlement(long ChatId, string Entitlement, bool Enabled);

    internal sealed record GroupContentItem(long ChatId, string ContentType, string Content);

    internal sealed record CustomCommand(
        long ChatId,
        string Trigger,
        string Content,
        string MediaType,
        string FileId,
        string Caption,
        string FileOwnerEdition = null);

    internal sealed record CustomCommandConflictCandidate(
        Guid CandidateId,
        long ChatId,
        string Trigger,
        string SourceTrigger,
        string Content,
        string MediaType,
        string FileId,
        string Caption,
        string FileOwnerEdition = null);

    internal static class GroupContentTypes
    {
        internal const string Rules = "rules";
        internal const string About = "about";
        internal const string Language = "language";
        internal const string Link = "link";

        internal static readonly IReadOnlyList<string> All = new[] { Rules, About, Language, Link };
    }

    /// <summary>
    /// A legacy record that is migrated but carries a defect worth recording. Telegram always
    /// supplies <c>first_name</c>, so a <c>user:{id}</c> without a <c>name</c> field is a defective
    /// write rather than a user that should be discarded.
    /// </summary>
    internal sealed record PrincipalDataQualityIssue(string SourceKey, string SourceField, string Reason);

    internal sealed class PrincipalContentBackfill
    {
        internal PrincipalContentBackfill(
            IReadOnlyList<UserPrincipal> users,
            IReadOnlyList<GroupPrincipal> groups,
            IReadOnlyList<GroupEntitlement> entitlements,
            IReadOnlyList<GroupContentItem> contentItems,
            IReadOnlyList<CustomCommand> customCommands,
            IReadOnlyList<CustomCommandConflictCandidate> customCommandConflicts = null,
            IReadOnlyList<PrincipalDataQualityIssue> dataQualityIssues = null)
        {
            Users = users;
            Groups = groups;
            Entitlements = entitlements;
            ContentItems = contentItems;
            CustomCommands = customCommands;
            CustomCommandConflicts = customCommandConflicts ?? Array.Empty<CustomCommandConflictCandidate>();
            DataQualityIssues = dataQualityIssues ?? Array.Empty<PrincipalDataQualityIssue>();
        }

        internal IReadOnlyList<UserPrincipal> Users { get; }
        internal IReadOnlyList<GroupPrincipal> Groups { get; }
        internal IReadOnlyList<GroupEntitlement> Entitlements { get; }
        internal IReadOnlyList<GroupContentItem> ContentItems { get; }
        internal IReadOnlyList<CustomCommand> CustomCommands { get; }
        internal IReadOnlyList<CustomCommandConflictCandidate> CustomCommandConflicts { get; }
        internal IReadOnlyList<PrincipalDataQualityIssue> DataQualityIssues { get; }
    }

    internal sealed class PrincipalContentBackfillResult
    {
        internal int UsersWritten { get; set; }
        internal int GroupsWritten { get; set; }
        internal int EntitlementsWritten { get; set; }
        internal int ContentItemsWritten { get; set; }
        internal int CustomCommandsWritten { get; set; }
        internal int DataQualityIssues { get; set; }
    }
}
