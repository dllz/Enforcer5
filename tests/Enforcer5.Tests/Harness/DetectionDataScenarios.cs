using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 14: new members' features, what they point at, channel and website reputation, implicit labels and
    /// retention. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class DetectionDataScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static Message Linked(HarnessChat chat, HarnessUser from, string text)
        {
            var message = chat.Text(from, text);
            message.Entities = Regex.Matches(text, @"https?://\S+")
                .Select(m => new MessageEntity { Type = MessageEntityType.Url, Offset = m.Index, Length = m.Length }).ToArray();
            return message;
        }

        private static async Task Flush(HarnessChat chat)
        {
            await chat.SettleAsync();
            await DetectionRecorder.FlushAsync();
        }

        private static Task GoLive(HarnessChat chat) =>
            chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });

        [TestMethod]
        public async Task ANewMember_IsDescribedByFeatures_NeverByName()
        {
            await using var chat = await HarnessChat.StartAsync();
            var newcomer = chat.Add("Ann 🌸 Official", username: "ann_x", languageCode: "DE");
            chat.Deliver(chat.MemberJoined(newcomer, inviteLink: "https://t.me/+abc", linkName: "Spring promo"));
            await chat.SettleAsync();
            await chat.AdvanceAsync(TimeSpan.FromSeconds(42));
            await chat.PostAsync(Linked(chat, newcomer, "deals at https://www.Shop.example/x and https://shop.example/y"));
            await Flush(chat);

            await using var connection = await chat.Database.OpenConnectionAsync();
            var row = await connection.QuerySingleAsync(@"
SELECT s.* FROM member_signals s JOIN telegram_accounts a ON a.account_id = s.account_id WHERE a.telegram_user_id = @Id",
                new { newcomer.Id });
            Assert.AreEqual("link", (string)row.join_source);
            Assert.AreEqual("Spring promo", (string)row.invite_link_name);
            Assert.AreEqual(true, (bool)row.has_username);
            Assert.AreEqual("de", (string)row.language_code);
            Assert.AreEqual("latin", (string)row.name_script);
            Assert.AreEqual(true, (bool)row.name_emoji);
            Assert.AreEqual(true, (bool)row.name_staff_word);
            Assert.AreEqual("text", (string)row.first_post_type);
            Assert.AreEqual(42, (int)row.first_post_delay_seconds);
            Assert.AreEqual(2, (int)row.links);
            Assert.AreNotEqual(0, (int)row.active_hours);
            CollectionAssert.AreEqual(new[] { "shop.example" }, (await connection.QueryAsync<string>(
                "SELECT value FROM member_pointers WHERE kind = 'domain'")).ToArray(), "one website, by its host");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWebsiteSpammersAdvertisedInTwoGroups_GetsABadReputation_AndTheNextNewcomerTheSign()
        {
            await using var chat = await HarnessChat.StartAsync();
            var other = chat.Group(-1009999999, "Second group");
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.SetMember(other.ChatId, admin.Id, MemberState.Administrator);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            other.Telegram.SetMember(other.ChatId, second.Id, MemberState.Member);

            var spam1 = await chat.PostAsync(Linked(chat, first, "earn now https://pump.example/a"));
            var spam2 = await other.PostAsync(Linked(other, second, "earn now https://pump.example/b"));
            await chat.SendAsync(admin, "/ban spam", replyTo: spam1);
            await other.SendAsync(admin, "/ban spam", replyTo: spam2);
            await Flush(chat);

            await DetectionData.NightlyAsync(force: true);
            Assert.IsTrue(Reputation.IsBad("domain", "pump.example"));

            var third = chat.Add("Third");
            await chat.PostAsync(Linked(chat, third, "look https://pump.example/c"));
            await Flush(chat);
            await using var connection = await chat.Database.OpenConnectionAsync();
            var signs = (await connection.QueryAsync<string>(@"
SELECT d.sign FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id WHERE a.telegram_user_id = @Id",
                new { third.Id })).ToArray();
            CollectionAssert.Contains(signs, "channel_reputation");
            StringAssert.Contains(Commands.ReputationText("pump.example",
                await Enforcer5.Data.Repositories.DetectionData.GetReputationAsync("domain", "pump.example")), "removed or muted for spam or abuse: 2 in 2 groups");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AGameRuleBan_SaysNothingAboutTheWebsite()
        {
            await using var chat = await HarnessChat.StartAsync();
            var other = chat.Group(-1009999999, "Second group");
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.SetMember(other.ChatId, admin.Id, MemberState.Administrator);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            other.Telegram.SetMember(other.ChatId, second.Id, MemberState.Member);
            var m1 = await chat.PostAsync(Linked(chat, first, "my stream https://twitch.example/me"));
            var m2 = await other.PostAsync(Linked(other, second, "my stream https://twitch.example/me2"));
            await chat.SendAsync(admin, "/warn", replyTo: m1);
            await other.SendAsync(admin, "/warn", replyTo: m2);
            await Flush(chat);

            await DetectionData.NightlyAsync(force: true);
            Assert.IsFalse(Reputation.IsBad("domain", "twitch.example"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Retention_RemovesTheAccount_FromRowsOlderThanTheGroupsChoice()
        {
            await using var chat = await HarnessChat.StartAsync();
            await chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { RetentionDays = 90 } });
            var newcomer = chat.Add("Newcomer");
            await chat.PostAsync(Linked(chat, newcomer, "https://shop.example"));
            await Flush(chat);
            await using var connection = await chat.Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE detections SET detected_at = detected_at - interval '100 days';
UPDATE member_signals SET updated_at = updated_at - interval '100 days';
UPDATE member_pointers SET last_seen_at = last_seen_at - interval '80 days'");

            await DetectionData.NightlyAsync(force: true);

            Assert.AreEqual(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM detections WHERE account_id IS NOT NULL"));
            Assert.IsTrue(await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM detections") > 0, "kept, anonymous");
            Assert.AreEqual(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM member_signals WHERE account_id IS NOT NULL"));
            Assert.AreEqual(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM member_pointers WHERE account_id IS NOT NULL"),
                "80 days is within 90");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ANewMemberWhoBecameEstablished_IsLabelledFine_AndAGlobalBanBad()
        {
            await using var chat = await HarnessChat.StartAsync();
            var good = chat.Add("Good");
            var bad = chat.Add("Bad");
            await chat.SendAsync(good, "hello there");
            await chat.SendAsync(bad, "hello there too");
            await Flush(chat);
            chat.Hashes[$"{chat.ChatId}:users:{good.Id}", "msgs"] = "5000";
            chat.Hashes[$"{chat.ChatId}:users:{good.Id}", "first"] = "1"; // talking here long enough
            await using var connection = await chat.Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
UPDATE member_signals SET first_seen_at = first_seen_at - interval '40 days'
WHERE account_id = (SELECT account_id FROM telegram_accounts WHERE telegram_user_id = @Id)", new { good.Id });
            await connection.ExecuteAsync(@"
INSERT INTO global_sanctions (account_id, kind, status, reason, imposed_at)
SELECT account_id, 'ban', 'active', 'spam', now() FROM telegram_accounts WHERE telegram_user_id = @Id", new { bad.Id });

            await DetectionData.NightlyAsync(force: true);
            await DetectionData.NightlyAsync(force: true);

            var labels = (await connection.QueryAsync<(long, string, string)>(@"
SELECT a.telegram_user_id, l.label, l.source FROM member_labels l JOIN telegram_accounts a ON a.account_id = l.account_id
ORDER BY l.label_id")).ToArray();
            CollectionAssert.AreEquivalent(new[] { (good.Id, "fine", "established"), (bad.Id, "bad", "global") }, labels, "once each");
            AssertNoErrors(chat);
        }
    }
}
