using System.Threading;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The buttons a button press redrew its menu with, so the log can say what changed (<see cref="Enforcer5.MenuChangeLog"/>)
    /// without every menu writing its own entry. Set around one callback; the gateway records each edit's keyboard.
    /// </summary>
    internal sealed class MenuCapture
    {
        private static readonly AsyncLocal<MenuCapture> Current = new();

        /// <summary>The keyboard of the last edit made while handling the press, or null.</summary>
        internal InlineKeyboardMarkup Markup { get; private set; }

        internal static MenuCapture Begin()
        {
            var capture = new MenuCapture();
            Current.Value = capture;
            return capture;
        }

        internal static void End() => Current.Value = null;

        internal static void Saw(IRequest request)
        {
            var capture = Current.Value;
            if (capture == null) return;
            var markup = request switch
            {
                EditMessageTextRequest text => text.ReplyMarkup,
                EditMessageReplyMarkupRequest keys => keys.ReplyMarkup,
                _ => null
            };
            if (markup != null) capture.Markup = markup;
        }
    }
}
