using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Enforcer5.Helpers;
using Enforcer5;
using Enforcer5.Handlers;
using Enforcer5.Storage;
using Enforcer5.Storage.Operations;

namespace Enforcer5
{
    public class Program
    {
        private static readonly ManualResetEventSlim ShutdownRequested = new ManualResetEventSlim(false);
        private static int _shutdownStarted;
        private static PosixSignalRegistration _sigtermRegistration;
        internal static bool Running = true;
        internal static List<long> MessagesReceived = new List<long>();
        internal static List<long> MessagesProcessed = new List<long>();
        internal static List<long> MessagesSent = new List<long>();
        private static long _previousMessages, _previousMessagesTx, _previousMessagesRx;
        internal static float MessagePxPerSecond, MessageRxPerSecond, MessageTxPerSecond;
        internal static int NodeMessagesSent = 0;
        private static System.Threading.Timer _timer;
        private static System.Threading.Timer _tempbanJob;
        private static System.Threading.Timer _postgresTempbanJob;
        private static PostgresDatabase _operationsDatabase;
        private static PostgresTempBanDueWorker _postgresTempBanWorker;
        private static System.Threading.Timer _cutoverJob;
        private static Storage.Cutover.GroupCutoverCrawler _cutoverCrawler;
        internal static List<Language> LangaugeList = new List<Language>();
        public static DateTime MaxTime = DateTime.MinValue;
        public static void Main(string[] args)
        {
            if (MigrationCommand.IsMigrationCommand(args))
            {
                try
                {
                    Environment.ExitCode = MigrationCommand.RunAsync(args).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    // Full trace, not just the message. A migration that fails after an hour is not
                    // cheap to reproduce, so the one chance to capture where it failed matters.
                    Console.Error.WriteLine($"[FATAL] Migration command failed: {e}");
                    Environment.ExitCode = 1;
                }
                return;
            }

            // The update path issues many concurrent Redis and Telegram calls. The default minimum
            // (= processor count) leaves the pool injecting only ~1-2 threads/sec once saturated,
            // which is what turned a burst of updates into SE.Redis timeouts after the migration.
            //
            // This is coupled to the raised Sync/AsyncTimeout in Redis.Start(): the ~330 remaining
            // blocking calls each hold their thread for up to that timeout, so if this line ever
            // goes away the longer timeout makes starvation worse, not better.
            if (!ThreadPool.SetMinThreads(200, 200))
                Console.Error.WriteLine("[WARN] ThreadPool.SetMinThreads was rejected; expect Redis timeouts under load.");

            // A faulted background task must never take the process down. Exceptions from the ~280
            // unawaited Redis writes land here; log them instead of leaving them silent.
            TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
            {
                eventArgs.SetObserved();
                LogHelper.Error($"Unobserved task exception: {eventArgs.Exception?.Flatten().Message}");
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                try
                {
                    var e = eventArgs.ExceptionObject as Exception;
                    var msg = $"[FATAL] {DateTime.UtcNow:u}\n{e?.Message}\n{e?.StackTrace}\n\n";
                    Console.Error.WriteLine(msg);
                    LogHelper.AppendLog(Path.Combine(Bot.LogDirectory, "error.log"), msg);
                    if (eventArgs.IsTerminating)
                        Environment.Exit(5);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[FATAL] Unhandled exception handler failed: {e}");
                }
            };

            // Apply any staged update before touching Redis or Telegram.
            Updater.ApplyPendingUpdate();

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                RequestShutdown("SIGINT");
            };
            if (!OperatingSystem.IsWindows())
            {
                _sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    RequestShutdown("SIGTERM");
                });
            }

            try { Console.Title = "Enforcer"; } catch (PlatformNotSupportedException) { }
            LogHelper.Info($"Enforcer starting, BaseDirectory={AppContext.BaseDirectory}");
            Bot.EnsureLanguageDirectory();
            Methods.IntialiseLanguages();

            // On Linux this process is always named "dotnet", so the guard would fire against any
            // other .NET app on the host. systemd already enforces a single instance there.
            if (!RegHelper.IsLinux &&
                Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1)
            {
                Environment.Exit(2);
            }

            var redisReady = Redis.Start();
            int count = 0;
            while (!redisReady)
            {
                Thread.Sleep(2000);
                redisReady = Redis.Start();
                if (count > 5)
                {
                    Console.Error.WriteLine("[FATAL] Could not reach Redis, exiting.");
                    Environment.Exit(1);
                }
                count++;
            }
            MigrationMaintenanceGuard.EnsureRuntimeAllowedAsync().GetAwaiter().GetResult();
            if (!MigrationMaintenanceGuard.PublishHeartbeatAsync(OperationsStore.CurrentEdition)
                    .GetAwaiter().GetResult())
                throw new InvalidOperationException("Migration maintenance lock became active during startup.");

            new Thread(() => Bot.Initialize().GetAwaiter().GetResult()) { IsBackground = true }.Start();
            new Thread(Bot.MonitorLanguageDirectory) { IsBackground = true }.Start();
            new Thread(Updater.MonitorUpdates) { IsBackground = true }.Start();
            new Thread(UpdateHandler.SpamDetection) { IsBackground = true }.Start();

            _timer = new Timer(TimerOnTick, null, 5000, 1000);
            var wait = TimeSpan.FromSeconds(30);

            // Both expiry workers run. Choosing one at start-up was safe only while every group
            // lived in the same store; during the per-group migration the estate is mixed, and a
            // group that had cut over would have been left with its tempbans in Redis and no worker
            // acting on them. Each worker filters on per-group state, so together they cover the
            // whole estate exactly once.
            _tempbanJob = new System.Threading.Timer(Methods.CheckTempBans, null, wait, wait);

            if (!string.IsNullOrWhiteSpace(RegHelper.GetRegValue("PostgresConnection")))
            {
                _operationsDatabase = PostgresDatabase.FromConfiguration();
                _postgresTempBanWorker = new PostgresTempBanDueWorker(_operationsDatabase,
                    OperationsStore.CurrentEdition, $"{Environment.MachineName}-{OperationsStore.CurrentEdition}");
                _postgresTempbanJob = new System.Threading.Timer(_ => Bot.Dispatch(
                    () => _postgresTempBanWorker.ProcessDueAsync(100, DateTimeOffset.UtcNow, Bot.ShutdownToken),
                    "PostgresTempBanWorker"), null, wait, wait);

                // Stage C. Off unless deliberately enabled, so deploying this build changes nothing
                // and the crawler cannot start moving groups on its own.
                if (RegHelper.GetInt("GroupCutoverBudget", 0) > 0)
                {
                    _cutoverCrawler = new Storage.Cutover.GroupCutoverCrawler(_operationsDatabase,
                        $"{Environment.MachineName}-{OperationsStore.CurrentEdition}");
                    var crawlInterval = TimeSpan.FromSeconds(
                        Math.Clamp(RegHelper.GetInt("GroupCutoverIntervalSeconds", 60), 5, 3600));
                    _cutoverJob = new System.Threading.Timer(_ => Bot.Dispatch(
                        () => _cutoverCrawler.RunOnceAsync(RegHelper.GetInt("GroupCutoverBudget", 0),
                            Bot.ShutdownToken), "GroupCutoverCrawler"), null, crawlInterval, crawlInterval);
                }
            }
            ShutdownRequested.Wait();
            _timer?.Dispose();
            _tempbanJob?.Dispose();
            _postgresTempbanJob?.Dispose();
            _cutoverJob?.Dispose();
            _operationsDatabase?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            // _operationsDatabase is a shared instance, so its own dispose is a no-op. The pools
            // themselves are process-owned and released here.
            PostgresDatabase.DisposeSharedAsync().GetAwaiter().GetResult();
            _sigtermRegistration?.Dispose();
        }

        internal static void RequestShutdown(string reason)
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;

            LogHelper.Info($"Shutdown requested ({reason})");
            Running = false;
            Bot.Running = false;
            // A command can request shutdown from inside tracked dispatched work. Coordinate on a
            // separate task so the command can return instead of waiting for itself to drain.
            _ = Task.Run(() =>
            {
                Bot.StopReceiving();
                ShutdownRequested.Set();
            });
        }

        private static void TimerOnTick(Object stateInfo)
        {
            try
            {
                Bot.Dispatch(async () =>
                {
                    if (!await MigrationMaintenanceGuard.PublishHeartbeatAsync(OperationsStore.CurrentEdition))
                        RequestShutdown("migration maintenance lock detected");
                }, "RuntimeHeartbeat");
                var newMessages = Bot.MessagesProcessed - _previousMessages;
                _previousMessages = Bot.MessagesProcessed;
                MessagesProcessed.Insert(0, newMessages);
                if (MessagesProcessed.Count > 60)
                    MessagesProcessed.RemoveAt(60);
                MessagePxPerSecond = MessagesProcessed.Max();

                newMessages = (Bot.MessagesSent + NodeMessagesSent) - _previousMessagesTx;
                _previousMessagesTx = (Bot.MessagesSent + NodeMessagesSent);
                MessagesSent.Insert(0, newMessages);
                if (MessagesSent.Count > 60)
                    MessagesSent.RemoveAt(60);  
                MessageTxPerSecond = MessagesSent.Max();

                newMessages = Bot.MessagesReceived - _previousMessagesRx;
                _previousMessagesRx = Bot.MessagesReceived;
                MessagesReceived.Insert(0, newMessages);
                if (MessagesReceived.Count > 60)
                    MessagesReceived.RemoveAt(60);
                MessageRxPerSecond = MessagesProcessed.Max();
            }
            catch
            {
                // ignored
            }
        }
    }
}
