namespace Enforcer5.Models
{
    internal enum RestrictFailure
    {
        /// <summary>The bot is not an admin, or lacks the right to restrict members.</summary>
        BotNotAdmin,

        /// <summary>The target is an admin or the owner, whom Telegram never lets a bot restrict.</summary>
        TargetIsAdmin,

        Other
    }

    internal static class RestrictFailures
    {
        /// <summary>
        /// Sorts Telegram's error description for restrictChatMember. Matched loosely and without
        /// case, because Telegram has changed the wording before ("Not enough rights to restrict/
        /// unrestrict chat member", "CHAT_ADMIN_REQUIRED").
        /// </summary>
        internal static RestrictFailure Classify(string description)
        {
            var text = description?.ToLowerInvariant() ?? "";
            if (text.Contains("not enough rights") || text.Contains("chat_admin_required") ||
                text.Contains("need administrator rights"))
                return RestrictFailure.BotNotAdmin;
            if (text.Contains("administrator of the chat") || text.Contains("chat owner"))
                return RestrictFailure.TargetIsAdmin;
            return RestrictFailure.Other;
        }
    }
}
