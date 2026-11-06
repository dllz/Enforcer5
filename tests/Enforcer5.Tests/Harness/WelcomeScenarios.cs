using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>The welcome a group sends when someone joins. Needs the test database, like every harness scenario.</summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class WelcomeScenarios
    {
        private static string[] SentDocuments(HarnessChat chat) =>
            chat.Telegram.Calls.Where(c => c.Method == "sendDocument" && c.ChatId == chat.ChatId).Select(c => c.Text).ToArray();

        private static string Link(HarnessUser user) => $"<a href=\"tg://user?id={user.Id}\">{user.FirstName}</a>";

        private static Telegram.Bot.Requests.SendMessageRequest LastSent(HarnessChat chat) =>
            (Telegram.Bot.Requests.SendMessageRequest)chat.Telegram.Accepted
                .Last(c => c.Method == "sendMessage" && c.ChatId == chat.ChatId).Request;

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task GifWelcome_SendsTheSavedGif()
        {
            // What /welcome saves when an admin replies to a GIF: the file in "media", the
            // command's (empty) argument in "content".
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "media";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "media"] = "GIF-FILE-ID";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "hasmedia"] = "True";

            await chat.JoinAsync(new HarnessUser(424243, "Joiner"));

            CollectionAssert.AreEqual(new[] { "GIF-FILE-ID" }, SentDocuments(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task GifWelcomeFromBefore2017Fix_StillReadsContent()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "media";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "OLD-GIF-FILE-ID";

            await chat.JoinAsync(new HarnessUser(424244, "Joiner"));

            CollectionAssert.AreEqual(new[] { "OLD-GIF-FILE-ID" }, SentDocuments(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task GifWelcomeWithoutAGif_SendsNothing_AndReportsNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "media";

            await chat.JoinAsync(new HarnessUser(424245, "Joiner"));

            Assert.AreEqual(0, SentDocuments(chat).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task CustomWelcomeMarkedWithMediaButWithoutAGif_SendsTheText()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "custom";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "Hello there";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "hasmedia"] = "true";

            await chat.JoinAsync(new HarnessUser(424246, "Joiner"));

            Assert.AreEqual(0, SentDocuments(chat).Length);
            Assert.IsTrue(chat.Replies.Any(r => r.Contains("Hello there")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }
        private static void CustomWelcome(HarnessChat chat, string text)
        {
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "custom";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = text;
        }

        [TestMethod]
        public async Task AJoinWithoutAServiceMessage_IsWelcomedFromTheMemberUpdate()
        {
            // Big groups (the support group, The Origin) get no "joined" service message, only the member update.
            await using var chat = await HarnessChat.StartAsync();
            CustomWelcome(chat, "Welcome $name");
            var joiner = chat.Add("Newcomer", MemberState.Left);

            chat.Deliver(chat.MemberJoined(joiner, "https://t.me/+invite"));
            await chat.CompleteAsync();

            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains($"Welcome {Link(joiner)}")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AServiceMessageAndAMemberUpdate_WelcomeOnce()
        {
            await using var chat = await HarnessChat.StartAsync();
            CustomWelcome(chat, "Welcome $name");
            var joiner = chat.Add("Newcomer", MemberState.Left);

            chat.Deliver(chat.MemberJoined(joiner, "https://t.me/+invite"));
            chat.Deliver(chat.Join(joiner));
            await chat.CompleteAsync();

            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains($"Welcome {Link(joiner)}")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WelcomeOnAndOff_SwitchTheWelcome_InsteadOfSavingTheWord()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            CustomWelcome(chat, "Welcome $name");

            await chat.SendAsync(admin, "/welcome off");
            Assert.IsFalse(Enforcer5.Helpers.GroupConfigs.For(chat.ChatId).Flag(Enforcer5.Data.ConfigFlag.Welcome));
            Assert.AreEqual("Welcome $name", Enforcer5.Helpers.GroupConfigs.For(chat.ChatId).Welcome.Content);
            await chat.JoinAsync(chat.Add("Quiet", MemberState.Left));
            Assert.IsFalse(chat.Replies.Any(r => r.Contains("Quiet")));

            await chat.SendAsync(admin, "/welcome on");
            Assert.IsTrue(Enforcer5.Helpers.GroupConfigs.For(chat.ChatId).Flag(Enforcer5.Data.ConfigFlag.Welcome));
            var loud = chat.Add("Loud", MemberState.Left);
            await chat.JoinAsync(loud);
            Assert.IsTrue(chat.Replies.Any(r => r.Contains($"Welcome {Link(loud)}")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task CustomWelcome_LinksTheNameToTheProfile()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            CustomWelcome(chat, "Hello $name, this is $title");
            var joiner = chat.Add("Tom & Jerry", MemberState.Left);

            await chat.JoinAsync(joiner);

            var sent = LastSent(chat);
            Assert.AreEqual($"Hello <a href=\"tg://user?id={joiner.Id}\">Tom &amp; Jerry</a>, this is Harness group", sent.Text);
            Assert.AreEqual(ParseMode.Html, sent.ParseMode);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task CustomWelcome_WithItsOwnLinkAroundTheName_KeepsItsLink()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            CustomWelcome(chat, "Hi <a href=\"tg://user?id=$id\">$name</a>, and welcome $name");
            var joiner = chat.Add("Ann", MemberState.Left);

            await chat.JoinAsync(joiner);

            Assert.AreEqual($"Hi <a href=\"tg://user?id={joiner.Id}\">Ann</a>, and welcome {Link(joiner)}", LastSent(chat).Text);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task CustomWelcomeWithBrokenHtml_GoesOutPlain_WithoutTheLinkTags()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            CustomWelcome(chat, "We <3 you $name");
            var joiner = chat.Add("Bo", MemberState.Left);

            await chat.JoinAsync(joiner);

            CollectionAssert.AreEqual(new[] { "We <3 you Bo" }, chat.Replies.ToArray());
            Assert.AreEqual(ParseMode.None, LastSent(chat).ParseMode);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task CustomWelcomeWithAGif_LinksTheNameInTheCaption()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            CustomWelcome(chat, "Hello $name");
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "hasmedia"] = "true";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "media"] = "GIF-FILE-ID";
            var joiner = chat.Add("Cy", MemberState.Left);

            await chat.JoinAsync(joiner);

            var sent = (Telegram.Bot.Requests.SendDocumentRequest)chat.Telegram.Accepted.Single(c => c.Method == "sendDocument").Request;
            Assert.AreEqual($"Hello {Link(joiner)}", sent.Caption);
            Assert.AreEqual(ParseMode.Html, sent.ParseMode);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task CustomWelcomeWithAGifAndBrokenHtml_SendsThePlainCaption()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            CustomWelcome(chat, "We <3 you $name");
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "hasmedia"] = "true";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "media"] = "GIF-FILE-ID";
            var joiner = chat.Add("Di", MemberState.Left);

            await chat.JoinAsync(joiner);

            var sent = (Telegram.Bot.Requests.SendDocumentRequest)chat.Telegram.Accepted.Single(c => c.Method == "sendDocument").Request;
            Assert.AreEqual("We <3 you Di", sent.Caption);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DefaultWelcome_LinksTheName()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "composed";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "no";
            var joiner = chat.Add("Eve", MemberState.Left);

            await chat.JoinAsync(joiner);

            Assert.AreEqual($"👋 Welcome to <b>Harness group</b>, {Link(joiner)}!", LastSent(chat).Text);
            Assert.AreEqual(ParseMode.Html, LastSent(chat).ParseMode);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DefaultWelcomeWithRules_ShowsThemFormatted()
        {
            // It went out plain until November 2026, so members saw "<b>Rules</b>".
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "composed";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "r";
            chat.Keys[$"chat:{chat.ChatId}:rules"] = "Be kind";
            var joiner = chat.Add("Fay", MemberState.Left);

            await chat.JoinAsync(joiner);

            var sent = LastSent(chat);
            StringAssert.Contains(sent.Text, Link(joiner));
            StringAssert.Contains(sent.Text, "<b>Rules</b>");
            StringAssert.Contains(sent.Text, "Be kind");
            Assert.AreEqual(ParseMode.Html, sent.ParseMode);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DefaultWelcomeWithRulesThatDoNotParse_GoesOutWithoutMarkup()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "composed";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "r";
            chat.Keys[$"chat:{chat.ChatId}:rules"] = "No <spam>";
            var joiner = chat.Add("Gil", MemberState.Left);

            await chat.JoinAsync(joiner);

            var sent = LastSent(chat);
            Assert.AreEqual(ParseMode.None, sent.ParseMode);
            StringAssert.StartsWith(sent.Text, "👋 Welcome to Harness group, Gil!");
            StringAssert.Contains(sent.Text, "Rules:");
            StringAssert.Contains(sent.Text, "No <spam>");
            AssertNoErrors(chat);
        }
    }
}
