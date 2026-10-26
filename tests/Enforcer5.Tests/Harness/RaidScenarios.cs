using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Requests;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Raids against a simulated group: detection, human and auto mode, /raid, locking and unlocking,
    /// quarantine, bans, link reports, and all of it under 429 storms, lost rights and a crash.
    /// Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Raid")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class RaidScenarios
    {
        private static void AssertOnly429s(HarnessChat chat)
        {
            var other = chat.Errors.Where(e => !e.Contains("Too Many Requests")).ToList();
            Assert.AreEqual(0, other.Count, "Errors reported:\n" + string.Join("\n", other));
        }

        private static List<HarnessUser> Regulars(HarnessChat chat, int count)
        {
            var regulars = new List<HarnessUser>();
            for (var i = 0; i < count; i++)
            {
                var r = chat.Add($"Regular{i}");
                chat.Hashes[$"{chat.ChatId}:users:{r.Id}", "msgs"] = "800";
                regulars.Add(r);
            }
            return regulars;
        }

        /// <summary>
        /// <paramref name="count"/> accounts join, one every <paramref name="every"/>, each posting
        /// the same picture a few seconds later. Joins as service messages, or as member updates
        /// through an invite link when <paramref name="viaLink"/>.
        /// </summary>
        private static async Task<List<HarnessUser>> RaidAsync(HarnessChat chat, int count, TimeSpan every, bool viaLink = false,
            string picture = "scam-picture", string prefix = "Raider")
        {
            var raiders = new List<HarnessUser>();
            for (var i = 0; i < count; i++)
            {
                var raider = chat.Add($"{prefix}{i}", MemberState.Left);
                raiders.Add(raider);
                if (viaLink) chat.Deliver(chat.MemberJoined(raider, "https://t.me/+leaked", "old link"));
                else chat.Deliver(chat.Join(raider));
                await chat.AdvanceAsync(every);
                chat.Deliver(chat.Photo(raider, uniqueId: picture, caption: "FREE crypto t.me/scam"));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            return raiders;
        }

        private static bool Locked(HarnessChat chat) => chat.Telegram.ChatPermissions(chat.ChatId)?.CanSendMessages == false;

        [TestMethod]
        public async Task HumanMode_ARaid_TagsTheAdminsAndWaits_ThenDoesWhatTheyChose()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var regulars = Regulars(chat, 5);

            var raiders = await RaidAsync(chat, 20, TimeSpan.FromSeconds(2));

            var alert = chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId && m.Text?.Contains("Possible raid") == true);
            StringAssert.Contains(alert.Text, $"tg://user?id={admin.Id}", "the admins are tagged");
            Assert.IsTrue(raiders.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Member), "nothing done before they decide");
            Assert.IsFalse(Locked(chat));

            await chat.PressAsync(admin, alert, $"raid:{chat.ChatId}:lock");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.IsTrue(Locked(chat), "the door is closed");
            Assert.IsTrue(raiders.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Kicked), "every raider banned");
            Assert.IsTrue(raiders.All(r => chat.Telegram.MessagesFrom(chat.ChatId, r.Id).Count == 0), "and their posts gone");
            Assert.IsTrue(regulars.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Member), "no regular touched");

            // A late joiner is muted on arrival.
            var late = chat.Add("Late", MemberState.Left);
            chat.Deliver(chat.Join(late));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, late.Id));

            // Ten quiet minutes later it is over and the group is open again.
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));
            Assert.IsFalse(Locked(chat), "permissions put back");
            Assert.IsNull(RaidGuard.IncidentIn(chat.ChatId));
            Assert.IsTrue(chat.Replies.Any(r => r.Contains("Raid mode is over")));
            Assert.AreEqual(0, chat.Telegram.LimitRefusals.Count, "never over Telegram's limits");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task AutoMode_ActsAtOnce_EvenWhileEveryMessageIsRateLimited()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            Regulars(chat, 3);
            chat.Telegram.StartMessageStorm(TimeSpan.FromMinutes(5), retryAfter: 300);

            var raiders = await RaidAsync(chat, 25, TimeSpan.FromSeconds(1));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.IsTrue(Locked(chat));
            Assert.IsTrue(raiders.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) is MemberState.Kicked or MemberState.Restricted),
                "every raider banned or muted while not a single message could be posted");
            Assert.AreEqual(0, raiders.Sum(r => chat.Telegram.MessagesFrom(chat.ChatId, r.Id).Count(_ => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Kicked)));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ARaidThroughAFullStorm_IsDealtWithWhenItEnds_NothingLost()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            chat.Telegram.StartStorm(c => FakeTelegram.IsMessage(c.Method) || FakeTelegram.IsModeration(c.Method),
                TimeSpan.FromMinutes(5), retryAfter: 120);

            var raiders = await RaidAsync(chat, 20, TimeSpan.FromSeconds(1));
            Assert.IsFalse(Locked(chat), "Telegram refuses everything for now");

            await chat.AdvanceAsync(TimeSpan.FromMinutes(7));

            Assert.IsTrue(Locked(chat), "locked as soon as Telegram let us");
            Assert.IsTrue(raiders.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) is MemberState.Kicked or MemberState.Restricted));
            Assert.AreEqual(0, Enforcer5.Enforcement.EnforcementQueue.CountIn(chat.ChatId), "nothing left behind");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ACrashInTheMiddleOfARaid_CarriesOn_AndStillUnlocks()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/raid");
            Assert.IsTrue(Locked(chat));

            await chat.CrashAndRestartAsync();
            Assert.IsNotNull(RaidGuard.IncidentIn(chat.ChatId), "the raid is still on after the restart");
            var late = chat.Add("Late", MemberState.Left);
            chat.Deliver(chat.Join(late));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, late.Id), "still quarantining");

            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));
            Assert.IsFalse(Locked(chat), "unlocked with the permissions saved before the crash");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task RegularsAllPostingTheSameSticker_IsNoRaid()
        {
            await using var chat = await HarnessChat.StartAsync();
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            var regulars = Regulars(chat, 15);

            foreach (var r in regulars) chat.Deliver(chat.Photo(r, uniqueId: "party-sticker"));
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));

            Assert.IsNull(RaidGuard.IncidentIn(chat.ChatId));
            Assert.IsFalse(Locked(chat));
            Assert.IsTrue(regulars.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Member));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ANewMembersBareLink_IsReported_AndBannedOnlyWhenAnAdminSaysSo()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var regular = Regulars(chat, 1)[0];
            var newcomer = chat.Add("Newcomer", MemberState.Left);
            await chat.JoinAsync(newcomer);

            await chat.SendAsync(regular, "https://example.com/news");
            var link = await chat.SendAsync(newcomer, "https://t.me/+cheapfollowers");

            var report = chat.Telegram.SentMessages.Single(m => m.Text?.Contains("posted nothing but a link") == true);
            StringAssert.Contains(report.Text, "Newcomer");
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, newcomer.Id).Count, "nothing deleted before an admin decides");

            await chat.PressAsync(admin, report, $"raidlink:{chat.ChatId}:ban");

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, newcomer.Id));
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, newcomer.Id).Count, "the link went with them");
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, regular.Id).Count, "a regular's link is left alone");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task SlashRaid_LocksAtOnce_AndSlashRaidOff_PutsThePermissionsBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Regulars(chat, 1)[0];
            var custom = new Telegram.Bot.Types.ChatPermissions { CanSendMessages = true, CanSendPhotos = false, CanInviteUsers = false };
            chat.Telegram.SetChatPermissions(chat.ChatId, custom);

            await chat.SendAsync(member, "/raid");
            Assert.IsNull(RaidGuard.IncidentIn(chat.ChatId), "admins only");

            await chat.SendAsync(admin, "/raid");
            Assert.IsTrue(Locked(chat));
            StringAssert.Contains(chat.Replies.Last(r => r.Contains("Raid mode is on")), $"tg://user?id={admin.Id}");

            await chat.SendAsync(admin, "/raid off");
            var restored = chat.Telegram.ChatPermissions(chat.ChatId);
            Assert.AreEqual((true, false, false), (restored.CanSendMessages, restored.CanSendPhotos, restored.CanInviteUsers),
                "exactly the group's own permissions");
            Assert.IsTrue(chat.Replies.Any(r => r.Contains("Raid mode is over")));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ARaidThatKeepsComing_KeepsRaidModeOn()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);

            for (var wave = 0; wave < 4; wave++)
            {
                await RaidAsync(chat, 16, TimeSpan.FromSeconds(1), picture: $"wave{wave}", prefix: $"W{wave}R");
                await chat.AdvanceAsync(TimeSpan.FromMinutes(6));
                Assert.IsTrue(Locked(chat), $"still on after wave {wave}");
            }
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));
            Assert.IsFalse(Locked(chat), "over once it stopped");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task TwoRaidedGroups_DoNotSlowACalmOne()
        {
            await using var raided = await HarnessChat.StartAsync(chatId: -1001000000001, title: "Raided one");
            var other = raided.Group(-1001000000002, "Raided two");
            var calm = raided.Group(-1001000000003, "Calm");
            RaidGuard.SetMode(raided.ChatId, RaidMode.Auto);
            RaidGuard.SetMode(other.ChatId, RaidMode.Auto);
            var calmAdmin = calm.Add("CalmAdmin", MemberState.Administrator);
            var troll = calm.Add("Troll");

            var first = RaidAsync(raided, 30, TimeSpan.FromMilliseconds(200));
            await first;
            await RaidAsync(other, 30, TimeSpan.FromMilliseconds(200));
            var asked = calm.Clock.UtcNow;
            await calm.SendAsync(calmAdmin, $"/ban {troll.Id}");

            var ban = calm.Telegram.Accepted.First(c => c.Method == "banChatMember" && c.ChatId == calm.ChatId);
            Assert.IsTrue(ban.At - asked <= TimeSpan.FromSeconds(2), $"the calm group waited {(ban.At - asked).TotalSeconds}s");
            AssertOnly429s(raided);
        }

        [TestMethod]
        public async Task LosingTheRightsMidRaid_TellsTheGroupOnce_AndCarriesOnWhenTheyAreBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            chat.Telegram.SetBotCanRestrict(chat.ChatId, false);

            var raiders = await RaidAsync(chat, 20, TimeSpan.FromSeconds(1));
            var callsWithoutRights = chat.Telegram.Calls.Count(c => FakeTelegram.IsModeration(c.Method));
            Assert.IsTrue(callsWithoutRights <= 6, $"{callsWithoutRights} moderation calls while the bot had no rights");

            chat.Telegram.SetBotCanRestrict(chat.ChatId, true);
            await chat.AdvanceAsync(TimeSpan.FromMinutes(3));

            Assert.IsTrue(Locked(chat));
            Assert.IsTrue(raiders.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) is MemberState.Kicked or MemberState.Restricted));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ModeOff_DetectsNothing_ButSlashRaidStillWorks()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/raidmode off");
            Assert.IsTrue(chat.Replies.Last().Contains("off"));

            await RaidAsync(chat, 20, TimeSpan.FromSeconds(1));
            Assert.IsNull(RaidGuard.IncidentIn(chat.ChatId));
            Assert.IsFalse(chat.Replies.Any(r => r.Contains("Possible raid")));

            await chat.SendAsync(admin, "/raid");
            Assert.IsTrue(Locked(chat));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task JoinsSeenOnlyAsMemberUpdates_AreEnough()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);

            var raiders = await RaidAsync(chat, 20, TimeSpan.FromSeconds(1), viaLink: true);

            Assert.IsTrue(Locked(chat));
            Assert.IsTrue(raiders.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) is MemberState.Kicked or MemberState.Restricted));
            AssertOnly429s(chat);
        }
    }
}
