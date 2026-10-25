using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>The update an error happened while handling, as far as the error chat needs it.</summary>
    internal sealed record ErrorContext(string Kind, long? ChatId, long? UserId, string Text)
    {
        internal static ErrorContext From(Update update)
        {
            if (update == null) return null;
            if (update.CallbackQuery is { } q)
                return new ErrorContext("callback", q.Message?.Chat.Id, q.From?.Id, q.Data);
            if (update.InlineQuery is { } iq)
                return new ErrorContext("inline", null, iq.From?.Id, iq.Query);
            var m = update.Message ?? update.EditedMessage;
            if (m == null) return new ErrorContext(update.Type.ToString(), null, null, null);
            return new ErrorContext("message", m.Chat.Id, m.SenderChat?.Id ?? m.From?.Id, m.Text ?? m.Caption);
        }
    }

    /// <summary>
    /// The one place errors go. Report() writes the full exception to the log, as before, and
    /// sends a short summary to the error chat (Bot.ErrorChatId), rate limited by ErrorThrottle.
    ///
    /// Call it from every catch that is handling a failure rather than an expected outcome. It
    /// never throws and never blocks: the send runs in the background and talks to the API
    /// directly, not through Bot.Send, so a failing report cannot loop back into Report().
    ///
    /// The update being handled is picked up from Current, which RouteAsync sets and which flows
    /// into the work it dispatches, so a catch deep inside a command still says which chat,
    /// user and command caused it.
    /// </summary>
    internal static class ErrorReporter
    {
        private const int MaxReportLength = 3500;
        private const int MaxFrames = 6;
        private const int MaxTextLength = 300;

        private static readonly AsyncLocal<ErrorContext> _current = new AsyncLocal<ErrorContext>();
        private static readonly ErrorThrottle Throttle = new ErrorThrottle(() => DateTime.UtcNow);

#if PREMIUM
        private const string Edition = "premium";
#else
        private const string Edition = "normal";
#endif

        /// <summary>The update being handled on this logical flow, if any.</summary>
        internal static ErrorContext Current
        {
            get => _current.Value;
            set => _current.Value = value;
        }

        /// <summary>Reports an exception. <paramref name="what"/> says what was being attempted.</summary>
        internal static void Report(Exception e, string what = null,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            try
            {
                var where = Where(member, file);
                LogHelper.Error($"{where}{(what != null ? $" ({what})" : "")}: {e}");
                var root = Unwrap(e);
                var key = $"{where}|{root?.GetType().Name}|{Normalise(root?.Message)}";
                Send(key, s => Format(where, what, e, Current, s.Suppressed, s.Dropped, Edition));
            }
            catch
            {
                // Reporting must never fault the caller; it is called from catch blocks.
            }
        }

        /// <summary>Reports a failure that is not an exception, e.g. an API call that returned nothing.</summary>
        internal static void Report(string message,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            try
            {
                var where = Where(member, file);
                LogHelper.Error($"{where}: {message}");
                var key = $"{where}|message|{Normalise(message)}";
                Send(key, s => Format(where, message, null, Current, s.Suppressed, s.Dropped, Edition));
            }
            catch
            {
            }
        }

        /// <summary>
        /// For the process-level handlers: sends synchronously (bounded) because the process may
        /// be about to exit, and does not go through the throttle.
        /// </summary>
        internal static void ReportFatal(Exception e, string what)
        {
            try
            {
                LogHelper.Error($"{what}: {e}");
                var text = Format(what, null, e, null, 0, 0, Edition);
                SendNow(text).Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }

        private static void Send(string key, Func<(int Suppressed, int Dropped), string> format)
        {
            if (!Throttle.TryAcquire(key, out var suppressed, out var dropped))
                return;
            var text = format((suppressed, dropped));
            _ = SendNow(text);
        }

        private static async Task SendNow(string text)
        {
            var api = Bot.Api;
            if (api == null) return; // before Initialize: the log has it
            try
            {
                await api.SendMessage(Bot.ErrorChatId, text, ParseMode.None,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
            }
            catch (Exception e)
            {
                LogHelper.Error($"Error report could not be sent: {Bot.AsApiError(e)?.Message ?? e.Message}");
            }
        }

        /// <summary>Strips the wrappers blocking calls add, to get at the exception that matters.</summary>
        internal static Exception Unwrap(Exception e)
        {
            while (true)
            {
                if (e is AggregateException agg && agg.InnerExceptions.Count > 0)
                    e = agg.InnerExceptions[0];
                else if (e is TargetInvocationException tie && tie.InnerException != null)
                    e = tie.InnerException;
                else
                    return e;
            }
        }

        /// <summary>"File.Member", from the caller info.</summary>
        internal static string Where(string member, string file)
        {
            var name = string.IsNullOrEmpty(file) ? "" : Path.GetFileNameWithoutExtension(file.Replace('\\', '/'));
            if (string.IsNullOrEmpty(name)) return member;
            return string.IsNullOrEmpty(member) ? name : $"{name}.{member}";
        }

        /// <summary>
        /// The throttle key ignores numbers, so the same failure for different chats, users or
        /// message ids counts as one error.
        /// </summary>
        internal static string Normalise(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            var s = Regex.Replace(message, @"-?\d+", "#");
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        internal static string Format(string where, string what, Exception e, ErrorContext ctx,
            int suppressed, int dropped, string edition)
        {
            var sb = new StringBuilder();
            sb.Append("#error #").Append(edition).Append(' ').Append(where).Append('\n');
            if (!string.IsNullOrEmpty(what) && what != where)
                sb.Append(what).Append('\n');

            var root = e == null ? null : Unwrap(e);
            if (root != null)
            {
                sb.Append(root.GetType().Name);
                if (root is ApiRequestException api)
                    sb.Append(' ').Append(api.ErrorCode);
                sb.Append(": ").Append(root.Message).Append('\n');
                var depth = 0;
                for (var inner = root.InnerException; inner != null && depth < 3; inner = inner.InnerException, depth++)
                    sb.Append("Caused by ").Append(inner.GetType().Name).Append(": ").Append(inner.Message).Append('\n');
            }

            if (ctx != null)
            {
                sb.Append(ctx.Kind);
                if (ctx.ChatId != null) sb.Append(" | chat ").Append(ctx.ChatId);
                if (ctx.UserId != null) sb.Append(" | from ").Append(ctx.UserId);
                sb.Append('\n');
                if (!string.IsNullOrEmpty(ctx.Text))
                    sb.Append("Text: ").Append(Truncate(ctx.Text, MaxTextLength)).Append('\n');
            }

            var frames = Frames(root);
            if (frames.Count > 0)
                sb.Append(string.Join("\n", frames)).Append('\n');

            if (suppressed > 0)
                sb.Append($"(+{suppressed} more of this in the last {(int)ErrorThrottle.DefaultWindow.TotalMinutes} min, see log)\n");
            if (dropped > 0)
                sb.Append($"({dropped} other errors not sent, rate limit; see log)\n");

            return Truncate(sb.ToString().TrimEnd(), MaxReportLength);
        }

        /// <summary>The top stack frames, our own code first since that is where the fix goes.</summary>
        private static List<string> Frames(Exception e)
        {
            var lines = (e?.StackTrace ?? "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("at "))
                .ToList();
            var ours = lines.Where(l => l.Contains("Enforcer5")).Take(MaxFrames).ToList();
            return ours.Count > 0 ? ours : lines.Take(MaxFrames).ToList();
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
