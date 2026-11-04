using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Enforcement
{
    /// <summary>Why a kick's unban was not done.</summary>
    internal enum KickFailure
    {
        /// <summary>The bot lost its rights between the ban and the unban.</summary>
        NoRights,
        /// <summary>Telegram still had them banned after <see cref="EnforcementQueue.KickUnbanFor"/>.</summary>
        GaveUp
    }

    /// <summary>
    /// A kick's message in the group, kept up to date with edits (Daniel, 2026-11-04): "removed from the
    /// group" once the ban went through, then "unbanned, can rejoin", or that the unban failed. A failed
    /// unban is never silent: the group hears it (a new message if the edit cannot be made) and so does
    /// the person, in private. Kicks in a raid's quiet mode and in a batch tell only a failure.
    /// </summary>
    internal static class KickNotices
    {
        // Job id → the "removed" message being posted, so the edit after it waits for its id.
        private static readonly ConcurrentDictionary<string, Task<int?>> Posting = new();

        /// <summary>What the kick's message will need, from the update that asked for it.</summary>
        internal static KickNotice Describe(long chatId, long targetId, long? actorId, bool failuresOnly)
        {
            var update = ErrorReporter.Current?.Update;
            return new KickNotice
            {
                TargetName = NameOf(targetId, update),
                ActorName = actorId is { } actor && actor != targetId ? NameOf(actor, update) : null,
                // Only in the same chat: a report's button is pressed in an admin's private chat.
                ReplyTo = update?.Message is { } m && m.Chat.Id == chatId ? m.MessageId
                    : update?.CallbackQuery?.Message is { } c && c.Chat.Id == chatId ? c.MessageId
                    : null,
                FailuresOnly = failuresOnly
            };
        }

        /// <summary>The ban went through and the unban is still to come.</summary>
        internal static void Removed(EnforcementJob job)
        {
            if (!Progress(job)) return;
            var posted = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Posting[job.Id] = posted.Task;
            Bot.Dispatch(() =>
            {
                try
                {
                    var message = Post(job, RemovedLine(job, Lang(job)));
                    if (message != null) job.Note.Kick.MessageId = message.MessageId;
                    posted.TrySetResult(message?.MessageId);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Posting the kick notice for {job}");
                    posted.TrySetResult(null);
                }
                return Task.CompletedTask;
            }, "KickNotice");
        }

        /// <summary>They are unbanned and can rejoin.</summary>
        internal static void Unbanned(EnforcementJob job)
        {
            if (!Progress(job)) return;
            Bot.Dispatch(async () =>
            {
                var lang = Lang(job);
                await Update(job, $"{RemovedLine(job, lang)}\n{Methods.GetLocaleString(lang, "kickUnbanned", job.Note.Kick.TargetName)}");
            }, "KickNotice");
        }

        /// <summary>The unban failed for good: the group and the person are told.</summary>
        internal static void Failed(EnforcementJob job, KickFailure why)
        {
            var notice = job.Note.Kick;
            if (notice == null)
            {
                // A job without a notice (none outside tests): the error chat is all there is.
                ErrorReporter.Report(new InvalidOperationException($"{job}: left banned ({why})"), "A kick's unban failed");
                return;
            }
            Bot.Dispatch(async () =>
            {
                var lang = Lang(job);
                var failed = Methods.GetLocaleString(lang, why == KickFailure.NoRights ? "kickUnbanFailedRights" : "kickUnbanFailedTimeout",
                    notice.TargetName);
                await Update(job, $"{RemovedLine(job, lang)}\n{failed}");
                var group = GroupName(job.ChatId);
                // Bot.Send says nothing about someone who never started the bot or blocked it; the group heard it.
                Bot.Send(Methods.GetLocaleString(lang, "kickUnbanFailedPrivate", group), job.TargetId);
            }, "KickNotice");
        }

        // Edits the "removed" message into text, or posts text when there is none to edit.
        private static async Task Update(EnforcementJob job, string text)
        {
            var notice = job.Note.Kick;
            int? id = notice.MessageId;
            if (id == null && Posting.TryRemove(job.Id, out var posting)) id = await posting;
            else Posting.TryRemove(job.Id, out _);
            if (id is { } messageId)
            {
                try
                {
                    await Bot.Edit(job.ChatId, messageId, text, parseMode: ParseMode.Html);
                    return;
                }
                catch (Exception e)
                {
                    var api = Bot.AsApiError(e) ?? ErrorReporter.Unwrap(e) as Telegram.Bot.Exceptions.ApiRequestException;
                    if (api?.Message?.Contains("message is not modified") == true) return;
                    // Deleted by an admin, too old, or rate limited: posted anew below, never dropped.
                    LogHelper.Info($"Editing the kick notice for {job} failed, posting it anew: {api?.Message ?? e.Message}");
                }
            }
            Post(job, text);
        }

        // A reply to what the kick was about (sent alone if that is gone), or a plain message.
        private static Message Post(EnforcementJob job, string text) =>
            job.Note.Kick.ReplyTo is { } replyTo ? Bot.SendReply(text, job.ChatId, replyTo) : Bot.Send(text, job.ChatId);

        /// <summary>The kick gave way to a later job for the same person: its message stays as it is.</summary>
        internal static void Forget(EnforcementJob job) => Posting.TryRemove(job.Id, out _);

        // "Removed" and "unbanned" are told unless a raid keeps the group quiet or a batch reports at its end.
        private static bool Progress(EnforcementJob job) =>
            job.Note.Kick is { FailuresOnly: false } && !Raids.RaidGuard.Quiet(job.ChatId);

        private static string RemovedLine(EnforcementJob job, XDocument lang) =>
            job.Note.Kick.ActorName is { } actor
                ? Methods.GetLocaleString(lang, "kickRemovedBy", job.Note.Kick.TargetName, actor)
                : Methods.GetLocaleString(lang, "kickRemoved", job.Note.Kick.TargetName);

        private static XDocument Lang(EnforcementJob job) => Methods.GetGroupLanguage(job.ChatId).Doc;

        // "Name (id)", as HTML: from the update when they are in it, else the name the bot last saw.
        private static string NameOf(long userId, Update update)
        {
            var user = update?.Message?.From?.Id == userId ? update.Message.From
                : update?.Message?.ReplyToMessage?.From?.Id == userId ? update.Message.ReplyToMessage.From
                : update?.CallbackQuery?.From?.Id == userId ? update.CallbackQuery.From
                : null;
            string name = user?.FirstName;
            if (name == null)
            {
                try
                {
                    name = Redis.db.HashGetAsync($"user:{userId}", "name").GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    // The id alone still says who it was.
                    LogHelper.Error($"Reading the name of {userId}: {e.Message}");
                }
            }
            return string.IsNullOrEmpty(name) ? userId.ToString() : $"{name.FormatHTML()} ({userId})";
        }

        private static string GroupName(long chatId)
        {
            try
            {
                string name = Redis.db.HashGetAsync($"chat:{chatId}:details", "name").GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(name)) return name.FormatHTML();
            }
            catch (Exception e)
            {
                // The id is a poor name, but the message still goes out.
                LogHelper.Error($"Reading the name of {chatId}: {e.Message}");
            }
            return chatId.ToString();
        }

        internal static void ResetForTests() => Posting.Clear();
    }
}
