using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Ids the bot shows can be copied with a tap: they go to Telegram as &lt;code&gt; (Daniel, 2026-11-04), and
    /// nothing of the marking reaches plain text. Needs the test database, like every harness scenario.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class CopyableIdScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static void AssertNoMarks(HarnessChat chat) =>
            Assert.IsFalse(chat.Telegram.Calls.Any(c => c.Text != null && Helpers.Ids.HasMarks(c.Text)),
                "no mark reaches Telegram");

        [TestMethod]
        public async Task Id_InTheGroup_IsTheGroupsId_AsCode_WithTheTitleEscaped()
        {
            await using var chat = await HarnessChat.StartAsync(title: "Cats & <Dogs>");
            var member = chat.Add("Member");

            await chat.SendAsync(member, "/id");

            Assert.AreEqual($"<code>{chat.ChatId}</code>\nCats &amp; &lt;Dogs&gt;", chat.Replies.Last());
            AssertNoMarks(chat);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Id_AsAReply_IsTheMembersId_AsCode()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("A<B");
            var asker = chat.Add("Asker");
            var post = await chat.SendAsync(member, "hello");

            await chat.SendAsync(asker, "/id", replyTo: post);

            Assert.AreEqual($"<code>{member.Id}</code>\n<a href=\"tg://user?id={member.Id}\">A&lt;B</a>", chat.Replies.Last());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWelcomesId_IsCode_ButPlainInsideTheAdminsLink()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "custom";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "Your id: $id <a href=\"tg://user?id=$id\">profile</a>";

            await chat.JoinAsync(new HarnessUser(424250, "Joiner"));

            Assert.IsTrue(chat.Replies.Contains("Your id: <code>424250</code> <a href=\"tg://user?id=424250\">profile</a>"),
                string.Join("\n", chat.Replies));
            AssertNoMarks(chat);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWarn_ShowsTheMembersIdAsCode()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "spam");

            await chat.SendAsync(admin, "/warn", replyTo: post);

            StringAssert.Contains(chat.Replies.Last(), $"<a href=\"tg://user?id={member.Id}\">Member</a> (<code>{member.Id}</code>)");
            AssertNoMarks(chat);
            AssertNoErrors(chat);
        }
    }
}
