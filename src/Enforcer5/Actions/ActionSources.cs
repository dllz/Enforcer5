using Enforcer5.Detection;
using Telegram.Bot.Types;

namespace Enforcer5.Actions
{
    /// <summary>
    /// Where an action came from when the caller does not say: worked out from the update being handled,
    /// as <see cref="Decisions"/> always has for its labels.
    /// </summary>
    internal static class ActionSources
    {
        /// <summary>
        /// The source of an action asked for while <paramref name="update"/> is handled. In this order: from a
        /// report; the bot's own (no actor); a Detection report's or raid alert's button; any other button;
        /// a command.
        /// </summary>
        internal static ActionSource Derive(Update update, long? actorId, bool fromReport)
        {
            if (fromReport) return ActionSource.Report;
            if (actorId == null) return ActionSource.Auto;
            return Decisions.ButtonSource(update?.CallbackQuery?.Data) switch
            {
                "report" => ActionSource.Report,
                "raid" => ActionSource.Raid,
                _ => update?.CallbackQuery != null ? ActionSource.Button : ActionSource.Command
            };
        }

        /// <summary>
        /// The source as stored in <c>member_labels.source</c>. Detection learns from these, so they stay the
        /// values they always were: everything the bot does on its own is <c>auto</c>.
        /// </summary>
        internal static string Label(ActionSource source) => source switch
        {
            ActionSource.Command => "command",
            ActionSource.Button => "button",
            ActionSource.Report => "report",
            ActionSource.Raid => "raid",
            _ => "auto"
        };
    }
}
