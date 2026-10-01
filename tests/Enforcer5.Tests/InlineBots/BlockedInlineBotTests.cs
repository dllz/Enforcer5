using System;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.InlineBots
{
    /// <summary>
    /// The decision half of OnMessage.BlockedInlineBot. Only paths that dispatch nothing are
    /// covered: the delete itself goes through the static Telegram client and Redis, which tests
    /// cannot reach until those sit behind an interface.
    /// </summary>
    [TestClass]
    public class BlockedInlineBotTests
    {
        private const long ChatId = -1001234567890;
        private const long UserId = 42;

        private static InlineBotBlock Parse(string input)
        {
            Assert.AreEqual(InlineBotBlockParseResult.Ok, InlineBotBlock.TryParse(input, out var block, out _));
            return block;
        }

        private static MessageContext Context(string viaBot, params string[] blocklist)
        {
            var message = new Message
            {
                Id = 7,
                Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup },
                From = new User { Id = UserId, FirstName = "Member" },
                ViaBot = viaBot == null ? null : new User { Id = 99, IsBot = true, FirstName = "Bot", Username = viaBot },
                Animation = new Animation(),
                Document = new Document { MimeType = "video/mp4" }
            };
            return new MessageContext
            {
                Update = new Update { Message = message },
                Message = message,
                ChatId = ChatId,
                UserId = UserId,
                IsGroup = true,
                Complete = true,
                InlineBotBlocks = Array.ConvertAll(blocklist, Parse)
            };
        }

        private static MessageContext AsAnonymousAdmin(MessageContext ctx)
        {
            ctx.Message.From = new User { Id = 1087968824, IsBot = true, FirstName = "Group" };
            ctx.Message.SenderChat = new Chat { Id = ChatId, Type = ChatType.Supergroup };
            return ctx;
        }

        [TestMethod]
        public void MessageNotSentViaABot_IsLeftAlone()
        {
            Assert.IsFalse(OnMessage.BlockedInlineBot(Context(null, "/.*/")));
        }

        [TestMethod]
        public void EmptyBlocklist_LeavesEverythingAlone()
        {
            Assert.IsFalse(OnMessage.BlockedInlineBot(Context("gif")));
        }

        [TestMethod]
        public void UnlistedBot_IsLeftAlone()
        {
            Assert.IsFalse(OnMessage.BlockedInlineBot(Context("vid", "@gif", "/^spam/")));
        }

        [TestMethod]
        public void BotWithoutUsername_IsLeftAlone()
        {
            Assert.IsFalse(OnMessage.BlockedInlineBot(Context(null, "@gif")));
            var ctx = Context("gif", "/.*/");
            ctx.Message.ViaBot.Username = null;
            Assert.IsFalse(OnMessage.BlockedInlineBot(ctx));
        }

        [TestMethod]
        public void WatchedUser_IsExempt()
        {
            var ctx = Context("gif", "@gif");
            ctx.Watched = true;
            Assert.IsFalse(OnMessage.BlockedInlineBot(ctx));
        }

        [TestMethod]
        public void AutomaticForwardFromLinkedChannel_IsExempt()
        {
            // Deleting it would break the channel post's comment thread.
            var ctx = Context("gif", "@gif");
            ctx.Message.IsAutomaticForward = true;
            Assert.IsFalse(OnMessage.BlockedInlineBot(ctx));
        }

        [DataTestMethod]
        [DataRow("gif", "@gif")]
        [DataRow("GIF", "@gif")]
        [DataRow("spam_gifs_bot", "/^spam/")]
        public void AnonymousAdmin_IsKeptButSkipsTheOtherFilters(string viaBot, string entry)
        {
            // True means "handled": HandleUpdate stops there. For an anonymous admin nothing is
            // deleted, which needs no Telegram call, so this path is fully observable here.
            Assert.IsTrue(OnMessage.BlockedInlineBot(AsAnonymousAdmin(Context(viaBot, "@vid", entry))));
        }
    }
}
