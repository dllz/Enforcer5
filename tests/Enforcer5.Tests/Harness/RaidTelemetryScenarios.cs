using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Raid profiles (rollout step 10): what is recorded, that it holds no ids or text, and that a
    /// recorded raid replays into a fresh group and is caught again. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Raid")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class RaidTelemetryScenarios
    {
        private static void AssertOnly429s(HarnessChat chat)
        {
            var other = chat.Errors.Where(e => !e.Contains("Too Many Requests")).ToList();
            Assert.AreEqual(0, other.Count, "Errors reported:\n" + string.Join("\n", other));
        }

        private static async Task<System.Collections.Generic.List<HarnessUser>> RaidAsync(HarnessChat chat, int count)
        {
            var raiders = Enumerable.Range(0, count).Select(i => chat.Add($"Raider{i}", MemberState.Left)).ToList();
            foreach (var r in raiders)
            {
                chat.Deliver(chat.MemberJoined(r, "https://t.me/+leaked", "old link"));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(1));
                chat.Deliver(chat.Photo(r, uniqueId: "scam-picture", caption: "free crypto t.me/scam"));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
            return raiders;
        }

        [TestMethod]
        public async Task ARaid_IsRecorded_WithoutAnyIdOrText()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            var raiders = await RaidAsync(chat, 20);
            await chat.AdvanceAsync(TimeSpan.FromMinutes(12)); // over, and flushed

            var profile = await Repositories.RaidTelemetry.GetProfileAsync(null, chat.ChatId);

            Assert.IsNotNull(profile);
            Assert.AreEqual(("active", "auto", "quiet"), (profile.PhaseReached, profile.Trigger, profile.EndedBy));
            Assert.IsTrue(profile.LockedAfterMs.HasValue && profile.LockedAfterMs < 5000, $"locked after {profile.LockedAfterMs} ms");
            Assert.AreEqual(20, profile.Events.Where(e => e.Kind == "join").Select(e => e.Actor).Distinct().Count());
            Assert.IsTrue(profile.Events.Any(e => e.Kind == "join" && e.T < 0), "the joins that set it off are in, before its start");
            Assert.IsTrue(profile.Events.Count(e => e.Kind == "message") >= 10);
            Assert.AreEqual(1, profile.Events.Where(e => e.Kind == "message").Select(e => e.Detail["content"]?.ToString()).Distinct().Count(),
                "the same picture is the same content");
            var json = profile.ToJson();
            Assert.IsFalse(raiders.Any(r => json.Contains(r.Id.ToString())), "no account id");
            Assert.IsFalse(json.Contains("scam-picture") || json.Contains("crypto") || json.Contains("t.me/+leaked"), "no content, no link");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ARecordedRaid_ReplaysIntoAFreshGroup_AndIsCaughtAgain()
        {
            RaidProfile profile;
            await using (var original = await HarnessChat.StartAsync())
            {
                original.Add("Admin", MemberState.Administrator);
                RaidGuard.SetMode(original.ChatId, RaidMode.Auto);
                await RaidAsync(original, 20);
                await original.AdvanceAsync(TimeSpan.FromMinutes(12));
                profile = RaidProfile.FromJson((await Repositories.RaidTelemetry.GetProfileAsync(null, original.ChatId)).ToJson());
            }

            await using var chat = await HarnessChat.StartAsync(chatId: -1009998887776, title: "Replay");
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            var joined = await RaidReplay.RunAsync(chat, profile);

            Assert.AreEqual(20, joined.Count);
            Assert.IsTrue(chat.Telegram.ChatPermissions(chat.ChatId)?.CanSendMessages == false, "caught again: locked");
            Assert.IsTrue(joined.All(u => chat.Telegram.GetMember(chat.ChatId, u.Id) is MemberState.Kicked or MemberState.Restricted),
                string.Join(",", joined.GroupBy(u => chat.Telegram.GetMember(chat.ChatId, u.Id)).Select(g => $"{g.Key}={g.Count()}")) +
                " events: " + string.Join(",", profile.Events.GroupBy(e => e.Kind).Select(g => $"{g.Key}={g.Count()}")) +
                " msgs from first-handled: " + string.Join(";", profile.Events.Where(e => e.Kind == "message").Take(3).Select(e => string.Join("|", e.Detail.Select(d => $"{d.Key}={d.Value}")))));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task AnAlertTheAdminsIgnored_IsRecordedAsANearMiss()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await RaidAsync(chat, 16);
            var alert = chat.Telegram.SentMessages.Last(m => m.Text?.Contains("Possible raid") == true);

            await chat.PressAsync(admin, alert, $"raid:{chat.ChatId}:ignore");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));

            var profile = await Repositories.RaidTelemetry.GetProfileAsync(null, chat.ChatId);
            Assert.AreEqual(("alert", "ignored"), (profile.PhaseReached, profile.EndedBy));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task SlashRaidProfile_SendsTheLatestToTheDeveloper()
        {
            await using var chat = await HarnessChat.StartAsync();
            var dev = chat.Add("Dev", MemberState.Administrator, id: Models.Constants.Devs[0]);
            await chat.SendAsync(dev, "/raid");
            await chat.SendAsync(dev, "/raid off");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));

            await chat.SendAsync(dev, "/raidprofile");

            var file = chat.Telegram.Accepted.Single(c => c.Method == "sendDocument");
            Assert.AreEqual(dev.Id, file.ChatId);
            AssertOnly429s(chat);
        }
    }
}
