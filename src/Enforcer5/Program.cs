using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Enforcer5.Helpers;
using Enforcer5;
using Enforcer5.Handlers;

namespace Enforcer5
{
    public class Program
    {
        internal static bool Running = true;
        internal static List<long> MessagesReceived = new List<long>();
        internal static List<long> MessagesProcessed = new List<long>();
        internal static List<long> MessagesSent = new List<long>();
        private static long _previousMessages, _previousMessagesTx, _previousMessagesRx;
        internal static float MessagePxPerSecond, MessageRxPerSecond, MessageTxPerSecond;
        internal static int NodeMessagesSent = 0;
        private static System.Threading.Timer _timer;
        private static System.Threading.Timer _tempbanJob;
        internal static List<Language> LangaugeList = new List<Language>();
        public static DateTime MaxTime = DateTime.MinValue;
        public static void Main(string[] args)
        {
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

            new Thread(() => Bot.Initialize().GetAwaiter().GetResult()) { IsBackground = true }.Start();
            new Thread(Bot.MonitorLanguageDirectory) { IsBackground = true }.Start();
            new Thread(Updater.MonitorUpdates) { IsBackground = true }.Start();
            new Thread(UpdateHandler.SpamDetection) { IsBackground = true }.Start();

            _timer = new Timer(TimerOnTick, null, 5000, 1000);
            var wait = TimeSpan.FromSeconds(30);
            _tempbanJob = new System.Threading.Timer(Methods.CheckTempBans, null, wait, wait);
            //now pause the main thread to let everything else run
            Thread.Sleep(-1);
        }

        private static void TimerOnTick(Object stateInfo)
        {
            try
            {
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
