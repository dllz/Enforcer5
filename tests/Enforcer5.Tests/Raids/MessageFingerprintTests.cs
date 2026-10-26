using System;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Raids
{
    [TestClass]
    public class MessageFingerprintTests
    {
        private static Message Text(string text, params MessageEntity[] entities) =>
            new Message { Text = text, Entities = entities.Length == 0 ? null : entities, Chat = new Chat { Id = -1 } };

        [TestMethod]
        public void TheSameWords_HashTheSame_WhateverTheCaseSpacingOrNumbers()
        {
            Assert.AreEqual(MessageFingerprint.Hash("Join NOW!! 100% free"), MessageFingerprint.Hash("join   now - 250% FREE"));
            Assert.AreNotEqual(MessageFingerprint.Hash("join now"), MessageFingerprint.Hash("join later"));
            Assert.IsNull(MessageFingerprint.Hash("12345 !!!"), "nothing but digits and punctuation");
        }

        [TestMethod]
        public void TheHash_DoesNotKeepTheText()
        {
            var hash = MessageFingerprint.Hash("my phone number is secret");
            Assert.AreEqual(24, hash.Length);
            Assert.IsFalse(hash.Contains("secret"));
        }

        [DataTestMethod]
        [DataRow("https://t.me/joinchat/abc", true)]
        [DataRow("t.me/spamgroup  t.me/other", true)]
        [DataRow("join https://t.me/x", true)]
        [DataRow("have a look at this cool article https://example.com", false)]
        [DataRow("hello everyone", false)]
        public void BareLinks(string text, bool bare) =>
            Assert.AreEqual(bare, MessageFingerprint.Of(Text(text)).BareLink);

        [TestMethod]
        public void AMentionAlone_IsABareLinkToo()
        {
            var print = MessageFingerprint.Of(Text("@spamchannel", new MessageEntity { Type = MessageEntityType.Mention, Offset = 0, Length = 12 }));
            Assert.IsTrue(print.BareLink);
            Assert.AreEqual(1, print.LinkCount);
        }

        [DataTestMethod]
        [DataRow("hello there", "latin")]
        [DataRow("سلام دوستان", "arabic")]
        [DataRow("Привет всем", "cyrillic")]
        [DataRow("大家好", "cjk")]
        [DataRow("안녕하세요", "hangul")]
        [DataRow("123 !!", null)]
        public void Scripts(string text, string script) => Assert.AreEqual(script, MessageFingerprint.ScriptOf(text));

        [TestMethod]
        public void APhoto_IsKnownByItsLargestSizesUniqueId()
        {
            var photo = new Message
            {
                Chat = new Chat { Id = -1 },
                Photo = new[]
                {
                    new PhotoSize { FileId = "a", FileUniqueId = "small", Width = 90, Height = 90 },
                    new PhotoSize { FileId = "b", FileUniqueId = "large", Width = 1280, Height = 1280 }
                }
            };
            var print = MessageFingerprint.Of(photo);
            Assert.AreEqual(("photo", "large"), (print.ContentType, print.MediaUniqueId));
        }
    }

    [TestClass]
    public class CaptureBufferTests
    {
        private static readonly DateTimeOffset At = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void AJoinSeenTwice_IsOneJoin_WithTheLinkTheMemberUpdateGave()
        {
            var buffer = new CaptureBuffer();
            buffer.Join(new JoinEvent(-1, 5, At, "unknown", null, null, null));
            buffer.Join(new JoinEvent(-1, 5, At.AddSeconds(1), "link", "https://t.me/+abc", "ads", null));
            buffer.Join(new JoinEvent(-1, 5, At.AddMinutes(10), "unknown", null, null, null)); // a later join

            var joins = buffer.Take().Batch.Joins;

            Assert.AreEqual(2, joins.Count);
            Assert.AreEqual(("link", "https://t.me/+abc", At), (joins[0].Source, joins[0].InviteLink, joins[0].At));
        }

        [TestMethod]
        public void Messages_AddUp_PerMember()
        {
            var buffer = new CaptureBuffer();
            buffer.Message(-1, 5, At, 3);
            buffer.Message(-1, 5, At.AddSeconds(5), 4);
            buffer.Message(-1, 6, At, 1);

            var members = buffer.Take().Batch.Members;

            Assert.AreEqual(2, members.Count);
            var five = members[0].UserId == 5 ? members[0] : members[1];
            Assert.AreEqual((2L, At, At.AddSeconds(5), 4L), (five.Messages, five.FirstMessageAt, five.LastMessageAt, five.RedisCount));
        }

        [TestMethod]
        public void ABatchThatFailed_GoesBack_UnderWhatCameSince()
        {
            var buffer = new CaptureBuffer();
            buffer.Message(-1, 5, At, 1);
            var (failed, _) = buffer.Take();
            buffer.Message(-1, 5, At.AddSeconds(5), 2);

            buffer.Restore(failed);

            var m = buffer.Take().Batch.Members[0];
            Assert.AreEqual((2L, At), (m.Messages, m.FirstMessageAt));
        }
    }

    [TestClass]
    [DoNotParallelize] // the norms are static
    public class MemberTrustTests
    {
        private static readonly DateTime Now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        private static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

        [TestCleanup]
        public void Cleanup() => MemberTrust.SetNormsForTests(null);

        [TestMethod]
        public void AQuietGroup_Twenty_Messages_AndAWeek()
        {
            Assert.AreEqual(TrustLevel.New, MemberTrust.Level(-1, 19, Unix(Now.AddDays(-30)), null, Now));
            Assert.AreEqual(TrustLevel.New, MemberTrust.Level(-1, 50, Unix(Now.AddDays(-3)), null, Now), "talked enough, but too new");
            Assert.AreEqual(TrustLevel.Established, MemberTrust.Level(-1, 20, Unix(Now.AddDays(-8)), null, Now));
        }

        [TestMethod]
        public void ABusyGroup_AsksForMore()
        {
            MemberTrust.SetNormsForTests(new GroupNorms(-1, 1000, 30, 5, 500));

            Assert.AreEqual(TrustLevel.New, MemberTrust.Level(-1, 500, Unix(Now.AddDays(-60)), null, Now));
            Assert.AreEqual(TrustLevel.Established, MemberTrust.Level(-1, 1200, Unix(Now.AddDays(-60)), null, Now));
            Assert.AreEqual(TrustLevel.Established, MemberTrust.Level(-2, 25, Unix(Now.AddDays(-60)), null, Now), "other groups keep their own");
        }

        [TestMethod]
        public void SomeoneFromBeforeThisExisted_IsEstablishedByTheirCount()
        {
            Assert.AreEqual(TrustLevel.Established, MemberTrust.Level(-1, 400, null, null, Now));
        }

        [TestMethod]
        public void RejoiningRecently_MakesYouNewAgain()
        {
            Assert.AreEqual(TrustLevel.New, MemberTrust.Level(-1, 400, null, Unix(Now.AddHours(-1)), Now));
        }
    }
}
