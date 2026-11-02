using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Actions
{
    /// <summary>
    /// /batch (<c>docs/action-seam.md</c>, section 9): one command run on a list of members. The admin replies to
    /// a list of ids (a message, or a .txt/.csv file); the bot previews what would happen; any admin of the group
    /// confirms within 10 minutes; then a worker runs one member at a time through the seam (source
    /// <see cref="ActionSource.Batch"/>, the admin who asked as actor, no notice per member), paced by the
    /// enforcement queue like everything else, editing one progress message at most every 5 seconds, with a
    /// Stop button. The batch and its items are in PostgreSQL, so a restart carries on where it was. At the end:
    /// a summary, and one line in the log channel.
    /// </summary>
    internal static class Batches
    {
        internal const string Trigger = "batch";

        /// <summary>How long a preview can be confirmed.</summary>
        internal static readonly TimeSpan PreviewFor = TimeSpan.FromMinutes(10);

        /// <summary>The least time between two edits of the progress message.</summary>
        internal static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(5);

        private const int Chunk = 50;

        private sealed record Preview(long ChatId, long RequestedBy, BatchOrder Order, IReadOnlyList<long> Members, int Skipped,
            DateTime ExpiresUtc);

        private static readonly ConcurrentDictionary<string, Preview> Previews = new();
        private static readonly ConcurrentDictionary<long, byte> Running = new();
        private static readonly ConcurrentDictionary<long, byte> Stopping = new();
        private static CancellationTokenSource _stop = new();

        // ------------------------------------------------------------------ the command

        /// <summary>/batch &lt;command&gt; [arguments], replying to the list. Answers with the preview.</summary>
        internal static void Command(Update update, string arguments)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (!PostgresStorage.Ready)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "batchNoDatabase"), update);
                return;
            }
            var words = (arguments ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var trigger = words.Length > 0 ? BatchPlan.Find(words[0]) : null;
            var defaultDuration = TimeSpan.FromMinutes(trigger?.Kind == ActionKind.TempMute
                ? Methods.GetGroupTempMuteTime(chatId)
                : Methods.GetGroupTempbanTime(chatId));
            switch (BatchPlan.Read(arguments, defaultDuration, out var order))
            {
                case BatchPlan.ReadResult.UnknownCommand:
                    Bot.SendReply(Methods.GetLocaleString(lang, "batchUsage"), update);
                    return;
                case BatchPlan.ReadResult.InvalidDuration:
                    Bot.SendReply(Methods.GetLocaleString(lang, "invalidDuration"), update);
                    return;
            }
            var ids = ReadList(message.ReplyToMessage);
            if (ids.Count == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "batchNoIds"), update);
                return;
            }
            var distinct = ids.Distinct().ToList();
            if (distinct.Count > BatchPlan.MaxMembers)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "batchTooMany", BatchPlan.MaxMembers), update);
                return;
            }

            // In memory and from caches only: no Telegram call per member.
            var admins = AdminDirectory.Get(chatId).Select(m => m.User.Id).ToHashSet();
            admins.Add(Bot.Me.Id);
            admins.Add(message.From.Id);
            var notAdmins = distinct.Where(id => !admins.Contains(id)).ToList();
            var members = NothingToDo(chatId, order.Command.Kind, notAdmins);
            var duplicates = ids.Count - distinct.Count;
            var adminCount = distinct.Count - notAdmins.Count;
            var nothing = notAdmins.Count - members.Count;

            var token = Guid.NewGuid().ToString("N")[..10];
            Previews[token] = new Preview(chatId, message.From.Id, order, members, adminCount + nothing, Clock.UtcNow + PreviewFor);
            var text = Methods.GetLocaleString(lang, "batchPreview", order.Text, ids.Count, members.Count, duplicates, adminCount, nothing);
            var buttons = members.Count == 0
                ? null
                : new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, "batchRun"), $"{Trigger}:{chatId}:run:{token}"),
                    InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, "batchCancel"), $"{Trigger}:{chatId}:cancel:{token}")
                });
            Bot.SendReply(text, update, buttons);
        }

        /// <summary>The ids of the replied message: its text and caption, or its .txt/.csv file.</summary>
        private static IReadOnlyList<long> ReadList(Message list)
        {
            if (list == null) return Array.Empty<long>();
            if (list.Document is { } doc && (doc.FileName ?? "").ToLowerInvariant() is var name &&
                (name.EndsWith(".txt") || name.EndsWith(".csv")))
            {
                // A list of 5,000 ids is about 60 KB; anything much bigger is not one.
                if (doc.FileSize > 1_000_000) return Array.Empty<long>();
                using var content = new System.IO.MemoryStream();
                Bot.Files.DownloadAsync(doc.FileId, content).GetAwaiter().GetResult();
                return BatchPlan.Ids(Encoding.UTF8.GetString(content.ToArray()));
            }
            return BatchPlan.Ids($"{list.Text}\n{list.Caption}");
        }

        /// <summary>
        /// Those on the list with something to do: not already banned (for a ban), with warns to take off (for a
        /// warn removal). From Redis, as the commands read it.
        /// </summary>
        private static List<long> NothingToDo(long chatId, ActionKind kind, List<long> members)
        {
            Func<long, Task<bool>> needed = kind switch
            {
                ActionKind.Ban => async id => await Repositories.BanList.GetReasonAsync(chatId, id) == null,
                ActionKind.RemoveWarn or ActionKind.ResetWarns => async id =>
                    await Repositories.Warns.GetCountAsync(chatId, WarnKind.Warn, id) > 0,
                _ => null
            };
            if (needed == null) return members;
            var keep = new List<long>();
            foreach (var chunk in members.Chunk(200))
            {
                var answers = Task.WhenAll(chunk.Select(needed)).GetAwaiter().GetResult();
                keep.AddRange(chunk.Where((_, i) => answers[i]));
            }
            return keep;
        }

        // ------------------------------------------------------------------ the buttons

        /// <summary>batch:{chat}:run|cancel:{token}, batch:{chat}:stop:{batch}. The dispatcher has checked the presser is an admin.</summary>
        internal static void Button(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var op = args.Length > 2 ? args[2] : "";
            var key = args.Length > 3 ? args[3] : "";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            switch (op)
            {
                case "run":
                    Confirm(call, chatId, key, lang);
                    break;
                case "cancel":
                    Previews.TryRemove(key, out _);
                    Answer(call, null);
                    Edit(chatId, call.Message.MessageId, Methods.GetLocaleString(lang, "batchCancelled"), null);
                    break;
                case "stop" when long.TryParse(key, out var batchId):
                    Stop(call, chatId, batchId, lang);
                    break;
                default:
                    Answer(call, null);
                    break;
            }
        }

        private static void Confirm(CallbackQuery call, long chatId, string token, XDocument lang)
        {
            if (!Previews.TryRemove(token, out var preview) || preview.ExpiresUtc < Clock.UtcNow || preview.ChatId != chatId)
            {
                Answer(call, Methods.GetLocaleString(lang, "batchExpired"));
                Edit(chatId, call.Message.MessageId, Methods.GetLocaleString(lang, "batchExpired"), null);
                return;
            }
            var order = preview.Order;
            var id = Repositories.Batches.CreateAsync(new NewBatch(chatId, BanHistory.Edition, order.Command.Trigger, order.Reason,
                    order.Duration is { } d ? (int)d.TotalMinutes : null, order.Text, preview.RequestedBy, call.From.Id,
                    preview.Skipped, preview.Members))
                .GetAwaiter().GetResult();
            if (id == null)
            {
                // Kept, so it can be confirmed once the running one is over.
                Previews[token] = preview;
                Answer(call, Methods.GetLocaleString(lang, "batchRunning"));
                return;
            }
            Answer(call, null);
            Repositories.Batches.SetProgressMessageAsync(id.Value, call.Message.MessageId).GetAwaiter().GetResult();
            Edit(chatId, call.Message.MessageId, Methods.GetLocaleString(lang, "batchProgress", id.Value, order.Text, 0, preview.Members.Count),
                StopButton(lang, chatId, id.Value));
            Start(id.Value);
        }

        private static void Stop(CallbackQuery call, long chatId, long batchId, XDocument lang)
        {
            Answer(call, null);
            Stopping[batchId] = 0;
            var stopped = Repositories.Batches.FinishAsync(batchId, "stopped", Now).GetAwaiter().GetResult();
            // A worker running it ends it itself; with none (it ended meanwhile, or this process never ran it), here.
            if (stopped && !Running.ContainsKey(batchId))
                Bot.Dispatch(() => EndAsync(batchId), "Batch.Stopped");
        }

        private static void Answer(CallbackQuery call, string text)
        {
            try
            {
                Bot.Api.AnswerCallbackQuery(call.Id, text, showAlert: text != null).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // A button answered too late (Telegram allows about 15 s) only leaves the spinner: not an error.
                LogHelper.Error($"Answering a batch button: {e.Message}");
            }
        }

        private static InlineKeyboardMarkup StopButton(XDocument lang, long chatId, long batchId) =>
            new(InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, "batchStop"), $"{Trigger}:{chatId}:stop:{batchId}"));

        private static void Edit(long chatId, int messageId, string text, InlineKeyboardMarkup markup)
        {
            try
            {
                Bot.Edit(chatId, messageId, text, markup).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // Deleted by an admin, or the same text again: the batch goes on regardless.
                LogHelper.Error($"Editing batch message {messageId} in {chatId}: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ the worker

        /// <summary>Carries on with the edition's running batches (after a restart). Never throws.</summary>
        internal static async Task ResumeAsync()
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                foreach (var id in await Repositories.Batches.RunningAsync(BanHistory.Edition)) Start(id);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Carrying on with the running batches");
            }
        }

        /// <summary>At startup: resumes the running batches once PostgreSQL is there.</summary>
        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested && !PostgresStorage.Ready)
                    await Clock.WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (!ct.IsCancellationRequested) await ResumeAsync();
            }, stop, "Batches");

        private static void Start(long batchId)
        {
            if (!Running.TryAdd(batchId, 0)) return;
            var stop = _stop.Token;
            Bot.Dispatch(async () =>
            {
                try
                {
                    await RunAsync(batchId, stop);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Running batch {batchId}");
                }
                finally
                {
                    Running.TryRemove(batchId, out _);
                }
            }, "Batch");
        }

        private static async Task RunAsync(long batchId, CancellationToken stop)
        {
            var batch = await Repositories.Batches.GetAsync(batchId);
            if (batch == null || batch.Status != "running") return;
            var chatId = batch.ChatId;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var command = BatchPlan.Find(batch.Kind);
            // Detection labels these decisions batch, and the error chat says which batch went wrong.
            ErrorReporter.Current = new ErrorContext("batch", chatId, batch.RequestedBy, $"/batch {batch.CommandText}")
            {
                LabelSource = ActionSources.Label(ActionSource.Batch)
            };
            var done = batch.Total - (await Repositories.Batches.PendingAsync(batchId, int.MaxValue)).Count;
            var lastEdit = Clock.UtcNow;
            while (true)
            {
                var items = await Repositories.Batches.PendingAsync(batchId, Chunk);
                if (items.Count == 0) break;
                foreach (var item in items)
                {
                    if (stop.IsCancellationRequested) return; // the process is going: the next start carries on
                    if (Stopping.ContainsKey(batchId)) break;
                    // Step 2 of the seam for a batch: the admin who asked must still be one when each member's turn comes.
                    if (await AdminDirectory.IsAdminAsync(chatId, batch.RequestedBy) == false)
                    {
                        await Repositories.Batches.SetOutcomeAsync(batchId, item.Position, "skipped", "notadmin", Now);
                        Stopping[batchId] = 0;
                        await Repositories.Batches.FinishAsync(batchId, "stopped", Now);
                        break;
                    }
                    var result = ActionSeam.Run(Request(batch, command, item.MemberId));
                    var (outcome, detail) = Outcome(result);
                    await Repositories.Batches.SetOutcomeAsync(batchId, item.Position, outcome, detail, Now);
                    done++;
                    if (Clock.UtcNow - lastEdit >= ProgressEvery && batch.ProgressMessageId is { } progress)
                    {
                        lastEdit = Clock.UtcNow;
                        Edit(chatId, progress, Methods.GetLocaleString(lang, "batchProgress", batchId, batch.CommandText, done, batch.Total),
                            StopButton(lang, chatId, batchId));
                    }
                }
                if (Stopping.ContainsKey(batchId)) break;
            }
            if (!Stopping.ContainsKey(batchId)) await Repositories.Batches.FinishAsync(batchId, "done", Now);
            await EndAsync(batchId);
        }

        /// <summary>One member's action. Silent: the summary speaks for the batch.</summary>
        private static ActionRequest Request(BatchRecord batch, BatchCommand command, long memberId)
        {
            var until = batch.DurationMinutes is { } minutes ? Clock.UtcNow.AddMinutes(minutes) : (DateTime?)null;
            return new ActionRequest
            {
                Kind = command.Kind, ChatId = batch.ChatId, TargetId = memberId, ActorId = batch.RequestedBy,
                Source = ActionSource.Batch, BatchId = batch.BatchId, Reason = batch.Reason, UntilUtc = until,
                WarnKind = WarnKind.Warn,
                // The member's ban record, as the commands count it.
                Stat = command.Kind switch
                {
                    ActionKind.Ban => "ban",
                    ActionKind.TempBan => "tempban",
                    ActionKind.Kick => "kick",
                    _ => null
                },
                BanListReason = command.Kind == ActionKind.Ban ? batch.Reason ?? "" : null,
                BanListName = command.Kind == ActionKind.Ban ? Service.GetCachedUserLabel(memberId) : null
            };
        }

        private static (string Outcome, string Detail) Outcome(ActionResult result) => result.Outcome switch
        {
            ActionOutcome.Done or ActionOutcome.Queued => ("done", null),
            ActionOutcome.NothingToDo => ("nothing", null),
            ActionOutcome.Blocked => ("blocked", result.BlockedBy),
            _ => ("refused", result.Refusal switch
            {
                Refusal.NotFound => "notfound",
                Refusal.TargetIsAdmin => "admin",
                Refusal.BotNotAdmin => "norights",
                _ => "other"
            })
        };

        // An item's detail (what Outcome stored) and the summary line that explains it.
        private static readonly Dictionary<string, string> WhyKeys = new()
        {
            ["notfound"] = "batchWhyNotFound",
            ["admin"] = "batchWhyAdmin",
            ["norights"] = "batchWhyNoRights",
            ["notadmin"] = "batchWhyNotAdmin"
        };

        private static string WhyLine(XDocument lang, BatchTally tally)
        {
            if (tally.Outcome == "blocked") return Methods.GetLocaleString(lang, "batchWhyRule", tally.Detail, tally.Count);
            var key = tally.Detail != null && WhyKeys.TryGetValue(tally.Detail, out var known) ? known : "batchWhyOther";
            return Methods.GetLocaleString(lang, key, tally.Count);
        }

        /// <summary>The summary in the progress message, and the line in the log channel.</summary>
        private static async Task EndAsync(long batchId)
        {
            Stopping.TryRemove(batchId, out _);
            var batch = await Repositories.Batches.GetAsync(batchId);
            if (batch == null) return;
            var tally = await Repositories.Batches.TallyAsync(batchId);
            int Count(params string[] outcomes) => tally.Where(t => outcomes.Contains(t.Outcome)).Sum(t => t.Count);
            var lang = Methods.GetGroupLanguage(batch.ChatId).Doc;
            var done = Count("done");
            var refused = Count("refused");
            var blocked = Count("blocked");
            var skipped = Count("skipped", "nothing") + batch.Skipped;
            var details = string.Join("\n", tally.Where(t => t.Outcome is "refused" or "blocked" || t.Detail == "notadmin")
                .Select(t => WhyLine(lang, t)));
            var ended = batch.Status == "stopped" ? "batchStopped" : "batchDone";
            var summary = Methods.GetLocaleString(lang, ended, batch.BatchId, batch.CommandText, done, refused, blocked, skipped);
            if (details.Length > 0) summary += "\n" + details;
            if (batch.ProgressMessageId is { } progress) Edit(batch.ChatId, progress, summary, null);
            else Bot.Send(summary, batch.ChatId);
            var group = Service.GetCachedGroupTitle(batch.ChatId);
            Service.SendToLogChannel(batch.ChatId, lang, () => Methods.GetLocaleString(lang, "batchLog",
                Service.GetCachedUserLabel(batch.RequestedBy), $"{group} ({batch.ChatId})", batch.CommandText,
                Service.GetCachedUserLabel(batch.ConfirmedBy), done, refused, blocked, skipped), batch.ProgressMessageId);
        }

        private static DateTimeOffset Now => new(Clock.UtcNow, TimeSpan.Zero);

        /// <summary>The process dies: workers stop between two members, previews are forgotten.</summary>
        internal static void ResetForTests()
        {
            _stop.Cancel();
            _stop = new CancellationTokenSource();
            Previews.Clear();
            Running.Clear();
            Stopping.Clear();
        }
    }
}
