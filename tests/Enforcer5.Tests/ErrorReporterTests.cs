using System;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Exceptions;

namespace Enforcer5.Tests
{
    [TestClass]
    public class ErrorReporterTests
    {
        private DateTime _now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

        private ErrorThrottle NewThrottle(int perMinute = 20) =>
            new ErrorThrottle(() => _now, TimeSpan.FromMinutes(10), perMinute);

        [TestMethod]
        public void Throttle_SameKeyOncePerWindow_AndCountsRepeats()
        {
            var t = NewThrottle();
            Assert.IsTrue(t.TryAcquire("a", out _, out _));
            _now = _now.AddMinutes(1);
            Assert.IsFalse(t.TryAcquire("a", out _, out _));
            Assert.IsFalse(t.TryAcquire("a", out _, out _));
            Assert.IsTrue(t.TryAcquire("b", out _, out _), "a different error is not held back");

            _now = _now.AddMinutes(10);
            Assert.IsTrue(t.TryAcquire("a", out var suppressed, out _));
            Assert.AreEqual(2, suppressed);
            _now = _now.AddMinutes(10);
            Assert.IsTrue(t.TryAcquire("a", out suppressed, out _));
            Assert.AreEqual(0, suppressed);
        }

        [TestMethod]
        public void Throttle_CapsReportsPerMinute_AndCountsDropped()
        {
            var t = NewThrottle(perMinute: 3);
            for (var i = 0; i < 3; i++)
                Assert.IsTrue(t.TryAcquire($"k{i}", out _, out _));
            Assert.IsFalse(t.TryAcquire("k3", out _, out _));
            Assert.IsFalse(t.TryAcquire("k4", out _, out _));

            _now = _now.AddMinutes(1);
            Assert.IsTrue(t.TryAcquire("k3", out _, out var dropped), "a dropped key is sent once there is room");
            Assert.AreEqual(2, dropped);
            Assert.IsTrue(t.TryAcquire("k5", out _, out dropped));
            Assert.AreEqual(0, dropped);
        }

        [TestMethod]
        public void Normalise_IgnoresIds()
        {
            Assert.AreEqual(
                ErrorReporter.Normalise("Send to -1001234 failed for 8887704339"),
                ErrorReporter.Normalise("Send to -1009999 failed for 42"));
        }

        [TestMethod]
        public void Unwrap_StripsAggregateWrappers()
        {
            var api = new ApiRequestException("Bad Request: user not found", 400);
            var wrapped = new AggregateException(new AggregateException(api));
            Assert.AreSame(api, ErrorReporter.Unwrap(wrapped));
        }

        [TestMethod]
        public void Format_HasErrorContextAndCounts()
        {
            Exception e;
            try { throw new AggregateException(new ApiRequestException("Bad Request: user not found", 400)); }
            catch (Exception ex) { e = ex; }
            var ctx = new ErrorContext("message", -100123, 42, "/status 8887704339");

            var text = ErrorReporter.Format("AdminCommands.Status", null, e, ctx, 3, 5, "normal");

            StringAssert.StartsWith(text, "#error #normal AdminCommands.Status\n");
            StringAssert.Contains(text, "ApiRequestException 400: Bad Request: user not found");
            StringAssert.Contains(text, "message | chat -100123 | from 42");
            StringAssert.Contains(text, "Text: /status 8887704339");
            StringAssert.Contains(text, "+3 more");
            StringAssert.Contains(text, "5 other errors");
        }

        [TestMethod]
        public void Format_ShowsCauseChain_AndStaysUnderTelegramLimit()
        {
            var e = new Exception("Error getting string statusRestricted with parameters x",
                new Exception("Cannot load english string for fallback"));
            var ctx = new ErrorContext("message", 1, 2, new string('x', 5000));

            var text = ErrorReporter.Format("Methods.GetLocaleString", null, e, ctx, 0, 0, "premium");

            StringAssert.Contains(text, "statusRestricted");
            StringAssert.Contains(text, "Caused by Exception: Cannot load english string for fallback");
            Assert.IsTrue(text.Length <= 4096);
        }

        [TestMethod]
        public void Report_NeverThrows_WithoutABot()
        {
            ErrorReporter.Report(new InvalidOperationException("boom"), "test");
            ErrorReporter.Report("plain message");
            ErrorReporter.Report((Exception)null);
        }
    }
}
