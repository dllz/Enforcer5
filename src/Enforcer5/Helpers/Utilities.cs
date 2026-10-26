using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Newtonsoft.Json;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using System.Linq;
using Telegram.Bot.Types.Payments;

#pragma warning disable CS0168
namespace Enforcer5.Helpers
{
    internal static class Bot
    {
        internal static string TelegramAPIKey;
        /// <summary>
        /// The Telegram client. An interface so the group-chat harness in the tests can stand in for
        /// Telegram; every call is an extension method over ITelegramBotClient.SendRequest anyway.
        /// </summary>
        public static ITelegramBotClient Api;

        /// <summary>Pacing and 429 state for <see cref="Api"/>; see <see cref="TelegramGateway"/>.</summary>
        internal static FloodControl Flood = new FloodControl();

        /// <summary>
        /// Points <see cref="Api"/> at <paramref name="client"/>, behind the gateway that keeps the bot
        /// under Telegram's limits, with fresh flood state. The test harness calls it with its fake.
        /// </summary>
        internal static void UseClient(ITelegramBotClient client)
        {
            var flood = new FloodControl();
            flood.Flooded += OnFlooded;
            Flood = flood;
            Api = new TelegramGateway(client, flood);
        }

        // Every 429 Telegram sends us. The log has each one; the error chat gets them summed up,
        // since ErrorReporter files all 429s under one key.
        private static void OnFlooded(Lane lane, string method, string chat, int seconds)
        {
            LogHelper.Info($"429 on {method} ({lane}) in {(chat.Length == 0 ? "the whole bot" : chat)}: paused {seconds}s");
            FloodSignal.Publish(lane, chat, Clock.UtcNow.AddSeconds(seconds));
        }
        /// <summary>Downloads files users send to the bot. Set up next to <see cref="Api"/>.</summary>
        internal static ITelegramFileDownloader Files;
        private static readonly HttpClient FileHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        public static User Me;
        public static DateTime StartTime = DateTime.UtcNow;
        public static bool Running = true;
        public static long CommandsReceived = 0;
        public static long MessagesProcessed = 0;
        public static long MessagesReceived = 0;
        public static long TotalPlayers = 0;
        public static long TotalGames = 0;
        public static Random R = new Random();
        public static int MessagesSent = 0;
        public static bool testing = false;
        public static string CurrentStatus = "";
        internal static string RootDirectory
        {
            get
            {
                var path = AppContext.BaseDirectory;;
                return Path.GetDirectoryName(path);
            }
        }
        internal delegate void ChatCommandMethod(Update u, string[] args);
        internal delegate void ChatCallbackMethod(CallbackQuery u, string[] args);
        internal delegate List<InlineQueryResultArticle> InlineQuery(User user, string[] args, XDocument lang);
        internal static HashSet<Models.Commands> Commands = new HashSet<Models.Commands>();
        internal static HashSet<Models.CallBacks> CallBacks = new HashSet<Models.CallBacks>();
        internal static HashSet<Models.Queries> Queries = new HashSet<Models.Queries>();
        internal static string LanguageDirectory => RegHelper.GetPath("LanguagesPath", "Languages");
        internal static string TempLanguageDirectory => RegHelper.GetPath("TempLanguageFilesPath", "TempLanguageFiles");
        internal static string LogDirectory => RegHelper.GetPath("LogPath", "Logs");

        internal static void EnsureLanguageDirectory()
        {
            var bundledDirectory = Path.Combine(AppContext.BaseDirectory, "Languages");
            var languageDirectory = LanguageDirectory;
            Directory.CreateDirectory(languageDirectory);

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var usesBundledDirectory = string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundledDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(languageDirectory)),
                comparison);

            if (!usesBundledDirectory && Directory.Exists(bundledDirectory))
            {
                foreach (var sourceFile in Directory.GetFiles(bundledDirectory, "*.xml"))
                {
                    var destinationFile = Path.Combine(languageDirectory, Path.GetFileName(sourceFile));
                    if (File.Exists(destinationFile)) continue;

                    var tempFile = Path.Combine(
                        languageDirectory,
                        $".{Path.GetFileName(sourceFile)}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.Copy(sourceFile, tempFile);
                        try
                        {
                            File.Move(tempFile, destinationFile);
                        }
                        catch (IOException) when (File.Exists(destinationFile))
                        {
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempFile)) File.Delete(tempFile);
                    }
                }
            }

            var englishFile = Path.Combine(languageDirectory, "English.xml");
            if (!File.Exists(englishFile))
                throw new FileNotFoundException($"Required language file not found: {englishFile}", englishFile);
        }

        internal static void MonitorLanguageDirectory()
        {
            // Seed from the current contents. Starting at null made the first tick always compare
            // unequal, so every start re-parsed all the XML five seconds in - right into the
            // restart burst.
            string state = null;
            try { state = GetLanguageDirectoryState(); } catch (Exception e) { ErrorReporter.Report(e, "Reading the language directory"); }

            while (true)
            {
                Thread.Sleep(5000);
                try
                {
                    var currentState = GetLanguageDirectoryState();
                    if (currentState == state) continue;
                    Methods.IntialiseLanguages();
                    state = currentState;
                    LogHelper.Info("Languages reloaded from the shared directory");
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Failed to reload languages");
                }
            }
        }

        private static string GetLanguageDirectoryState()
        {
            return string.Join("\n", Directory.GetFiles(LanguageDirectory, "*.xml")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                {
                    var file = new FileInfo(path);
                    return $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
                }));
        }

        /// <summary>Chat that startup notices and unhandled errors are reported to.</summary>
        internal static long ErrorChatId => RegHelper.GetLong("ErrorChatId") ?? Constants.Devs[0];

        /// <summary>Redis key holding the last processed update id. Separate per edition.</summary>
#if PREMIUM
        private const string OffsetKey = "bot:last_Premium_update";
#else
        private const string OffsetKey = "bot:last_update";
#endif

        private static readonly CancellationTokenSource _receiverCts = new CancellationTokenSource();
        internal static CancellationToken ShutdownToken => _receiverCts.Token;

        /// <summary>
        /// Stops polling, then lets the queued updates finish before the caller exits. Cancelling
        /// the consumers outright would discard whatever is still queued, and the offset has
        /// already advanced past it.
        /// </summary>
        internal static void StopReceiving()
        {
            _receiverCts.Cancel();
            _updates.Writer.TryComplete();
            try
            {
                Task.WhenAll(_consumers).Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Drain on shutdown failed");
            }
            // What was observed since the last minute's flush; FlushAsync never throws.
            PrincipalObserver.FlushAsync().Wait(TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Updates are handed to a bounded queue and drained by a fixed set of async consumers.
        /// Previously every update spawned unbounded thread-pool work, so a restart burst starved
        /// the pool instead of degrading. A full queue now blocks the poll loop, which is the
        /// back-pressure we want.
        /// </summary>
        private const int UpdateConsumers = 32;

        // Deliberately modest. The offset advances when an update is received, so anything still
        // queued at a hard kill is lost - a smaller queue bounds that window while still smoothing
        // a restart burst.
        private const int UpdateQueueDepth = 200;

        // Carries when we received the update, so staleness checks in the handlers measure
        // Telegram-to-us latency rather than however long it then sat in this queue.
        private static readonly Channel<(Update Update, DateTime ReceivedAt)> _updates =
            Channel.CreateBounded<(Update, DateTime)>(
            new BoundedChannelOptions(UpdateQueueDepth)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

        // Assigned once, before polling starts, so StopReceiving cannot enumerate it mid-build.
        private static Task[] _consumers = Array.Empty<Task>();

        private static void StartUpdateConsumers()
        {
            var started = new Task[UpdateConsumers];
            for (var i = 0; i < UpdateConsumers; i++)
                started[i] = Task.Run(ConsumeUpdatesAsync);
            _consumers = started;
        }

        /// <summary>
        /// Drains the update queue. The loop is supervised: a consumer that dies and is not
        /// replaced silently reduces throughput, and losing all of them would leave the process
        /// alive but deaf, which is worse than crashing. Nothing in here is allowed to escape.
        /// </summary>
        private static async Task ConsumeUpdatesAsync()
        {
            while (true)
            {
                try
                {
                    // No token: once the writer is completed this returns false and we exit
                    // cleanly, having drained whatever was still queued.
                    while (await _updates.Reader.WaitToReadAsync())
                    {
                        while (_updates.Reader.TryRead(out var queued))
                        {
                            // Flows into everything this update dispatches, so any error report
                            // can say which chat and command it came from.
                            ErrorReporter.Current = ErrorContext.From(queued.Update);
                            try
                            {
                                await UpdateHandler.RouteAsync(queued.Update, queued.ReceivedAt);
                            }
                            catch (Exception e)
                            {
                                ErrorReporter.Report(e, $"Update {queued.Update?.Id}");
                            }
                        }
                    }
                    return; // channel completed and empty
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Update consumer faulted, restarting");
                    // Guard against a fault that reproduces immediately turning this into a spin.
                    try { await Task.Delay(1000); } catch { }
                }
            }
        }

        // Blocking, Telegram-facing work is bounded separately from update routing. If the
        // consumers awaited it, a rate-limited chat could park all of them, fill the queue, and
        // stop the poll loop for every chat at once.
        //
        // Two gates, deliberately. Enforcement can hold a permit for a minute (Bot.Send sleeps out
        // a 429; KickUser makes many blocking calls), so it must not share with work a user is
        // waiting on: Telegram discards an unanswered inline query after about ten seconds, and a
        // callback leaves the button spinning. Sharing one gate would let a single raided group
        // stall every command, button and inline query bot-wide.
        private static readonly SemaphoreSlim _actionGate = new SemaphoreSlim(32);
        private static readonly SemaphoreSlim _interactiveGate = new SemaphoreSlim(64);

        /// <summary>
        /// Runs blocking enforcement on the pool without holding up an update consumer, capped so
        /// a raid cannot spawn unbounded concurrent kicks. Fire-and-forget; never throws.
        /// </summary>
        internal static void DispatchAction(Action work, [CallerMemberName] string name = "")
        {
            Run(_actionGate, work, name);
        }

        /// <summary>
        /// As DispatchAction, but for work a user is actively waiting on - commands, callbacks and
        /// inline queries. Separate gate so an enforcement backlog cannot starve it.
        /// </summary>
        internal static void DispatchInteractive(Action work, [CallerMemberName] string name = "")
        {
            Run(_interactiveGate, work, name);
        }

        // Work started by Run, Dispatch and StartService that has not finished yet. Lets the test
        // harness wait until everything an update set off is done; costs one interlocked add per
        // dispatch.
        private static int _inFlight;

        /// <summary>
        /// One piece of dispatched work, for the harness's bookkeeping. Work that ends while a wait
        /// it started is still pending (a Telegram call it did not await, waiting for its slot) stays
        /// counted until that wait is over, so the harness never takes the waiting call for nothing.
        /// </summary>
        internal sealed class WorkUnit
        {
            private int _outstanding;
            private bool _ended;

            internal void WaitStarted()
            {
                lock (this)
                {
                    if (_ended && _outstanding == 0) Interlocked.Increment(ref _inFlight);
                    _outstanding++;
                }
            }

            internal void WaitEnded()
            {
                lock (this)
                {
                    _outstanding--;
                    if (_ended && _outstanding == 0) Interlocked.Decrement(ref _inFlight);
                }
            }

            internal void End()
            {
                lock (this)
                {
                    _ended = true;
                    if (_outstanding == 0) Interlocked.Decrement(ref _inFlight);
                }
            }
        }

        // Set inside dispatched work, so Clock can count it as parked while it waits on the clock.
        private static readonly AsyncLocal<WorkUnit> _unit = new AsyncLocal<WorkUnit>();

        /// <summary>True inside work started by Run, Dispatch or StartService.</summary>
        internal static bool InDispatch => _unit.Value != null;

        /// <summary>The dispatched work this flow belongs to, or null.</summary>
        internal static WorkUnit CurrentWork => _unit.Value;

        // Background loops started by StartService; they never finish while the bot runs.
        private static int _services;

        /// <summary>Dispatched work that has not finished, background loops left out. For the harness.</summary>
        internal static int PendingWork => Volatile.Read(ref _inFlight) - Volatile.Read(ref _services);

        /// <summary>Dispatched work started and not finished, background loops included. For the harness.</summary>
        internal static int InFlight => Volatile.Read(ref _inFlight);

        /// <summary>
        /// Waits until no dispatched work is running: everything still in flight is waiting on the
        /// clock (<see cref="Clock.Parked"/>). For the test harness, which then moves its clock on.
        /// </summary>
        internal static async Task WaitIdleAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            // Idle several times in a row: work that just finished may be about to dispatch more,
            // and a continuation may be queued but not running yet.
            var idleTicks = 0;
            while (idleTicks < 3)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        $"{Volatile.Read(ref _inFlight)} dispatched tasks still running, {Clock.Parked} of them waiting on the clock");
                idleTicks = Volatile.Read(ref _inFlight) <= Clock.Parked ? idleTicks + 1 : 0;
                await Task.Delay(idleTicks == 0 ? 5 : 2);
            }
        }

        /// <summary>
        /// Starts a long-running background loop (the enforcement queue's worker, for one). It
        /// counts as dispatched work for its whole life, so the harness sees it as busy unless it is
        /// waiting on <see cref="Clock"/>. Never throws; a loop that faults is reported and restarted.
        /// </summary>
        internal static void StartService(Func<CancellationToken, Task> loop, CancellationToken stop, [CallerMemberName] string name = "")
        {
            Interlocked.Increment(ref _inFlight);
            Interlocked.Increment(ref _services);
            var unit = new WorkUnit();
            _ = Task.Run(async () =>
            {
                _unit.Value = unit;
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            await loop(stop);
                            return;
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception e)
                        {
                            ErrorReporter.Report(e, "Background loop faulted, restarting", name, "");
                            await Clock.WaitAsync(TimeSpan.FromSeconds(1), stop);
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _services);
                    unit.End();
                }
            });
        }

        private static void Run(SemaphoreSlim gate, Action work, string name)
        {
            Interlocked.Increment(ref _inFlight);
            var unit = new WorkUnit();
            _ = Task.Run(async () =>
            {
                _unit.Value = unit;
                try
                {
                    try
                    {
                        // Waiting for a permit is waiting on whoever holds it: when they are all
                        // waiting on the clock, so is this.
                        Clock.Park();
                        try { await gate.WaitAsync(); }
                        finally { Clock.Unpark(); }
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "could not be queued", name, "");
                        return;
                    }
                    try
                    {
                        work();
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, null, name, "");
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                finally
                {
                    unit.End();
                }
            });
        }

        /// <summary>
        /// Fire-and-forget background work that can never take the process down. Replaces the
        /// `new Task(...).Start()` idiom, which scheduled on TaskScheduler.Current rather than the
        /// pool and left exceptions to escape through async void.
        ///
        /// Do not add an Action overload: a lambda whose body returns Task binds to either, and
        /// picking Action would silently drop the task - the async void trap again. Wrap sync work
        /// as `() => { work(); return Task.CompletedTask; }` at the call site instead.
        /// </summary>
        internal static void Dispatch(Func<Task> work, [CallerMemberName] string name = "")
        {
            Interlocked.Increment(ref _inFlight);
            var unit = new WorkUnit();
            _ = Task.Run(async () =>
            {
                _unit.Value = unit;
                try
                {
                    await work();
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, null, name, "");
                }
                finally
                {
                    unit.End();
                }
            });
        }


        /// <summary>
        /// Binds the [Command], [Callback] and [Query] methods by reflection. Idempotent, so the
        /// test harness can call it without Initialize.
        /// </summary>
        internal static void BindHandlers()
        {
            Commands.Clear();
            CallBacks.Clear();
            Queries.Clear();
            //load the commands list
            foreach (var m in typeof(Commands).GetMethods())
            {
                var c = new Models.Commands();
                foreach (var a in m.GetCustomAttributes(true))
                {
                    if (a is Attributes.Command)
                    {
                        var ca = a as Attributes.Command;
                        c.Blockable = ca.Blockable;
                        c.DevOnly = ca.DevOnly;
                        c.GlobalAdminOnly = ca.GlobalAdminOnly;
                        c.GroupAdminOnly = ca.GroupAdminOnly;
                        c.Trigger = ca.Trigger;
                        c.Method = (ChatCommandMethod)m.CreateDelegate(typeof(ChatCommandMethod));
                        c.InGroupOnly = ca.InGroupOnly;
                        c.RequiresReply = ca.RequiresReply;
                        c.UploadAdmin = ca.UploadAdmin;
                        Commands.Add(c);
                    }
                }
            }
            //loadCallbackQuries
            foreach (var m in typeof(CallBacks).GetMethods())
            {
                var c = new Models.CallBacks();
                foreach (var a in m.GetCustomAttributes(true))
                {
                    if (a is Attributes.Callback)
                    {
                        var ca = a as Attributes.Callback;
                        c.Blockable = ca.Blockable;
                        c.DevOnly = ca.DevOnly;
                        c.GlobalAdminOnly = ca.GlobalAdminOnly;
                        c.GroupAdminOnly = ca.GroupAdminOnly;
                        c.Trigger = ca.Trigger;
                        c.Method = (ChatCallbackMethod)m.CreateDelegate(typeof(ChatCallbackMethod));
                        c.InGroupOnly = ca.InGroupOnly;
                        c.RequiresReply = ca.RequiresReply;
                        c.UploadAdmin = ca.UploadAdmin;
                        CallBacks.Add(c);
                    }
                }
            }

            foreach (var m in typeof(Queries).GetMethods())
            {
                var c = new Models.Queries();
                foreach (var a in m.GetCustomAttributes(true))
                {
                    if (a is Attributes.Query)
                    {
                        var ca = a as Attributes.Query;
                        c.Trigger = ca.Trigger;
                        c.DefaultResponse = ca.DefaultResponse;
                        c.Description = ca.Description;
                        c.Title = ca.Title;
                        c.Method = (InlineQuery) m.CreateDelegate(typeof(InlineQuery));
                        Queries.Add(c);
                    }
                }
            }
        }

        public static async Task Initialize(string updateid = null)
        {
#if PREMIUM
            TelegramAPIKey = RegHelper.GetRequired("EnforcerPremiumAPI");
#else
            TelegramAPIKey = RegHelper.GetRequired("EnforcerAPI");
#endif
            var serverUrl = RegHelper.GetRegValue("TelegramServerUrl");
            // RetryCount stays 0: 429s are handled by the gateway, per chat and lane, not by the
            // client sleeping inside a request.
            var client = string.IsNullOrEmpty(serverUrl)
                ? new TelegramBotClient(TelegramAPIKey)
                : new TelegramBotClient(new TelegramBotClientOptions(TelegramAPIKey, serverUrl));
            UseClient(client);
            Files = new TelegramFileDownloader(client, FileHttp,
                TelegramFileDownloader.ResolveFileBaseUrl(RegHelper.GetRegValue("TelegramFileUrl"), serverUrl),
                TelegramAPIKey);

            Me = await Api.GetMe();
            try { Console.Title = $"Enforcer {Me.Username}"; } catch (PlatformNotSupportedException) { }
            LogHelper.Info($"Connected as @{Me.Username}");
            StartTime = DateTime.UtcNow;

            // Resume from the offset we persisted, so a restart neither reprocesses nor skips updates.
            long offset;
            var storedOffset = Redis.db.StringGetAsync(OffsetKey).Result;
            if (!storedOffset.HasValue || !long.TryParse(storedOffset.ToString(), out offset))
                offset = 0;

            LogHelper.Info($"Database offset is {offset}");
            Send($"Bot Started:\n{Methods.DisplayNow():hh:mm:ss dd-MM-yyyy}", ErrorChatId);
            BindHandlers();
            // Now we can start receiving. ReceiveAsync runs the long-polling loop until cancelled;
            // it restarts internally on transient errors, so no watchdog timer is needed.
            LogHelper.Info($"Starting ID = {offset + 1}");
            var receiverOptions = new ReceiverOptions
            {
                Offset = (int)(offset + 1),
                AllowedUpdates = new[]
                {
                    UpdateType.Message,
                    UpdateType.CallbackQuery,
                    UpdateType.InlineQuery,
                    UpdateType.PreCheckoutQuery,
                    UpdateType.ShippingQuery
                }
            };

            StartUpdateConsumers();
            await Api.ReceiveAsync(new EnforcerUpdateHandler(), receiverOptions, ShutdownToken);
        }

        /// <summary>
        /// Queues polled updates for the consumer pool. Replaces the 13.x
        /// OnUpdate/OnInlineQuery/OnCallbackQuery event model.
        /// </summary>
        private sealed class EnforcerUpdateHandler : IUpdateHandler
        {
            public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
            {
                // Persist the offset here, on the receive thread, so it advances in order. Writing it
                // from the consumers would let a later id land before an earlier update finished and
                // a restart would then skip work.
                Redis.db.StringSetAsync(OffsetKey, update.Id);

                // Blocks the poll loop when the queue is full - that back-pressure is the point.
                // No token: an OperationCanceledException here escapes ReceiveAsync and Initialize,
                // reaching the unhandled handler as a terminating fault, so a planned shutdown
                // would exit(5) and look like a crash to DeployBot. Writer completion ends this.
                try
                {
                    // UTC, like Message.Date: the flood and length checks measure the difference.
                    // DateTime.Now was local time, so on a server not on UTC every message looked
                    // hours old and those checks never ran.
                    await _updates.Writer.WriteAsync((update, Clock.UtcNow), CancellationToken.None);
                }
                catch (ChannelClosedException)
                {
                    // shutting down
                }
            }

            public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source,
                CancellationToken cancellationToken)
            {
                ErrorReporter.Report(exception, $"Polling [{source}]");

                // A failing poll must not turn into a tight loop.
                if (source == HandleErrorSource.PollingError)
                    return Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return Task.CompletedTask;
            }
        }

        internal static void ReplyToCallback(CallbackQuery query, string text = null, bool edit = true, bool showAlert = false, InlineKeyboardMarkup replyMarkup = null)
        {
            //first answer the callback
            Bot.Api.AnswerCallbackQuery(query.Id, edit ? null : text, showAlert);
            //edit the original message
            if (edit)
                Edit(query, text, replyMarkup);
        }

        internal static Task<Message> Edit(CallbackQuery query, string text, InlineKeyboardMarkup replyMarkup = null)
        {
            return Edit(query.Message.Chat.Id, query.Message.MessageId, text, replyMarkup);
        }

        internal static Task<Message> Edit(long id, int msgId, string text, InlineKeyboardMarkup replyMarkup = null)
        {
            Interlocked.Increment(ref MessagesSent);
            return Bot.Api.EditMessageText(id, msgId, text, replyMarkup: replyMarkup);
        }

        /// <summary>
        /// Unwraps the ApiRequestException from a blocking call's AggregateException, if there is one.
        /// Every send path here is synchronous (.Result), so Telegram errors arrive wrapped.
        /// </summary>
        internal static ApiRequestException AsApiError(Exception e)
        {
            if (e is ApiRequestException direct) return direct;
            if (e is AggregateException agg)
                return agg.InnerExceptions.OfType<ApiRequestException>().FirstOrDefault();
            return e?.InnerException as ApiRequestException;
        }

        /// <summary>True when Telegram rejected the send because the bot cannot reach that user or chat.</summary>
        private static bool IsUnreachable(ApiRequestException api)
        {
            if (api == null) return false;
            // 403: bot blocked, kicked, or user deactivated. 400 + these descriptions: chat gone.
            if (api.ErrorCode == 403) return true;
            return api.ErrorCode == 400 &&
                   (api.Message.Contains("chat not found") || api.Message.Contains("user is deactivated"));
        }

        /// <summary>True for a 429, Telegram's or one the gateway answered itself.</summary>
        internal static bool IsRateLimited(ApiRequestException api) => api?.ErrorCode == 429;

        /// <summary>True when the message body failed Telegram's HTML/Markdown parser.</summary>
        private static bool IsParseFailure(ApiRequestException api)
        {
            return api != null && api.ErrorCode == 400 && api.Message.Contains("can't parse entities");
        }

        private static Message CatchSend(string message, long id, InlineKeyboardMarkup customMenu = null,
            ParseMode parsemode = ParseMode.None, int messageId = -1)
        {
            Message result = null;
            try
            {
                Interlocked.Increment(ref MessagesSent);
                result = Bot.Api.SendMessage(id, message, parsemode,
                    replyParameters: messageId == -1 ? null : new ReplyParameters { MessageId = messageId },
                    replyMarkup: customMenu).Result;
            }
            catch (Exception e) when (IsRateLimited(AsApiError(e)))
            {
                // Rate limited: logged by the gateway, nothing more to do for a fallback send.
            }
            catch (Exception e)
            {
                // ErrorReporter talks to the API directly, so this cannot recurse.
                ErrorReporter.Report(e, $"CatchSend to {id} failed");
            }
            return result;
        }

        internal static Message SendToPm(string message, long pmId, long chatId, InlineKeyboardMarkup menu = null,
            ParseMode parseMode = ParseMode.Html, int messageId = -1)
        {
            Message result = null;
            try
            {
                result = Send(message, pmId, menu, parseMode);
            }
            catch (Exception e) when (IsUnreachable(AsApiError(e)))
            {
                // The user has not started the bot (or has blocked it) - point them at it in the group.
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var startMe = new Menu(1)
                {
                    Buttons = new List<InlineButton>
                    {
                        new InlineButton(Methods.GetLocaleString(lang, "StartMe"), url:$"https://t.me/{Bot.Me.Username}")
                    }
                };
                if (messageId == -1)
                    Bot.Send(Methods.GetLocaleString(lang, "botNotStarted"), chatId, Key.CreateMarkupFromMenu(startMe));
                else
                    Bot.SendReply(Methods.GetLocaleString(lang, "botNotStarted"), chatId, messageId, Key.CreateMarkupFromMenu(startMe));
                result = null;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"SendToPm to {pmId} failed, sending in the group instead");
                result = CatchSend(message, chatId, menu, parseMode, messageId);
            }
            return result;
        }

        internal static Message SendToPm(string message, Update update, InlineKeyboardMarkup menu = null,
            ParseMode parseMode = ParseMode.Html)
        {
            return SendToPm(message, update.Message.From.Id, update.Message.Chat.Id, menu, parseMode,
                update.Message.MessageId);
        }

        internal static Message Send(string message, long id,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html, [CallerMemberName]string parentMethod = "")
        {
            Message result = null;
            try
            {
                Interlocked.Increment(ref MessagesSent);
                result = Api.SendMessage(id, message, parseMode,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: customMenu).Result;
            }
            catch (Exception e)
            {
                var api = AsApiError(e);

                if (IsRateLimited(api))
                {
                    // The gateway already waited out a short 429 and tried once more. Never sleep
                    // here: that held an enforcement permit for up to a minute.
                    Announcements.Defer(id, message, parseMode, customMenu);
                    result = null;
                }
                else if (IsParseFailure(api))
                {
                    // Send it unformatted rather than losing the message entirely.
                    result = CatchSend(message, id, customMenu, ParseMode.None);
                }
                else if (api != null && api.Message.Contains("message is too long"))
                {
                    result = SendInChunks(message, id);
                }
                else if (IsUnreachable(api))
                {
                    // SendToPm needs to know so it can prompt the user to start the bot.
                    if (parentMethod.Equals("SendToPm")) throw;
                    result = null;
                }
                else if (api != null && api.Message.Contains("bot can't send messages to bots"))
                {
                    result = null;
                }
                else
                {
                    ErrorReporter.Report(e, $"Send to {id} failed");
                    result = null;
                }
            }
            return result;
        }

        /// <summary>
        /// Splits an over-long message on line boundaries and sends each part.
        /// Returns the last message sent, or null if nothing went out.
        /// </summary>
        private static Message SendInChunks(string message, long id, int replyToMessageId = -1)
        {
            const int MaxLength = 4000; // Telegram's limit is 4096; leave room for formatting.
            Message last = null;
            var lines = message.Split('\n');
            var chunk = new StringBuilder();

            foreach (var line in lines)
            {
                if (chunk.Length + line.Length + 1 > MaxLength && chunk.Length > 0)
                {
                    last = CatchSend(chunk.ToString(), id, messageId: replyToMessageId);
                    chunk.Clear();
                }
                chunk.Append(line).Append('\n');
            }
            if (chunk.Length > 0)
                last = CatchSend(chunk.ToString(), id, messageId: replyToMessageId);

            return last;
        }
        internal static Message Send(string message, Update chatUpdate,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html, [CallerMemberName]string parentMethod = "")
        {
            var id = chatUpdate.Message.Chat.Id;
            return Send(message, id, customMenu, parseMode, parentMethod);
        }

        internal static Message SendReply(string message, Message msg, [CallerMemberName]string parentMethod = "")
        {
            return SendReply(message, msg.Chat.Id, msg.MessageId, parentMethod: parentMethod);
        }
        internal static Message SendReply(string message, long chatid, int msgid, InlineKeyboardMarkup keyboard = null, [CallerMemberName]string parentMethod = "")
        {
            Interlocked.Increment(ref MessagesSent);
            Message result = null;
            try
            {
                result = Api.SendMessage(chatid, message, ParseMode.Html,
                    replyParameters: new ReplyParameters { MessageId = msgid },
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: keyboard).Result;
            }
            catch (Exception e)
            {
                var api = AsApiError(e);

                if (IsRateLimited(api))
                {
                    Announcements.Defer(chatid, message, ParseMode.Html, keyboard);
                    result = null;
                }
                else if (IsParseFailure(api))
                {
                    result = CatchSend(message, chatid, keyboard, ParseMode.None, msgid);
                }
                else if (api != null && api.Message.Contains("reply message not found"))
                {
                    // The message being replied to was deleted - send it standalone.
                    result = Send(message, chatid);
                }
                else if (api != null && api.Message.Contains("message is too long"))
                {
                    result = SendInChunks(message, chatid, msgid);
                }
                else if (IsUnreachable(api))
                {
                    if (parentMethod.Equals("SendToPm")) throw;
                    result = null;
                }
                else
                {
                    ErrorReporter.Report(e, $"SendReply to {chatid} failed");
                    result = null;
                }
            }
            return result;
        }
        internal static Message SendReply(string message, Update msg,[CallerMemberName]string parentMethod = "")
        {            
            return SendReply(message, msg.Message.Chat.Id, msg.Message.MessageId, parentMethod:parentMethod);
        }
        internal static Message SendReply(string message, Update msg, InlineKeyboardMarkup keyboard, [CallerMemberName]string parentMethod = "")
        {
            return SendReply(message, msg.Message.Chat.Id, msg.Message.MessageId, keyboard, parentMethod);
        }

        // ---------------------------------------------------------------------------------------
        // Async twins of the send helpers, for the update hot path. The sync versions above stay
        // for the command handlers, which are offloaded to the pool anyway. Calling a blocking
        // Send from an async handler would park a consumer for the whole Telegram round-trip and
        // undo the point of the conversion.
        // ---------------------------------------------------------------------------------------

        private static async Task<Message> CatchSendAsync(string message, long id, InlineKeyboardMarkup customMenu = null,
            ParseMode parsemode = ParseMode.None, int messageId = -1)
        {
            try
            {
                Interlocked.Increment(ref MessagesSent);
                return await Api.SendMessage(id, message, parsemode,
                    replyParameters: messageId == -1 ? null : new ReplyParameters { MessageId = messageId },
                    replyMarkup: customMenu);
            }
            catch (Exception e) when (IsRateLimited(AsApiError(e)))
            {
                // Rate limited: logged by the gateway, nothing more to do for a fallback send.
                return null;
            }
            catch (Exception e)
            {
                // ErrorReporter talks to the API directly, so this cannot recurse.
                ErrorReporter.Report(e, $"CatchSendAsync to {id} failed");
                return null;
            }
        }

        internal static async Task<Message> SendAsync(string message, long id,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html)
        {
            try
            {
                Interlocked.Increment(ref MessagesSent);
                return await Api.SendMessage(id, message, parseMode,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: customMenu);
            }
            catch (Exception e)
            {
                var api = AsApiError(e);

                if (IsRateLimited(api))
                {
                    Announcements.Defer(id, message, parseMode, customMenu);
                    return null;
                }
                if (IsParseFailure(api))
                {
                    // Send it unformatted rather than losing the message entirely.
                    return await CatchSendAsync(message, id, customMenu, ParseMode.None);
                }
                if (IsUnreachable(api) || (api != null && api.Message.Contains("bot can't send messages to bots")))
                {
                    return null;
                }

                ErrorReporter.Report(e, $"SendAsync to {id} failed");
                return null;
            }
        }

        internal static Task<Message> SendAsync(string message, Update chatUpdate,
            InlineKeyboardMarkup customMenu = null, ParseMode parseMode = ParseMode.Html)
        {
            return SendAsync(message, chatUpdate.Message.Chat.Id, customMenu, parseMode);
        }


        /// <summary>
        /// Deletes a message through the enforcement queue. True once it is gone or certain to go;
        /// a message already gone counts as deleted. Throws when the bot may not delete it, as it
        /// always did.
        /// </summary>
        public static Boolean DeleteMessage(long chatId, int msgid)
        {
            var result = Enforcement.EnforcementQueue.Run(Enforcement.EnforcementJob.Delete(chatId, msgid));
            if (result.Succeeded) return true;
            throw result.Error ?? new InvalidOperationException($"Deleting {msgid} in {chatId}: {result.Refusal}");
        }

        public static void DeleteLastWelcomeMessage(long ChatId, int msgid)
        {
            var enabled = Redis.db.HashGetAsync($"chat:{ChatId}:settings", "DeleteLastWelcome").Result;
            int lastWelcomeMessageId = (int)Redis.db.StringGetAsync($"chat:{ChatId}:lastwelcome").Result;
            if (enabled.Equals("no"))
            {
                
                Redis.db.StringSetAsync($"chat:{ChatId}:lastwelcome", $"{msgid}");
                DeleteMessage(ChatId, lastWelcomeMessageId);
            }  
        }

        public static void SendInvoice(long userId, string title, string description, string callbackKey, LabeledPrice[] labeledPrices)
        {
            try
            {
                Api.SendInvoice(userId, title, description,
                    payload: Guid.NewGuid().ToString(),
                    currency: Constants.paymentCurrency,
                    prices: labeledPrices,
                    providerToken: Constants.PaymentProviderToken,
                    needEmail: true).Wait();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Failed to send invoice to {userId}");
                Bot.CatchSend("Sorry, something went wrong creating your invoice. Please try again.", userId);
            }
        }

    }

    internal static class Redis
    {
        private static ConnectionMultiplexer _redis;
        private static IDatabase _db;

        /// <summary>
        /// Connection is established lazily by Start(). It must not happen in a static field
        /// initializer: a failure there surfaces as a TypeInitializationException from whichever
        /// unrelated line first touches this class, with nothing logged.
        /// </summary>
        public static IDatabase db
        {
            get
            {
                if (_db == null)
                    throw new InvalidOperationException("Redis.Start() must succeed before the database is used.");
                return _db;
            }
        }

        /// <summary>Points Redis.db at a stand-in, for the test harness. Never called by the bot.</summary>
        internal static void UseForTests(IDatabase database) => _db = database;

        public static void SaveRedis()
        {
            var endpoint = _redis.GetEndPoints().FirstOrDefault();
            if (endpoint == null) return;
            _redis.GetServer(endpoint).Save(SaveType.BackgroundSave);
        }

        public static bool Start()
        {
            try
            {
                if (_redis == null || !_redis.IsConnected)
                {
                    var connectionString = RegHelper.GetRequired("RedisConnection");
                    var options = ConfigurationOptions.Parse(connectionString);
                    var password = RegHelper.GetRegValue("RedisPassword");
                    if (!string.IsNullOrEmpty(password))
                        options.Password = password;
                    options.AllowAdmin = true;          // required for the BGSAVE in SaveRedis()
                    options.AbortOnConnectFail = false; // survive Redis restarting under us

                    // Supply timeouts only where the connection string is silent, so operator tuning
                    // in the systemd unit still wins. The 5s SE.Redis default is too tight for a
                    // shared instance under a restart burst.
                    var declared = new HashSet<string>(
                        connectionString.Split(',')
                            .Select(part => part.Split('=')[0].Trim())
                            .Where(name => name.Length > 0),
                        StringComparer.OrdinalIgnoreCase);

                    if (!declared.Contains("connectTimeout")) options.ConnectTimeout = 15000;
                    if (!declared.Contains("syncTimeout")) options.SyncTimeout = 15000;
                    if (!declared.Contains("asyncTimeout")) options.AsyncTimeout = 15000;
                    if (!declared.Contains("connectRetry")) options.ConnectRetry = 3;
                    if (!declared.Contains("keepAlive")) options.KeepAlive = 60;
                    options.ReconnectRetryPolicy = new ExponentialRetry(1000);

                    // The instance is shared with blackwolf; without this CLIENT LIST cannot tell
                    // the consumers apart.
                    if (!declared.Contains("name"))
#if PREMIUM
                        options.ClientName = "enforcer-premium";
#else
                        options.ClientName = "enforcer-normal";
#endif

                    _redis = ConnectionMultiplexer.Connect(options);
                    _db = _redis.GetDatabase(Constants.EnforcerDb);
                }

                return _db.StringSet("testWrite", "trying");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Redis connection failed");
                return false;
            }
        }
    }
}
