using Enforcer5.Actions;
using Enforcer5.Attributes;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static partial class Commands
    {
        /// <summary>/batch &lt;command&gt; [arguments], replying to a list of ids: one command on many members (Actions/Batches).</summary>
        [Command(Trigger = "batch", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true, Menu = CommandMenu.GroupAdmins)]
        public static void Batch(Update update, string[] args) => Batches.Command(update, args.Length > 1 ? args[1] : null);
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = Batches.Trigger, GroupAdminOnly = true)]
        public static void BatchButton(CallbackQuery call, string[] args) => Batches.Button(call, args);
    }
}
