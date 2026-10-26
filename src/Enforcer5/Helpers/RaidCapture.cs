using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Storage;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Records what raid detection and the after-the-fact lookup need: who joined which group, when,
    /// how and with whom; how much each member has talked; and what their messages were like
    /// (<see cref="MessageFingerprint"/>), for each member's first <see cref="FirstMessages"/>
    /// messages, one in <see cref="SampleEvery"/> after that, and every message while a raid is on.
    /// <para>
    /// The update path only adds to a buffer in memory; it is written to PostgreSQL right after the
    /// principals, once a minute (<see cref="PrincipalObserver"/>). Off while PostgreSQL is not ready.
    /// Never throws.
    /// </para>
    /// </summary>
    internal static class RaidCapture
    {
        internal const int FirstMessages = 20;
        internal const int SampleEvery = 20;

        private static readonly CaptureBuffer Buffer = new CaptureBuffer();
        private static readonly Random Sampler = new Random();

        /// <summary>True when capture is collected. Settable so tests can turn it on.</summary>
        internal static Func<bool> Enabled { get; set; } = () => PostgresStorage.Ready;

        /// <summary>Says whether a raid is on in a chat, so every message there is fingerprinted. Set by RaidGuard.</summary>
        internal static Func<long, bool> RaidOn { get; set; } = _ => false;

        /// <summary>
        /// A message from <paramref name="message"/>'s sender in a group, whose message count there
        /// is now <paramref name="count"/> (the Redis counter).
        /// </summary>
        internal static void Message(Message message, long count)
        {
            try
            {
                if (!Enabled() || message.From == null || ObservationBuffer.Placeholders.Contains(message.From.Id)) return;
                var chatId = message.Chat.Id;
                var at = Utc(message.Date);
                Buffer.Message(chatId, message.From.Id, at, count);
                string reason = null;
                if (RaidOn(chatId)) reason = "raid";
                else if (count <= FirstMessages) reason = "first";
                else
                {
                    int roll;
                    lock (Sampler) roll = Sampler.Next(SampleEvery);
                    if (roll == 0) reason = "sample";
                }
                if (reason != null)
                    Buffer.Fingerprint(new FingerprintEvent(chatId, message.From.Id, message.MessageId, at, MessageFingerprint.Of(message), reason));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Capturing a message for raid detection");
            }
        }

        internal static void Joined(long chatId, long userId, DateTimeOffset at, string source, ChatInviteLink link, long? addedBy)
        {
            try
            {
                if (!Enabled()) return;
                Buffer.Join(new JoinEvent(chatId, userId, at, source, link?.InviteLink, link?.Name, addedBy));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Capturing a join");
            }
        }

        internal static void Left(long chatId, long userId, DateTimeOffset at)
        {
            try
            {
                if (!Enabled()) return;
                Buffer.Leave(new LeaveEvent(chatId, userId, at));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Capturing a leave");
            }
        }

        /// <summary>Writes what was collected. Called by the principal flush, after the accounts and groups exist.</summary>
        internal static async Task FlushAsync()
        {
            var (batch, dropped) = Buffer.Take();
            if (dropped > 0) ErrorReporter.Report($"Raid capture buffer full; {dropped} events dropped");
            if (batch.IsEmpty || !PostgresStorage.Ready) return;
            try
            {
                await Repositories.RaidCapture.ApplyAsync(batch, DateTimeOffset.UtcNow);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing raid capture ({batch.Members.Count} members, {batch.Joins.Count} joins, " +
                                        $"{batch.Fingerprints.Count} fingerprints); retrying next minute");
                Buffer.Restore(batch);
            }
        }

        internal static DateTimeOffset Utc(DateTime date) => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
    }
}
