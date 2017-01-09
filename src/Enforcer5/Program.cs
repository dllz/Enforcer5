﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Languages;

namespace Enforcer5
{
    public class Program
    {
        internal static bool Running = true;
        private static bool _writingInfo = false;
        internal static List<long> MessagesReceived = new List<long>();
        internal static List<long> MessagesProcessed = new List<long>();
        internal static List<long> MessagesSent = new List<long>();
        private static long _previousMessages, _previousMessagesTx, _previousMessagesRx;
        internal static float MessagePxPerSecond, MessageRxPerSecond, MessageTxPerSecond;
        internal static int NodeMessagesSent = 0;
        private static System.Threading.Timer _timer;
        internal static List<Language> LangaugeList;
        public static DateTime MaxTime = DateTime.MinValue;
        public static void Main(string[] args)
        {
            Console.Title = "Enforcer";

            //Make sure another instance isn't already running
            if (Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1)
            {
                Environment.Exit(2);
            }

            new Thread(() => Bot.Initialize()).Start();

            //new Thread(UpdateHandler.SpamDetection).Start();
            //new Thread(UpdateHandler.BanMonitor).Start();
            _timer = new Timer(TimerOnTick, null, 5000, 1000);
            new Task(Methods.IntialiseLanguages);
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
