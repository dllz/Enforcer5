using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Enforcer5.Helpers
{
    internal static class TaskExtensions
    {
        /// <summary>
        /// A Telegram call nobody waits for (a button's answer, a menu redraw, a media extra): its failure is reported
        /// with the update it was made for, as if it had been awaited in a catch, and an expected one (TelegramErrors:
        /// the tap too old, the menu not modified, no rights) only logged. Before 2026-11-06 these surfaced as
        /// "Unobserved task exception" without a chat, whenever the garbage collector got to them.
        /// </summary>
        internal static void Forget(this Task task, string what = null,
            [CallerMemberName] string member = "", [CallerFilePath] string file = "")
        {
            if (task == null || task.IsCompletedSuccessfully) return;
            var context = ErrorReporter.Current;
            task.ContinueWith(t =>
            {
                var e = t.Exception?.Flatten();
                if (e == null) return;
                ErrorReporter.Current = context;
                ErrorReporter.Report(e, what, member, file);
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
