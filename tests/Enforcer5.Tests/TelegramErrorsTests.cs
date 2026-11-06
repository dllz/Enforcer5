using System;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Enforcer5.Tests
{
    [TestClass]
    public class TelegramErrorsTests
    {
        [DataTestMethod]
        [DataRow("Bad Request: not enough rights to send text messages to the chat", "NoSendRights")]
        [DataRow("Bad Request: CHAT_WRITE_FORBIDDEN", "NoSendRights")]
        [DataRow("Bad Request: have no rights to send a message", "NoSendRights")]
        [DataRow("Bad Request: CHAT_RESTRICTED", "NoSendRights")]
        [DataRow("Bad Request: not enough rights to send animations to the chat", "NoMediaRights")]
        [DataRow("Bad Request: not enough rights to send photos to the chat", "NoMediaRights")]
        [DataRow("Bad Request: not enough rights to restrict/unrestrict chat member", "NoAdminRights")]
        [DataRow("Bad Request: not enough rights to kick/unban chat member", "NoAdminRights")]
        [DataRow("Bad Request: need administrator rights in the channel chat", "NoAdminRights")]
        [DataRow("Bad Request: CHAT_ADMIN_REQUIRED", "NoAdminRights")]
        [DataRow("Forbidden: bot was kicked from the supergroup chat", "ChatGone")]
        [DataRow("Forbidden: bot is not a member of the channel chat", "ChatGone")]
        [DataRow("Forbidden: bot was blocked by the user", "ChatGone")]
        [DataRow("Forbidden: bot can't initiate conversation with a user", "ChatGone")]
        [DataRow("Bad Request: chat not found", "ChatGone")]
        [DataRow("Bad Request: PEER_ID_INVALID", "ChatGone")]
        [DataRow("Bad Request: group chat was upgraded to a supergroup chat", "Migrated")]
        [DataRow("Bad Request: message to edit not found", "MessageGone")]
        [DataRow("Bad Request: message to delete not found", "MessageGone")]
        [DataRow("Bad Request: message can't be deleted", "MessageGone")]
        [DataRow("Bad Request: message is not modified: specified new message content and reply markup are exactly the same", "MessageGone")]
        [DataRow("Bad Request: message to be replied not found", "MessageGone")]
        [DataRow("Bad Request: MESSAGE_ID_INVALID", "MessageGone")]
        [DataRow("Bad Request: TOPIC_CLOSED", "TopicClosed")]
        [DataRow("Bad Request: message thread not found", "TopicClosed")]
        [DataRow("Bad Request: query is too old and response timeout expired or query ID is invalid", "QueryTooOld")]
        [DataRow("Bad Request: method is available only for supergroups", "NotSupergroup")]
        [DataRow("Bad Request: user is an administrator of the chat", "TargetIsAdmin")]
        [DataRow("Bad Request: can't remove chat owner", "TargetIsAdmin")]
        [DataRow("Bad Request: can't restrict self", "TargetIsAdmin")]
        [DataRow("Bad Request: user not found", "MemberGone")]
        [DataRow("Bad Request: member not found", "MemberGone")]
        [DataRow("Bad Request: PARTICIPANT_ID_INVALID", "MemberGone")]
        [DataRow("Bad Request: can't parse entities: unsupported start tag", "Other")]
        [DataRow("Bad Request: message is too long", "Other")]
        [DataRow("Bad Request: wrong file identifier/HTTP URL specified", "Other")]
        public void Classify_SortsTelegramsDescriptions(string description, string expected)
        {
            Assert.AreEqual(expected, TelegramErrors.Classify(description).ToString());
        }

        [TestMethod]
        public void Classify_UnwrapsBlockingCallsAndReadsTheCode()
        {
            var wrapped = new AggregateException(new ApiRequestException("Bad Request: not enough rights to send text messages to the chat", 400));
            Assert.AreEqual(TelegramErrorKind.NoSendRights, TelegramErrors.Classify(wrapped));
            Assert.AreEqual(TelegramErrorKind.RateLimited, TelegramErrors.Classify(new ApiRequestException("Too Many Requests: retry after 5", 429)));
            Assert.AreEqual(TelegramErrorKind.ChatGone, TelegramErrors.Classify(new ApiRequestException("Forbidden: something new", 403)),
                "any other 403 means the bot cannot reach the chat");
            Assert.AreEqual(TelegramErrorKind.Other, TelegramErrors.Classify(new InvalidOperationException("not Telegram")));
        }

        [TestMethod]
        public void Migrated_GivesTheNewId()
        {
            var e = new ApiRequestException("Bad Request: group chat was upgraded to a supergroup chat", 400,
                new ResponseParameters { MigrateToChatId = -1001234 });
            Assert.AreEqual(TelegramErrorKind.Migrated, TelegramErrors.Classify(e));
            Assert.AreEqual(-1001234L, TelegramErrors.MigratedTo(e));
            Assert.IsNull(TelegramErrors.MigratedTo(new ApiRequestException("Bad Request: chat not found", 400)));
        }

        [TestMethod]
        public void Report_KeepsExpectedFailuresOutOfTheErrorChat()
        {
            var reported = 0;
            var expected = 0;
            var previous = (ErrorReporter.Observer, ErrorReporter.Expected);
            ErrorReporter.Observer = _ => reported++;
            ErrorReporter.Expected = _ => expected++;
            try
            {
                ErrorReporter.Report(new ApiRequestException("Bad Request: not enough rights to send text messages to the chat", 400), "a send");
                ErrorReporter.Report(new ApiRequestException("Bad Request: message is not modified", 400), "an edit");
                Assert.AreEqual(0, reported);
                Assert.AreEqual(2, expected);
                ErrorReporter.Report(new ApiRequestException("Bad Request: wrong file identifier", 400), "a real one");
                Assert.AreEqual(1, reported);
            }
            finally
            {
                (ErrorReporter.Observer, ErrorReporter.Expected) = previous;
            }
        }
    }
}
