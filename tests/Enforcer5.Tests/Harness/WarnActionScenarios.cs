using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Actions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Warns through the action seam, step C (<c>docs/action-seam.md</c>): one warn for every path, the
    /// max-warns ban or kick as its own action that keeps the warns when it does not happen, and warn
    /// removal by id. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class WarnActionScenarios
    {
        private sealed class Recorder : IDisposable
        {
            private readonly ConcurrentQueue<ActionResult> _results = new();
            internal Recorder() => ActionSeam.Ran += Add;
            private void Add(ActionResult result) => _results.Enqueue(result);
            internal ActionResult[] Results => _results.ToArray();
            public void Dispose() => ActionSeam.Ran -= Add;
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static long Count(HarnessChat chat, string hash, HarnessUser user) =>
            long.TryParse(chat.Hashes[$"chat:{chat.ChatId}:{hash}", user.Id.ToString()], out var n) ? n : 0;

        private static void WarnSettings(HarnessChat chat, int max, string action)
        {
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = max.ToString();
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "type"] = action;
        }

        private static async Task<string[]> WarnOps(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>(
                "SELECT warning_type || ' ' || operation || ' ' || quantity FROM warning_events ORDER BY warning_event_id")).ToArray();
        }

        [TestMethod]
        public async Task TheUserMenusWarnButton_IsTheSameWarnAsTheCommand()
        {
            // It used to be a copy that left prewarns alone and never counted the lifetime warns.
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 5, "ban");
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "rude");
            await chat.SendAsync(admin, "/prewarn", replyTo: post);

            await chat.SendAsync(admin, $"/user {member.Id}");
            var menu = chat.Telegram.SentMessages.Last(m => m.ReplyMarkup?.InlineKeyboard
                .SelectMany(r => r).Any(b => b.CallbackData?.StartsWith("userbuttonwarnuser:") == true) == true);
            await chat.PressAsync(admin, menu, "userbuttonwarnuser:");

            Assert.AreEqual((2L, 0L, 2L), (Count(chat, "warns", member), Count(chat, "prewarns", member), Count(chat, "totalWarns", member)),
                "the prewarn became a warn, and both count towards the lifetime warns");
            var warn = seam.Results.Single(r => r.Applied.Kind == ActionKind.Warn);
            Assert.AreEqual((ActionSource.Button, (long?)admin.Id, 1L), (warn.Applied.Source, warn.Applied.ActorId, warn.Converted));
            CollectionAssert.AreEqual(new[] { "prewarning add 1", "prewarning remove 1", "standard add 2" }, await WarnOps(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABanAtTheLimit_ThatTelegramRefuses_KeepsTheWarns_AndSaysWhy()
        {
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 2, "ban");
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "rude");
            await chat.SendAsync(admin, "/warn", replyTo: post);
            // Promoted a moment ago: the bot's admin list does not know yet, Telegram does and refuses the ban.
            chat.Telegram.SetMember(chat.ChatId, member.Id, MemberState.Administrator);

            await chat.SendAsync(admin, "/warn", replyTo: post);

            var warn = seam.Results.Last(r => r.Applied.Kind == ActionKind.Warn);
            Assert.AreEqual((ActionOutcome.Refused, ActionSource.Escalation), (warn.Escalation.Outcome, warn.Escalation.Applied.Source));
            Assert.AreEqual(2, Count(chat, "warns", member), "the warns stay");
            CollectionAssert.AreEqual(new[] { "standard add 1", "standard add 1" }, await WarnOps(chat), "no reset");
            StringAssert.Contains(chat.Replies.Last(), "not banned");
            StringAssert.Contains(chat.Replies.Last(), "I can not kick or ban an admin");
            Assert.AreEqual(MemberState.Administrator, chat.Telegram.GetMember(chat.ChatId, member.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AKickAtTheLimit_ThatTelegramRefuses_KeepsTheWarnsToo()
        {
            // The kick used to be announced and the warns reset whatever Telegram said.
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 1, "kick");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "rude");
            await chat.SendAsync(admin, "/user 1"); // the admin list is loaded
            chat.Telegram.SetMember(chat.ChatId, member.Id, MemberState.Administrator);

            await chat.SendAsync(admin, "/warn", replyTo: post);

            Assert.AreEqual(1, Count(chat, "warns", member));
            StringAssert.Contains(chat.Replies.Last(), "not kicked");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABanAtTheLimit_ThatGoesThrough_ResetsTheWarnsAsBefore()
        {
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 1, "ban");
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/warn", replyTo: post);

            var warn = seam.Results.Single(r => r.Applied.Kind == ActionKind.Warn);
            Assert.IsTrue(warn.Escalation.Succeeded);
            Assert.AreSame(warn.Warning, warn.Escalation.Applied.Trigger, "the ban points at the warn");
            Assert.AreEqual(0, Count(chat, "warns", member));
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
            StringAssert.Contains(chat.Replies.Last(), "banned");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RemoveWarn_ByIdOrUsername_WithAReasonAfterIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 5, "ban");
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member", username: "member_one");
            var post = await chat.SendAsync(member, "rude");
            await chat.SendAsync(admin, "/warn", replyTo: post);
            await chat.SendAsync(admin, "/warn", replyTo: post);
            await chat.SendAsync(admin, "/warn", replyTo: post);

            await chat.SendAsync(admin, $"/removewarn {member.Id} appeal accepted");
            Assert.AreEqual(2, Count(chat, "warns", member));
            StringAssert.Contains(chat.Replies.Last(), "appeal accepted");

            await chat.SendAsync(admin, "/resetwarns @member_one wrong user");
            Assert.AreEqual(0, Count(chat, "warns", member));
            StringAssert.Contains(chat.Replies.Last(), "wrong user");

            var removals = seam.Results.Where(r => r.Applied.Kind is ActionKind.RemoveWarn or ActionKind.ResetWarns).ToArray();
            Assert.AreEqual(2, removals.Length);
            Assert.IsTrue(removals.All(r => r.Applied.Source == ActionSource.Command && r.Applied.ActorId == admin.Id
                                            && r.Applied.TargetId == member.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RemoveWarn_WithoutAReplyOrAMember_SaysHowToUseIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);

            await chat.SendAsync(admin, "/removewarn appeal accepted");

            Assert.AreEqual("Please reply to someone to use this command", chat.Replies.Last());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheBotsOwnWarns_SayWhereTheyCameFrom()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no"; // "yes" switches it off
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = "5";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = "warn";
            WarnSettings(chat, 5, "ban");
            using var seam = new Recorder();
            var flooder = chat.Add("Flooder");

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            var warn = seam.Results.Single(r => r.Applied.Kind == ActionKind.Warn);
            Assert.AreEqual((ActionSource.Flood, (long?)null, "Flood"), (warn.Applied.Source, warn.Applied.ActorId, warn.Applied.Reason));
            Assert.AreEqual(1, Count(chat, "warns", flooder));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdmin_IsNeverWarned()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var post = await chat.SendAsync(other, "hi");
            var before = chat.Replies.Count;

            await chat.SendAsync(admin, "/warn", replyTo: post);

            Assert.AreEqual(ActionOutcome.Refused, seam.Results.Single().Outcome);
            Assert.AreEqual(0, Count(chat, "warns", other));
            Assert.AreEqual(before, chat.Replies.Count, "nothing said, as before");
            AssertNoErrors(chat);
        }
    }
}
