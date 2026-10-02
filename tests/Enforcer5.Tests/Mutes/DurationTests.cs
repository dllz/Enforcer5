using System;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Mutes
{
    [TestClass]
    public class DurationTests
    {
        [DataTestMethod]
        [DataRow("30", 30)]
        [DataRow(" 30 ", 30)]
        [DataRow("30 min", 30)]
        [DataRow("30min", 30)]
        [DataRow("30m", 30)]
        [DataRow("1 minute", 1)]
        [DataRow("45 MINUTES", 45)]
        [DataRow("2 hours", 120)]
        [DataRow("2h", 120)]
        [DataRow("3 hrs", 180)]
        [DataRow("1 hour", 60)]
        [DataRow("1 day", 1440)]
        [DataRow("1d", 1440)]
        [DataRow("7 days", 10080)]
        [DataRow("365 days", 525600)]
        public void Valid_IsParsedToMinutes(string input, int minutes)
        {
            Assert.AreEqual(DurationParseResult.Ok, Duration.TryParse(input, out var duration), input);
            Assert.AreEqual(TimeSpan.FromMinutes(minutes), duration, input);
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void Nothing_IsEmpty(string input)
        {
            Assert.AreEqual(DurationParseResult.Empty, Duration.TryParse(input, out _));
        }

        [DataTestMethod]
        [DataRow("abc")]
        [DataRow("2 weeks")]
        [DataRow("2 hours spamming")]
        [DataRow("-5")]
        [DataRow("1.5 hours")]
        [DataRow("hours")]
        public void Garbage_IsInvalid(string input)
        {
            // The old parser read "2 weeks" as 2 minutes and "abc" as the default, and still said "updated".
            Assert.AreEqual(DurationParseResult.Invalid, Duration.TryParse(input, out _), input);
        }

        [DataTestMethod]
        [DataRow("0")]
        [DataRow("0 days")]
        [DataRow("366 days")]
        [DataRow("9000 hours")]
        [DataRow("999999999 days")]
        [DataRow("999999999 hours")]
        public void OutsideTelegramsRange_IsRejected(string input)
        {
            // Telegram makes a restriction under 30 seconds or over 366 days permanent.
            Assert.AreEqual(DurationParseResult.OutOfRange, Duration.TryParse(input, out _), input);
        }

        [DataTestMethod]
        [DataRow(null, int.MaxValue, 1440)]
        [DataRow("", int.MaxValue, 1440)]
        [DataRow("abc", int.MaxValue, 1440)]
        [DataRow("0", int.MaxValue, 1440)]
        [DataRow("-10", int.MaxValue, 1440)]
        [DataRow("60", int.MaxValue, 60)]
        [DataRow("600000", int.MaxValue, 600000)]
        [DataRow("600000", 525600, 1440)]
        [DataRow("525600", 525600, 525600)]
        public void StoredDefault_FallsBackToOneDay(string stored, int max, int expected)
        {
            // Regression: an unset default used to come back as 0, which muted forever and turned
            // every tempban into a kick.
            Assert.AreEqual(expected, Duration.StoredMinutesOrDefault(stored, max));
        }

        [TestMethod]
        public void MaximumMinutes_IsTheLongestTempmute()
        {
            Assert.AreEqual(525600, Duration.MaximumMinutes);
        }

        [DataTestMethod]
        [DataRow(0, "00:00:00")]
        [DataRow(1, "00:00:01")]
        [DataRow(90, "00:01:30")]
        [DataRow(1440, "01:00:00")]
        [DataRow(525600, "365:00:00")]
        public void Display_IsDaysHoursMinutes(int minutes, string expected)
        {
            Assert.AreEqual(expected, Duration.ToDisplay(TimeSpan.FromMinutes(minutes)));
        }

        [TestMethod]
        public void Display_RoundsSecondsUpAndNeverGoesNegative()
        {
            Assert.AreEqual("00:00:01", Duration.ToDisplay(TimeSpan.FromSeconds(5)));
            Assert.AreEqual("00:00:00", Duration.ToDisplay(TimeSpan.FromMinutes(-3)));
        }

        private static Message Command(string text, bool reply = false, MessageEntity[] entities = null) => new Message
        {
            Text = text,
            Entities = entities,
            ReplyToMessage = reply ? new Message { From = new User { Id = 42 } } : null
        };

        [TestMethod]
        public void AfterTarget_WithReply_IsTheWholeArgument()
        {
            Assert.AreEqual("2 hours", Duration.AfterTarget(Command("/tempmute 2 hours", reply: true), "2 hours"));
            Assert.IsNull(Duration.AfterTarget(Command("/tempmute", reply: true), null));
        }

        [TestMethod]
        public void AfterTarget_WithIdOrUsername_SkipsTheFirstWord()
        {
            Assert.AreEqual("2 hours", Duration.AfterTarget(Command("/tempmute @someone 2 hours"), "@someone 2 hours"));
            Assert.AreEqual("30", Duration.AfterTarget(Command("/tempmute 12345 30"), "12345 30"));
            Assert.IsNull(Duration.AfterTarget(Command("/tempmute 12345"), "12345"));
        }

        [TestMethod]
        public void AfterTarget_WithTextMention_SkipsTheWholeMention()
        {
            // A text mention is a name without a username, and a name can have spaces.
            const string text = "/tempmute John Smith 1d";
            var mention = new MessageEntity { Type = MessageEntityType.TextMention, Offset = 10, Length = 10, User = new User { Id = 7 } };
            Assert.AreEqual("1d", Duration.AfterTarget(Command(text, entities: new[] { mention }), "John Smith 1d"));
        }
    }
}
