using System;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;

namespace Enforcer5.Actions
{
    /// <summary>
    /// What an action changes in the bot's own state once Telegram has taken it (or certainly will):
    /// <c>docs/action-seam.md</c>, section 8. Moved out of the callers, which each had their own copy and
    /// had drifted apart (issue #24). The ban and mute history and Detection's labels stay with the queue
    /// (<c>EnforcementExecutor.Record</c>), which writes them when Telegram has really carried the job out.
    /// </summary>
    internal static class ActionEffects
    {
        /// <summary>Applies what <paramref name="result"/> owns. Never throws: the action itself has happened.</summary>
        internal static void Apply(ActionResult result)
        {
            if (!result.Succeeded) return;
            var request = result.Applied;
            try
            {
                switch (request.Kind)
                {
                    case ActionKind.Ban:
                        // A permanent ban ends any tempban: left in the schedule, it would unban them when it ran out.
                        Repositories.Tempbans.CancelAsync(request.ChatId, request.TargetId).GetAwaiter().GetResult();
                        if (request.BanListReason != null)
                            // A name built for showing can carry a copyable id (Ids); the list stores it plain.
                            Repositories.BanList.SetAsync(request.ChatId, request.TargetId, Ids.Strip(request.BanListName), Ids.Strip(request.BanListReason))
                                .GetAwaiter().GetResult();
                        break;
                    case ActionKind.TempBan:
                        // The latest tempban is the one that counts.
                        Repositories.Tempbans.CancelAsync(request.ChatId, request.TargetId).GetAwaiter().GetResult();
                        Repositories.Tempbans.ScheduleAsync(request.ChatId, request.TargetId, request.UntilUtc!.Value, Clock.UtcNow,
                            ScheduleLabel(request.ChatId, request.TargetId)).GetAwaiter().GetResult();
                        break;
                    case ActionKind.Unban:
                        Repositories.Tempbans.CancelAsync(request.ChatId, request.TargetId).GetAwaiter().GetResult();
                        Repositories.BanList.ClearAsync(request.ChatId, request.TargetId).GetAwaiter().GetResult();
                        break;
                    case ActionKind.Delete:
                        // An admin deleting a member's post decided about them; deleting one's own (/d) did not.
                        if (request.ActorId is { } actor && request.TargetId > 0 && request.TargetId != actor)
                        {
                            if (request.About != null) Decisions.Deleted(request.About, actor);
                            else Decisions.Deleted(request.ChatId, request.TargetId, request.MessageId, actor,
                                request.Source == ActionSource.Report);
                        }
                        break;
                }
                if (request.Stat != null) Methods.SaveBan(request.TargetId, request.Stat);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Recording the {request.Kind} of {request.TargetId} in {request.ChatId}");
            }
        }

        /// <summary><c>{name}:{group}</c> for the schedule entry, as Commands.Tempban wrote it.</summary>
        private static string ScheduleLabel(long chatId, long userId)
        {
            var name = Redis.db.HashGetAsync($"user:{userId}", "name").GetAwaiter().GetResult();
            var group = Redis.db.HashGetAsync($"chat:{chatId}:details", "name").GetAwaiter().GetResult();
            return $"{name}:{group}";
        }
    }
}
