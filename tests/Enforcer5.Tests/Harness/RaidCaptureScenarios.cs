using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The raid building blocks in PostgreSQL (rollout step 8), driven through real joins and
    /// messages: who joined how, member counts, fingerprints, and the lookup for everyone resembling
    /// a few known raiders. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class RaidCaptureScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private sealed class JoinRow
        {
            public long TelegramUserId { get; set; }
            public string Source { get; set; }
            public string InviteLink { get; set; }
            public string InviteLinkName { get; set; }
            public long? AddedBy { get; set; }
        }

        [TestMethod]
        public async Task Joins_AreKeptWithHowTheyCameIn_OnceEach()
        {
            await using var chat = await HarnessChat.StartAsync();
            var adder = chat.Add("Adder");
            var viaLink = chat.Add("ViaLink", MemberState.Left);
            var added = chat.Add("Added", MemberState.Left);
            var silent = chat.Add("Silent", MemberState.Left);

            // The member update and the service message for the same join.
            chat.Deliver(chat.MemberJoined(viaLink, "https://t.me/+abc", "ads campaign"));
            chat.Deliver(chat.Join(viaLink));
            chat.Deliver(chat.Join(added, addedBy: adder));
            chat.Deliver(chat.Join(silent));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));

            await using var connection = await chat.Database.OpenConnectionAsync();
            var joins = (await connection.QueryAsync<JoinRow>(@"
SELECT a.telegram_user_id AS TelegramUserId, j.source AS Source, j.invite_link AS InviteLink,
       j.invite_link_name AS InviteLinkName, adder.telegram_user_id AS AddedBy
FROM group_joins j JOIN telegram_accounts a ON a.account_id = j.account_id
LEFT JOIN telegram_accounts adder ON adder.account_id = j.added_by_account_id")).ToDictionary(j => j.TelegramUserId);

            Assert.AreEqual(3, joins.Count, "one row per join");
            Assert.AreEqual(("link", "https://t.me/+abc", "ads campaign"), (joins[viaLink.Id].Source, joins[viaLink.Id].InviteLink, joins[viaLink.Id].InviteLinkName));
            Assert.AreEqual(("added", (long?)adder.Id), (joins[added.Id].Source, joins[added.Id].AddedBy));
            Assert.AreEqual("unknown", joins[silent.Id].Source);
            Assert.IsNotNull(chat.Hashes[$"{chat.ChatId}:users:{silent.Id}", "joined"], "kept in Redis too, for when PostgreSQL is not there");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Members_StartFromTheirRedisCount_AndTheirFirstMessagesAreFingerprinted()
        {
            await using var chat = await HarnessChat.StartAsync();
            var regular = chat.Add("Regular");
            chat.Hashes[$"{chat.ChatId}:users:{regular.Id}", "msgs"] = "500"; // talking here for years
            var newcomer = chat.Add("Newcomer");

            await chat.SendAsync(regular, "morning all");
            for (var i = 0; i < 25; i++) chat.Deliver(chat.Text(newcomer, $"message number {i} https://t.me/spam"));
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));

            await using var connection = await chat.Database.OpenConnectionAsync();
            var counts = (await connection.QueryAsync<(long Id, long Count)>(@"
SELECT a.telegram_user_id, m.message_count FROM group_members m JOIN telegram_accounts a USING (account_id)")).ToDictionary(r => r.Id, r => r.Count);
            Assert.AreEqual(501L, counts[regular.Id]);
            Assert.AreEqual(25L, counts[newcomer.Id]);

            var prints = (await connection.QueryAsync<(long Id, string Reason, bool Bare, string Script)>(@"
SELECT a.telegram_user_id, f.sample_reason, f.bare_link, f.script FROM message_fingerprints f JOIN telegram_accounts a USING (account_id)")).ToList();
            Assert.AreEqual(20, prints.Count(p => p.Id == newcomer.Id && p.Reason == "first"), "their first twenty");
            Assert.IsTrue(prints.Count(p => p.Id == newcomer.Id) <= 25);
            Assert.IsTrue(prints.Count(p => p.Id == regular.Id) <= 1, "a regular is only sampled");
            Assert.IsTrue(prints.Where(p => p.Id == newcomer.Id).All(p => p.Script == "latin"));
            Assert.AreEqual(TrustLevel.Established, await MemberTrust.OfAsync(chat.ChatId, regular.Id));
            Assert.AreEqual(TrustLevel.New, await MemberTrust.OfAsync(chat.ChatId, newcomer.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ThreeKnownRaiders_FindTheRestOfTheRaid_AndNotTheRegulars()
        {
            await using var chat = await HarnessChat.StartAsync();
            var regulars = Enumerable.Range(0, 5).Select(i => chat.Add($"Regular{i}")).ToList();
            foreach (var r in regulars)
            {
                chat.Hashes[$"{chat.ChatId}:users:{r.Id}", "msgs"] = "300";
                await chat.SendAsync(r, "hi, how is everyone");
            }
            await chat.AdvanceAsync(TimeSpan.FromHours(2));
            var since = chat.Clock.GetUtcNow();

            // Ten accounts in two minutes through one invite link, all posting the same picture.
            var raiders = Enumerable.Range(0, 10).Select(i => chat.Add($"Crypto Bonus {i}", MemberState.Left, languageCode: "fa")).ToList();
            foreach (var r in raiders)
            {
                chat.Deliver(chat.MemberJoined(r, "https://t.me/+leaked", "old link"));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
                chat.Deliver(chat.Photo(r, uniqueId: "same-scam-picture", caption: "Free crypto here t.me/scam"));
            }
            await chat.AdvanceAsync(TimeSpan.FromMinutes(2));

            var similar = await Repositories.RaidCapture.FindSimilarAsync(chat.ChatId, raiders.Take(3).Select(r => r.Id).ToList(), since);

            var found = similar.Select(s => s.TelegramUserId).ToHashSet();
            Assert.IsTrue(raiders.Skip(3).All(r => found.Contains(r.Id)), "the other seven");
            Assert.IsFalse(regulars.Any(r => found.Contains(r.Id)), "no regular");
            var one = similar.First(s => s.TelegramUserId == raiders[5].Id);
            foreach (var reason in new[] { "same_link", "same_media", "joined_close", "silent_before_join", "same_language", "same_text", "similar_name" })
                CollectionAssert.Contains(one.Reasons.ToList(), reason);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task NightlyNorms_ScaleWithHowBusyTheGroupIs()
        {
            await using var chat = await HarnessChat.StartAsync();
            for (var i = 0; i < 12; i++)
            {
                var member = chat.Add($"Member{i}");
                chat.Hashes[$"{chat.ChatId}:users:{member.Id}", "msgs"] = (2000 + i).ToString();
                await chat.SendAsync(member, "hello");
            }

            await Repositories.RaidCapture.RecomputeNormsAsync(DateTimeOffset.UtcNow.AddDays(1));
            var norms = (await Repositories.RaidCapture.GetNormsAsync()).Single(n => n.TelegramChatId == chat.ChatId);

            Assert.AreEqual((12, 1000, 3), (norms.ActiveMembers, norms.EstablishedMessages, norms.EstablishedDays),
                "a busy group: capped at 1000 messages; everyone first seen a day ago, so the shortest stay");
            AssertNoErrors(chat);
        }
    }
}
