using System;
using Enforcer5.Helpers;

namespace Enforcer5.Actions
{
    /// <summary>
    /// The log channel line of an action (<c>docs/action-seam.md</c>, step E), in the wording the callers
    /// used to post themselves, and only once the action went through (done or queued):
    /// <list type="bullet">
    /// <item>an admin's command: the command as typed, who it was about and a link to it, once per command
    /// however many actions it ran (<see cref="Service.LogCommand(Telegram.Bot.Types.Update, string)"/>);</item>
    /// <item>an action with its own <see cref="ActionRequest.Log"/> (the bot's own actions, a warn removal's
    /// readable line): that line instead.</item>
    /// </list>
    /// Buttons are logged by the dispatcher as they were pressed (<see cref="Service.LogCallback"/>), and an
    /// action on oneself (/d of one's own post, /kickme) is not logged.
    /// </summary>
    internal static class ActionLog
    {
        /// <summary>Posts the line <paramref name="result"/> owns, if any. Never throws.</summary>
        internal static void Write(ActionResult result)
        {
            if (!result.Succeeded) return;
            var request = result.Applied;
            try
            {
                if (request.Log is { } entry)
                {
                    var text = entry.Text(result);
                    if (text == null) return;
                    if (entry.ByBot) Service.LogBotAction(request.ChatId, text, request.TargetId > 0 ? request.TargetId : null);
                    else Service.SendToLogChannel(request.ChatId, Methods.GetGroupLanguage(request.ChatId).Doc, () => text, entry.LinkMessageId);
                    return;
                }
                if (request.Source != ActionSource.Command || request.ActorId is not { } actor) return;
                if (!request.Record || request.TargetId == actor) return;
                var update = ErrorReporter.Current?.Update;
                if (update?.Message?.From?.Id != actor) return;
                Service.LogCommand(request.ChatId, request.TargetId, update);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Logging the {request.Kind} of {request.TargetId} in {request.ChatId}");
            }
        }
    }
}
