using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Actions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// /batch (<c>docs/action-seam.md</c>, step G): the preview, any admin running it, one member at a time through
    /// the seam, Stop, a restart halfway, a rule blocking some, and Detection's labels. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class BatchScenarios
    {
        private const long LogChannel = -1007777;

        private sealed class Recorder : IDisposable
        {
            internal readonly ConcurrentQueue<ActionResult> Results = new();
            internal Recorder() => ActionSeam.Ran += Add;
            private void Add(ActionResult result) => Results.Enqueue(result);
            public void Dispose() => ActionSeam.Ran -= Add;
        }

        private sealed class BlockSome : IActionHook, IDisposable
        {
            private readonly long[] _blocked;
            internal BlockSome(params long[] blocked)
            {
                _blocked = blocked;
                ActionHooks.Add(this);
            }
            public string Name => "VIP rule";
            public bool Wants(ActionRequest request) => request.Source == ActionSource.Batch;
            public Task<HookVerdict> BeforeAsync(ActionRequest request) =>
                Task.FromResult(_blocked.Contains(request.TargetId) ? HookVerdict.Block("VIP") : HookVerdict.Allow);
            public void Dispose() => ActionHooks.Remove(this);
        }

        [TestCleanup]
        public void Cleanup() => ActionHooks.ResetForTests();

        private static async Task<HarnessChat> StartAsync()
        {
            var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
            return chat;
        }

        private static HarnessUser[] Members(HarnessChat chat, int count) =>
            Enumerable.Range(0, count).Select(i => chat.Add($"Member{i}")).ToArray();

        private static string List(params HarnessUser[] users) => string.Join("\n", users.Select(u => u.Id));

        private static Message LastInGroup(HarnessChat chat) => chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId);

        private static Message Preview(HarnessChat chat) =>
            chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId && m.ReplyMarkup != null &&
                                                 m.ReplyMarkup.InlineKeyboard.SelectMany(r => r).Any(b => b.CallbackData?.Contains(":run:") == true));

        private static string Text(HarnessChat chat, int messageId) =>
            chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId && m.MessageId == messageId).Text;

        private static async Task<(long Id, string Status, long ConfirmedBy)> BatchAsync(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.QuerySingleAsync<(long, string, long)>(@"
SELECT b.batch_id, b.status, a.telegram_user_id FROM batches b
JOIN telegram_accounts a ON a.account_id = b.confirmed_by_account_id ORDER BY b.batch_id DESC LIMIT 1");
        }

        private static async Task<string[]> LabelSourcesAsync(HarnessChat chat, long userId)
        {
            await chat.SettleAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>(@"
SELECT l.source FROM member_labels l JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE a.telegram_user_id = @UserId", new { UserId = userId })).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task ThePreview_LeavesOutDuplicatesAndAdmins_AndAnotherAdminRunsIt()
        {
            await using var chat = await StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var members = Members(chat, 3);
            var list = await chat.SendAsync(admin, $"{List(members)}\n{members[0].Id}\n{other.Id}\n{HarnessChat.BotUser.Id}");

            await chat.SendAsync(admin, "/batch ban spam ring", replyTo: list);

            var preview = Preview(chat);
            StringAssert.Contains(preview.Text, "ban spam ring");
            StringAssert.Contains(preview.Text, "<b>3</b>");
            StringAssert.Contains(preview.Text, "1 duplicates, 2 admins");
            StringAssert.Contains(preview.Text, $"Will be done for: <code>{string.Join(", ", members.Select(m => m.Id))}</code>", "who, by id");
            StringAssert.Contains(preview.Text, $"Admins, left out: <code>{other.Id}, {HarnessChat.BotUser.Id}</code>");
            Assert.IsTrue(members.All(m => chat.Telegram.GetMember(chat.ChatId, m.Id) == MemberState.Member), "nothing before Run");

            await chat.PressAsync(other, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");

            Assert.IsTrue(members.All(m => chat.Telegram.GetMember(chat.ChatId, m.Id) == MemberState.Kicked));
            var batch = await BatchAsync(chat);
            Assert.AreEqual(("done", other.Id), (batch.Status, batch.ConfirmedBy), "the admin who confirmed is recorded");
            var bans = seam.Results.Where(r => r.Applied.Kind == ActionKind.Ban).Select(r => r.Applied).ToArray();
            Assert.IsTrue(bans.Length == 3 && bans.All(b => b.Source == ActionSource.Batch && b.ActorId == admin.Id &&
                                                            b.BatchId == batch.Id && b.Reason == "spam ring"));
            StringAssert.Contains(Text(chat, preview.MessageId), "is finished");
            StringAssert.Contains(Text(chat, preview.MessageId), "Done: 3");
            foreach (var member in members)
                CollectionAssert.AreEqual(new[] { "batch" }, await LabelSourcesAsync(chat, member.Id), "Detection sees the batch");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            var logs = chat.Telegram.SentMessages.Where(m => m.Chat.Id == LogChannel).ToArray();
            Assert.AreEqual(1, logs.Length, "one line for the whole batch");
            StringAssert.Contains(logs[0].Text, "Done 3");
            StringAssert.Contains(logs[0].Text, $"Done: <code>{string.Join(", ", members.Select(m => m.Id))}</code>", "the log says who");
            Assert.AreEqual(0, chat.Telegram.SentFiles.Count, "short lists need no file");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheProgressAndTheSummary_AreHtml_AndWhatTheAdminTyped_IsEscapedOnce()
        {
            await using var chat = await StartAsync();
            chat.Telegram.ParsesHtml = true;
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Members(chat, 2);
            var list = await chat.SendAsync(admin, List(members));

            await chat.SendAsync(admin, "/batch kick bye <3 & see you", replyTo: list);

            var preview = Preview(chat);
            StringAssert.Contains(preview.Text, "bye &lt;3 &amp; see you", "a '<' in the reason would make Telegram refuse the preview");
            await chat.PressAsync(admin, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));

            var summary = Text(chat, preview.MessageId);
            StringAssert.Contains(summary, "is finished");
            StringAssert.Contains(summary, "bye &lt;3 &amp; see you");
            Assert.AreEqual(Telegram.Bot.Types.Enums.ParseMode.Html, chat.Telegram.ParseModeOf(chat.ChatId, preview.MessageId),
                "the summary replaces the preview by an edit, and its <b> must not show as text");
            StringAssert.Contains(chat.Telegram.SentMessages.Single(m => m.Chat.Id == LogChannel).Text, "bye &lt;3");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheSummaryAndTheLog_SayWhoThePreviewLeftOut_AndTheProgressCountsOnlyThoseRun()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var members = Members(chat, 3);
            foreach (var warned in members.Take(2))
                await chat.SendAsync(admin, "/warn", replyTo: await chat.SendAsync(warned, "hi"));
            var list = await chat.SendAsync(admin, $"{List(members)}\n{other.Id}");

            await chat.SendAsync(admin, "/batch resetwarns reward", replyTo: list);
            var preview = Preview(chat);
            StringAssert.Contains(preview.Text, $"Nothing to do: <code>{members[2].Id}</code>");
            await chat.PressAsync(admin, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));

            var batch = await Data.Repositories.Batches.GetAsync((await BatchAsync(chat)).Id);
            Assert.AreEqual((2, 0), (batch.Total, batch.Skipped), "those left out are items, but not counted in Done X of Y");
            var summary = Text(chat, preview.MessageId);
            StringAssert.Contains(summary, "Done: 2");
            StringAssert.Contains(summary, "Left out: 2");
            StringAssert.Contains(summary, $"Done: <code>{members[0].Id}, {members[1].Id}</code>", "the preview's lists are gone: the summary says who");
            StringAssert.Contains(summary, $"Admins, left out: <code>{other.Id}</code>");
            StringAssert.Contains(summary, $"Nothing to do: <code>{members[2].Id}</code>");
            var log = chat.Telegram.SentMessages.Last(m => m.Chat.Id == LogChannel).Text;
            StringAssert.Contains(log, "left out 2");
            StringAssert.Contains(log, $"Admins, left out: <code>{other.Id}</code>");
            StringAssert.Contains(log, $"Nothing to do: <code>{members[2].Id}</code>");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ListsTooLongForTheMessage_AreCut_AndSentWholeAsAFile()
        {
            var (previewBudget, logBudget) = (BatchLists.PreviewBudget, BatchLists.LogBudget);
            BatchLists.PreviewBudget = BatchLists.LogBudget = 30;
            try
            {
                await using var chat = await StartAsync();
                var admin = chat.Add("Admin", MemberState.Administrator);
                var members = Members(chat, 6);
                using var rule = new BlockSome(members[5].Id);
                var list = await chat.SendAsync(admin, $"{List(members)}\n{admin.Id}");

                await chat.SendAsync(admin, "/batch kick", replyTo: list);

                var preview = Preview(chat);
                StringAssert.Contains(preview.Text, "more");
                var previewFile = chat.Telegram.SentFiles.Single();
                Assert.AreEqual((chat.ChatId, "batch-preview.csv", preview.MessageId), (previewFile.ChatId, previewFile.FileName, previewFile.ReplyTo));
                var rows = Encoding.UTF8.GetString(previewFile.Content).Trim().Split('\n');
                Assert.AreEqual("id,outcome", rows[0]);
                CollectionAssert.AreEqual(members.Select(m => $"{m.Id},will be done").Append($"{admin.Id},\"admin, left out\"").ToArray(),
                    rows.Skip(1).ToArray());

                await chat.PressAsync(admin, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");
                await chat.AdvanceAsync(TimeSpan.FromSeconds(5));

                var (id, _, _) = await BatchAsync(chat);
                var summaryFile = chat.Telegram.SentFiles.Single(f => f.ChatId == chat.ChatId && f.FileName != "batch-preview.csv");
                Assert.AreEqual(($"batch-{id}.csv", preview.MessageId), (summaryFile.FileName, summaryFile.ReplyTo),
                    "the summary's lists are cut too: the whole of them replies to it");
                StringAssert.Contains(Text(chat, preview.MessageId), "more");

                var log = chat.Telegram.SentMessages.Single(m => m.Chat.Id == LogChannel);
                StringAssert.Contains(log.Text, "more");
                var logFile = chat.Telegram.SentFiles.Last();
                Assert.AreEqual(LogChannel, logFile.ChatId);
                Assert.AreEqual($"batch-{id}.csv", logFile.FileName);
                var logRows = Encoding.UTF8.GetString(logFile.Content).Trim().Split('\n');
                Assert.AreEqual("id,member,outcome,reason", logRows[0]);
                Assert.AreEqual(7, logRows.Length - 1, "every member of the run, and the admin the preview left out");
                Assert.IsTrue(logRows.Single(r => r.StartsWith(admin.Id + ",")).EndsWith(",admin,"), string.Join("\n", logRows));
                Assert.IsTrue(members.Take(5).All(m => logRows.Single(r => r.StartsWith(m.Id + ",")).EndsWith(",done,")), string.Join("\n", logRows));
                Assert.IsTrue(logRows.Single(r => r.StartsWith(members[5].Id + ",")).EndsWith(",blocked,VIP rule"), logRows.Last());
                AssertNoErrors(chat);
            }
            finally
            {
                (BatchLists.PreviewBudget, BatchLists.LogBudget) = (previewBudget, logBudget);
            }
        }

        [TestMethod]
        public async Task AMember_CannotRunIt_AndAnExpiredPreview_IsNotRun()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Nosy");
            var targets = Members(chat, 2);
            var list = await chat.SendAsync(admin, List(targets));
            await chat.SendAsync(admin, "/batch kick", replyTo: list);
            var preview = Preview(chat);

            await chat.PressAsync(member, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");
            Assert.IsTrue(targets.All(t => chat.Telegram.GetMember(chat.ChatId, t.Id) == MemberState.Member), "not an admin: nothing");

            await chat.AdvanceAsync(Batches.PreviewFor + TimeSpan.FromMinutes(1));
            await chat.PressAsync(admin, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");
            Assert.IsTrue(targets.All(t => chat.Telegram.GetMember(chat.ChatId, t.Id) == MemberState.Member), "expired: nothing");
            StringAssert.Contains(Text(chat, preview.MessageId), "no longer open");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ATempban_FromAFile_TakesItsDuration()
        {
            await using var chat = await StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Members(chat, 2);
            var file = await chat.PostAsync(chat.Document(admin, "raiders.csv",
                Encoding.UTF8.GetBytes($"id,name\n{members[0].Id},a\n{members[1].Id},b\n")));

            await chat.SendAsync(admin, "/batch tempban 2d raid", replyTo: file);
            await chat.PressAsync(admin, Preview(chat), $"{Batches.Trigger}:{chat.ChatId}:run:");

            var tempbans = seam.Results.Where(r => r.Applied.Kind == ActionKind.TempBan).Select(r => r.Applied).ToArray();
            Assert.AreEqual(2, tempbans.Length);
            Assert.IsTrue(tempbans.All(t => t.UntilUtc is { } until && Math.Abs((until - chat.Clock.GetUtcNow().UtcDateTime).TotalDays - 2) < 0.01
                                           && t.Reason == "raid"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Stop_EndsIt_AndTheRestIsLeftOut()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Members(chat, 20);
            var list = await chat.SendAsync(admin, List(members));
            await chat.SendAsync(admin, "/batch ban", replyTo: list);
            var preview = Preview(chat);
            // Telegram slow on bans: each member takes the seam's three seconds.
            chat.Telegram.StartStorm(c => c.Method == "banChatMember", TimeSpan.FromMinutes(30), retryAfter: 600);

            chat.DeliverPress(admin, preview, preview.ReplyMarkup.InlineKeyboard.SelectMany(r => r).First().CallbackData);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
            var batch = await BatchAsync(chat);
            await chat.PressDataAsync(admin, preview, $"{Batches.Trigger}:{chat.ChatId}:stop:{batch.Id}");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual("stopped", (await BatchAsync(chat)).Status);
            var summary = Text(chat, preview.MessageId);
            StringAssert.Contains(summary, "was stopped");
            await using var connection = await chat.Database.OpenConnectionAsync();
            var outcomes = (await connection.QueryAsync<(string, int)>(
                "SELECT outcome, count(*)::int FROM batch_items WHERE batch_id = @Id GROUP BY outcome", new { batch.Id })).ToDictionary(x => x.Item1, x => x.Item2);
            Assert.IsTrue(outcomes.GetValueOrDefault("done") is > 0 and < 20, string.Join(",", outcomes));
            Assert.AreEqual(20, outcomes.Values.Sum(), "every item has an outcome");
            Assert.IsTrue(outcomes.GetValueOrDefault("skipped") > 0);
        }

        [TestMethod]
        public async Task ARestartHalfway_CarriesOn()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Members(chat, 12);
            var list = await chat.SendAsync(admin, List(members));
            await chat.SendAsync(admin, "/batch mute", replyTo: list);
            var preview = Preview(chat);
            chat.Telegram.StartStorm(c => c.Method == "restrictChatMember", TimeSpan.FromSeconds(20), retryAfter: 5);

            chat.DeliverPress(admin, preview, preview.ReplyMarkup.InlineKeyboard.SelectMany(r => r).First().CallbackData);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(9));
            Assert.AreEqual("running", (await BatchAsync(chat)).Status);
            await chat.CrashAndRestartAsync();
            await chat.CompleteAsync();
            await chat.AdvanceAsync(TimeSpan.FromMinutes(2));

            Assert.AreEqual("done", (await BatchAsync(chat)).Status);
            Assert.IsTrue(members.All(m => chat.Telegram.GetMember(chat.ChatId, m.Id) == MemberState.Restricted));
            StringAssert.Contains(Text(chat, preview.MessageId), "Done: 12");
        }

        [TestMethod]
        public async Task ARule_BlocksSome_AndTheSummarySaysWhich()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Members(chat, 4);
            using var rule = new BlockSome(members[1].Id, members[2].Id);
            var list = await chat.SendAsync(admin, List(members));

            await chat.SendAsync(admin, "/batch kick", replyTo: list);
            var preview = Preview(chat);
            await chat.PressAsync(admin, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, members[1].Id));
            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, members[0].Id));
            var summary = Text(chat, preview.MessageId);
            StringAssert.Contains(summary, "Blocked by a rule: 2");
            StringAssert.Contains(summary, "blocked by VIP rule: 2");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task NoIds_OrAnUnknownCommand_IsExplained()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var list = await chat.SendAsync(admin, "nobody here");

            await chat.SendAsync(admin, "/batch ban", replyTo: list);
            StringAssert.Contains(LastInGroup(chat).Text, "no user ids");
            await chat.SendAsync(admin, "/batch explode", replyTo: list);
            StringAssert.Contains(LastInGroup(chat).Text, "/batch");
            await chat.SendAsync(admin, "/batch tempban 2x", replyTo: list);
            StringAssert.Contains(LastInGroup(chat).Text, "duration");
            AssertNoErrors(chat);
        }
    }
}
