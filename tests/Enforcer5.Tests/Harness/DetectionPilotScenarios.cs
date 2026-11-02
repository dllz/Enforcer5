using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection's pilot (Daniel, 2026-11-02): it works only in groups Daniel is in. Elsewhere the new signs are not
    /// looked for and nothing is reported or recorded, while the old checks and the filters work as before.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class DetectionPilotScenarios
    {
        [TestCleanup]
        public void Cleanup() => DetectionPilot.ResetForTests();

        private static async Task<HarnessChat> StartAsync()
        {
            var chat = await HarnessChat.StartAsync();
            DetectionPilot.ResetForTests(everywhere: false);
            // Live at once, so a sign would act and report.
            await chat.ChangeSettingsAsync(p => p with
            {
                Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) },
                Filters = p.Filters.WithPreset("invite_link", "delete"),
                TextLength = new LengthSettings(true, 100, 50, "kick")
            });
            return chat;
        }

        private static async Task<string[]> SignsAsync(HarnessChat chat)
        {
            await DetectionRecorder.FlushAsync();
            await chat.SettleAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>("SELECT sign FROM detections ORDER BY detection_id")).ToArray();
        }

        private static bool Reported(HarnessChat chat, HarnessUser admin) =>
            chat.Telegram.SentMessages.Any(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true);

        [TestMethod]
        public async Task WithoutDaniel_NoSignIsLookedFor_ButTheOldChecksAndFiltersWork()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Boss", MemberState.Administrator, lastName: "Person");
            var copy = chat.Add("Воss", lastName: "Person"); // Cyrillic В and о
            var spammer = chat.Add("Spammer");
            var talker = chat.Add("Talker");

            await chat.JoinAsync(copy);
            await chat.SendAsync(spammer, "come and join our club at t.me/+cheapfollowers today");
            await chat.SendAsync(talker, new string('a', 150));

            Assert.IsFalse(Reported(chat, admin), "no Detection report");
            CollectionAssert.DoesNotContain(await SignsAsync(chat), "name_impersonation");
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, spammer.Id).Count, "the filter still deletes");
            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, talker.Id), "the length check still kicks");
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task WithDaniel_DetectionWorks_FromTheMomentHeJoins()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Boss", MemberState.Administrator, lastName: "Person");
            var first = chat.Add("Воss", lastName: "Person");
            var second = chat.Add("Bоss", lastName: "Person");
            await chat.JoinAsync(first);
            Assert.IsFalse(Reported(chat, admin));

            var daniel = chat.Add("Daniel", MemberState.Left, id: DetectionPilot.Owner);
            chat.Deliver(chat.MemberJoined(daniel));
            await chat.SettleAsync();
            await chat.JoinAsync(second);

            Assert.IsTrue(Reported(chat, admin), "a report once Daniel is in the group");
            CollectionAssert.Contains(await SignsAsync(chat), "name_impersonation");
            Assert.IsTrue(DetectionPilot.Includes(chat.ChatId));
        }

        [TestMethod]
        public async Task AGroupDanielIsAlreadyIn_IsAskedOnce_AndIncluded()
        {
            await using var chat = await StartAsync();
            chat.Add("Daniel", MemberState.Member, id: DetectionPilot.Owner);

            Assert.IsTrue(await DetectionPilot.IncludesAsync(chat.ChatId));
            var asked = chat.Telegram.Accepted.Count(c => c.Method == "getChatMember" && c.UserId == DetectionPilot.Owner);
            Assert.IsTrue(await DetectionPilot.IncludesAsync(chat.ChatId));
            Assert.AreEqual(asked, chat.Telegram.Accepted.Count(c => c.Method == "getChatMember" && c.UserId == DetectionPilot.Owner),
                "believed, not asked again");
        }
    }
}
