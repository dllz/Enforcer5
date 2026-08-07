using System;
using System.Collections.Generic;

namespace Enforcer5.Storage.PrincipalContent
{
    internal sealed record UserPrincipal(long UserId, string Name, string Username);

    internal sealed record GroupPrincipal(long ChatId, string Name);

    internal sealed record GroupEntitlement(long ChatId, string Entitlement, bool Enabled);

    internal sealed record GroupContentItem(long ChatId, string ContentType, string Content);

    internal sealed record CustomCommand(
        long ChatId,
        string Trigger,
        string Content,
        string MediaType,
        string FileId,
        string Caption);

    internal static class GroupContentTypes
    {
        internal const string Rules = "rules";
        internal const string About = "about";
        internal const string Language = "language";
        internal const string Link = "link";

        internal static readonly IReadOnlyList<string> All = new[] { Rules, About, Language, Link };
    }

    internal sealed class PrincipalContentBackfill
    {
        internal PrincipalContentBackfill(
            IReadOnlyList<UserPrincipal> users,
            IReadOnlyList<GroupPrincipal> groups,
            IReadOnlyList<GroupEntitlement> entitlements,
            IReadOnlyList<GroupContentItem> contentItems,
            IReadOnlyList<CustomCommand> customCommands)
        {
            Users = users;
            Groups = groups;
            Entitlements = entitlements;
            ContentItems = contentItems;
            CustomCommands = customCommands;
        }

        internal IReadOnlyList<UserPrincipal> Users { get; }
        internal IReadOnlyList<GroupPrincipal> Groups { get; }
        internal IReadOnlyList<GroupEntitlement> Entitlements { get; }
        internal IReadOnlyList<GroupContentItem> ContentItems { get; }
        internal IReadOnlyList<CustomCommand> CustomCommands { get; }
    }

    internal sealed class PrincipalContentBackfillResult
    {
        internal int UsersWritten { get; set; }
        internal int GroupsWritten { get; set; }
        internal int EntitlementsWritten { get; set; }
        internal int ContentItemsWritten { get; set; }
        internal int CustomCommandsWritten { get; set; }
    }
}
