using System;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.ChannelPostTests
{
    [TestClass]
    public class ChannelPostDecisionTests
    {
        private const long ChatId = -1001234567890;
        private const long Spammer = -1009999999999;
        private const long Linked = -1005555555555;

        private static Message PostedAs(long? senderChatId, bool automaticForward = false) => new Message
        {
            Id = 7,
            Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup },
            From = new User { Id = ChannelPosts.PlaceholderSenderId, IsBot = true, FirstName = "Channel" },
            SenderChat = senderChatId == null ? null : new Chat { Id = senderChatId.Value, Type = ChatType.Channel, Title = "Totally the admin" },
            IsAutomaticForward = automaticForward,
            Text = "hello"
        };

        private static ChannelPostPolicy On(bool allowed = false, long? linked = 0) => new ChannelPostPolicy(true, allowed, linked);

        [TestMethod]
        public void OrdinaryMessage_IsIgnored()
        {
            var message = PostedAs(null);
            message.From = new User { Id = 42, FirstName = "Member" };
            Assert.AreEqual(ChannelPostDecision.Ignore, ChannelPosts.Decide(message, ChatId, On()));
        }

        [TestMethod]
        public void AnonymousAdmin_IsIgnored()
        {
            Assert.AreEqual(ChannelPostDecision.Ignore, ChannelPosts.Decide(PostedAs(ChatId), ChatId, On()));
        }

        [TestMethod]
        public void SettingOff_LeavesChannelPostsAlone()
        {
            Assert.AreEqual(ChannelPostDecision.Ignore,
                ChannelPosts.Decide(PostedAs(Spammer), ChatId, new ChannelPostPolicy(false, false, 0)));
            Assert.AreEqual(ChannelPostDecision.Ignore, ChannelPosts.Decide(PostedAs(Spammer), ChatId, null));
        }

        [TestMethod]
        public void ChannelPost_IsRemoved()
        {
            Assert.AreEqual(ChannelPostDecision.Remove, ChannelPosts.Decide(PostedAs(Spammer), ChatId, On(linked: 0)));
            Assert.AreEqual(ChannelPostDecision.Remove, ChannelPosts.Decide(PostedAs(Spammer), ChatId, On(linked: Linked)));
        }

        [TestMethod]
        public void UnknownLinkedChannel_NeedsALookupFirst()
        {
            Assert.AreEqual(ChannelPostDecision.RemoveUnlessLinked, ChannelPosts.Decide(PostedAs(Spammer), ChatId, On(linked: null)));
        }

        [TestMethod]
        public void LinkedChannel_IsExempt()
        {
            Assert.AreEqual(ChannelPostDecision.Exempt, ChannelPosts.Decide(PostedAs(Linked), ChatId, On(linked: Linked)));
        }

        [TestMethod]
        public void AutomaticForward_IsExemptWithoutALookup()
        {
            Assert.AreEqual(ChannelPostDecision.Exempt,
                ChannelPosts.Decide(PostedAs(Linked, automaticForward: true), ChatId, On(linked: null)));
        }

        [TestMethod]
        public void AllowedChannel_IsExempt()
        {
            Assert.AreEqual(ChannelPostDecision.Exempt, ChannelPosts.Decide(PostedAs(Spammer), ChatId, On(allowed: true, linked: null)));
        }

        [TestMethod]
        public void OnMessageChannelPost_DispatchesNothingForExemptOrIgnoredPosts()
        {
            // Remove paths reach Telegram and cannot run here; these must return without doing so.
            var message = PostedAs(Spammer);
            var ctx = new MessageContext
            {
                Update = new Update { Message = message },
                Message = message,
                ChatId = ChatId,
                UserId = ChannelPosts.PlaceholderSenderId,
                IsGroup = true,
                Complete = true,
                ChannelPosts = On(allowed: true)
            };
            Assert.AreEqual(ChannelPostDecision.Exempt, OnMessage.ChannelPost(ctx));

            ctx.ChannelPosts = new ChannelPostPolicy(false, false, null);
            Assert.AreEqual(ChannelPostDecision.Ignore, OnMessage.ChannelPost(ctx));
        }
    }

    [TestClass]
    public class ChannelTargetTests
    {
        private const long ChatId = -1001234567890;

        private static Message Command(Message reply = null) => new Message
        {
            Id = 8,
            Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup },
            From = new User { Id = 42, FirstName = "Admin" },
            Text = "/ban",
            ReplyToMessage = reply
        };

        private static Message PostedAs(long chatId, string title) => new Message
        {
            Id = 7,
            Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup },
            From = new User { Id = ChannelPosts.PlaceholderSenderId, IsBot = true, FirstName = "Channel" },
            SenderChat = new Chat { Id = chatId, Type = ChatType.Channel, Title = title }
        };

        [TestMethod]
        public void ReplyToChannelPost_TargetsTheChannel()
        {
            var kind = ChannelPosts.FromModerationCommand(Command(PostedAs(-1009, "Spam")), "some reason", ChatId, out var id, out var title);
            Assert.AreEqual(ChannelTargetKind.Channel, kind);
            Assert.AreEqual(-1009L, id);
            Assert.AreEqual("Spam", title);
        }

        [TestMethod]
        public void ReplyToAnonymousAdmin_TargetsTheGroup()
        {
            Assert.AreEqual(ChannelTargetKind.Group,
                ChannelPosts.FromModerationCommand(Command(PostedAs(ChatId, "Group")), null, ChatId, out _, out _));
        }

        [TestMethod]
        public void ReplyToUser_IsNotAChannel()
        {
            var reply = new Message { Id = 6, Chat = new Chat { Id = ChatId }, From = new User { Id = 77, FirstName = "User" } };
            // With a reply, a negative number is the reason, not a channel id.
            Assert.AreEqual(ChannelTargetKind.None, ChannelPosts.FromModerationCommand(Command(reply), "-1009", ChatId, out _, out _));
        }

        [DataTestMethod]
        [DataRow("-1009", "Channel")]
        [DataRow("-1009 spamming", "Channel")]
        [DataRow("-1001234567890", "Group")]
        [DataRow("12345", "None")]
        [DataRow("@someone", "None")]
        [DataRow(null, "None")]
        public void Argument_WithoutAReply(string argument, string expected)
        {
            Assert.AreEqual(Enum.Parse<ChannelTargetKind>(expected), ChannelPosts.FromModerationCommand(Command(), argument, ChatId, out _, out _));
        }

        [TestMethod]
        public void FromReply_FindsPostedAsOrForwardedFromChannel()
        {
            Assert.AreEqual(-1009L, ChannelPosts.FromReply(PostedAs(-1009, "Spam")).Id);

            var forwarded = new Message
            {
                Id = 5,
                Chat = new Chat { Id = ChatId },
                From = new User { Id = 77, FirstName = "User" },
                ForwardOrigin = new MessageOriginChannel { Chat = new Chat { Id = -1008, Type = ChatType.Channel, Title = "News" } }
            };
            Assert.AreEqual(-1008L, ChannelPosts.FromReply(forwarded).Id);

            Assert.IsNull(ChannelPosts.FromReply(new Message { Id = 4, From = new User { Id = 77 } }));
            Assert.IsNull(ChannelPosts.FromReply(null));
        }

        [DataTestMethod]
        [DataRow("@channel", true)]
        [DataRow("@", false)]
        [DataRow("@two words", false)]
        [DataRow("channel", false)]
        public void IsUsername(string text, bool expected)
        {
            Assert.AreEqual(expected, ChannelPosts.IsUsername(text));
        }

        [DataTestMethod]
        [DataRow("-100:42:Name:Group", true)]
        [DataRow("-100:42", true)]
        [DataRow("-100:421:Name:Group", false)]
        [DataRow("-1001:42:Name:Group", false)]
        [DataRow("", false)]
        public void TempbanEntry_MatchesItsUserOnly(string value, bool expected)
        {
            // /ban used to compare with Equals, which never matched the stored "{chat}:{user}:{name}:{group}".
            Assert.AreEqual(expected, ChannelPosts.IsTempbanEntryFor(value, -100, 42));
        }

        [TestMethod]
        public void AllowedChannel_DisplaysTitleAndId()
        {
            Assert.AreEqual("News (-1008)", new AllowedChannel(-1008, "News").ToString());
            Assert.AreEqual("-1008", new AllowedChannel(-1008, null).ToString());
        }
    }
}
