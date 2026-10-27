using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The ways raid mode itself could go wrong in a real raid: a group left locked, an alert that
    /// never reaches the admins, the wrong people muted or banned, raids that hide (posting late,
    /// as channels, by editing, with lookalike letters) and surges that are no raid. Needs the test
    /// database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Raid")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class RaidReadinessScenarios
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

        private static async Task<List<HarnessUser>> RaidAsync(HarnessChat chat, int count, TimeSpan every, string picture = "scam-picture",
            string prefix = "Raider")
        {
            var raiders = new List<HarnessUser>();
            for (var i = 0; i < count; i++)
            {
                var raider = chat.Add($"{prefix}{i}", MemberState.Left);
                raiders.Add(raider);
                chat.Deliver(chat.Join(raider));
                await chat.AdvanceAsync(every);
                chat.Deliver(chat.Photo(raider, uniqueId: picture, caption: "FREE crypto t.me/scam"));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            return raiders;
        }

        /// <summary>A text with its @mentions marked, as Telegram sends them.</summary>
        private static Message WithMentions(HarnessChat chat, HarnessUser from, string text)
        {
            var message = chat.Text(from, text);
            message.Entities = System.Text.RegularExpressions.Regex.Matches(text, @"@\w+")
                .Select(m => new MessageEntity { Type = MessageEntityType.Mention, Offset = m.Index, Length = m.Length }).ToArray();
            return message;
        }

        private static bool Locked(HarnessChat chat) => chat.Telegram.ChatPermissions(chat.ChatId)?.CanSendMessages == false;

        private static Message Alert(HarnessChat chat) =>
            chat.Telegram.SentMessages.LastOrDefault(m => m.Chat.Id == chat.ChatId && m.Text?.Contains("Possible raid") == true);

        // ------------------------------------------------------------ never left locked

        [TestMethod]
        public async Task SlashRaidOff_WhileTheLockWaitsOutA429_LeavesTheGroupOpen()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.StartStorm(c => c.Method == "setChatPermissions", TimeSpan.FromSeconds(90), retryAfter: 90);

            await chat.SendAsync(admin, "/raid");
            Assert.IsFalse(Locked(chat), "the lock waits out the 429");
            await chat.SendAsync(admin, "/raid off");

            await chat.AdvanceAsync(TimeSpan.FromMinutes(3));
            Assert.IsFalse(Locked(chat), "the unlock is not mistaken for the lock and dropped");
            Assert.AreEqual(0, Enforcer5.Enforcement.EnforcementQueue.CountIn(chat.ChatId));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ASecondRaidBeforeTheFirstUnlockRan_StillPutsTheGroupsOwnPermissionsBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var custom = new ChatPermissions { CanSendMessages = true, CanSendPhotos = false, CanInviteUsers = false };
            chat.Telegram.SetChatPermissions(chat.ChatId, custom);

            await chat.SendAsync(admin, "/raid");
            Assert.IsTrue(Locked(chat));
            chat.Telegram.StartStorm(c => c.Method == "setChatPermissions", TimeSpan.FromSeconds(90), retryAfter: 90);
            await chat.SendAsync(admin, "/raid off");
            await chat.SendAsync(admin, "/raid");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(3));
            Assert.IsTrue(Locked(chat), "the second raid's lock");

            await chat.SendAsync(admin, "/raid off");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));
            var restored = chat.Telegram.ChatPermissions(chat.ChatId);
            Assert.AreEqual((true, false, false), (restored.CanSendMessages, restored.CanSendPhotos, restored.CanInviteUsers),
                "the group's own permissions, not the first raid's lock");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ABusyGroup_LeavesRaidMode_ThoughRealMembersKeepJoining()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/raid");

            for (var i = 0; i < 10; i++)
            {
                await chat.JoinAsync(chat.Add($"Joiner{i}", MemberState.Left));
                await chat.AdvanceAsync(TimeSpan.FromMinutes(3));
            }
            Assert.IsNull(RaidGuard.IncidentIn(chat.ChatId), "a join every few minutes is no raid going on");
            Assert.IsFalse(Locked(chat));
            AssertOnly429s(chat);
        }

        // ------------------------------------------------------------ the admins hear about it

        [TestMethod]
        public async Task HumanMode_TheAlertGetsThroughA429()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            Regulars(chat, 3);
            chat.Telegram.StartMessageStorm(TimeSpan.FromMinutes(2), retryAfter: 120);

            await RaidAsync(chat, 20, TimeSpan.FromSeconds(2));
            Assert.IsNull(Alert(chat), "nothing can be posted yet");

            await chat.AdvanceAsync(TimeSpan.FromMinutes(3));
            var alert = Alert(chat);
            Assert.IsNotNull(alert, "the alert goes out once Telegram lets it");
            StringAssert.Contains(alert.Text, $"tg://user?id={admin.Id}");
            Assert.IsNotNull(alert.ReplyMarkup, "with its buttons");
            AssertOnly429s(chat);
        }

        // ------------------------------------------------------------ the right people

        [TestMethod]
        public async Task TheReleaseButton_UnmutesThisRaidsJoiners_NotTheGroupsOwnMutedJoiners()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var earlier = chat.Add("MutedLastWeek", MemberState.Restricted);
            chat.Keys.AddToSet($"chat:{chat.ChatId}:mutedJoiners", earlier.Id.ToString());

            await chat.SendAsync(admin, "/raid");
            var joiner = chat.Add("Joiner", MemberState.Left);
            await chat.JoinAsync(joiner);
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, joiner.Id));
            await chat.SendAsync(admin, "/raid off");

            var summary = chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId && m.Text?.Contains("Raid mode is over") == true);
            await chat.PressAsync(admin, summary, $"raid:{chat.ChatId}:release");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, joiner.Id), "this raid's joiner is free");
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, earlier.Id), "the group's own mute stays");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task AutoMode_RaidersWhoJoinedLongBeforePosting_AreStillBanned()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            var regulars = Regulars(chat, 3);
            var sleepers = new List<HarnessUser>();
            for (var i = 0; i < 10; i++)
            {
                var s = chat.Add($"Sleeper{i}", MemberState.Left);
                sleepers.Add(s);
                await chat.JoinAsync(s);
                await chat.AdvanceAsync(TimeSpan.FromMinutes(5));
            }
            await chat.AdvanceAsync(TimeSpan.FromHours(1));
            Assert.IsNull(RaidGuard.IncidentIn(chat.ChatId), "ten joins in an hour is no raid");

            foreach (var s in sleepers)
            {
                chat.Deliver(WithMentions(chat, s, "Everyone go and check @cryptogems for free signals every single day"));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(2));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.IsTrue(sleepers.All(s => chat.Telegram.GetMember(chat.ChatId, s.Id) == MemberState.Kicked), "every sleeper banned");
            Assert.IsTrue(sleepers.All(s => chat.Telegram.MessagesFrom(chat.ChatId, s.Id).Count == 0), "and their spam gone");
            Assert.IsTrue(regulars.All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Member));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task AutoMode_AFewSpammersWhoJoinedEarlier_AreMutedAndTheirSpamDeleted()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            var sleepers = new List<HarnessUser>();
            for (var i = 0; i < 3; i++)
            {
                var s = chat.Add($"Sleeper{i}", MemberState.Left);
                sleepers.Add(s);
                await chat.JoinAsync(s);
            }
            await chat.AdvanceAsync(TimeSpan.FromHours(1));

            foreach (var s in sleepers)
            {
                chat.Deliver(WithMentions(chat, s, "Everyone go and check @cryptogems for free signals every single day"));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(2));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.IsTrue(sleepers.All(s => chat.Telegram.GetMember(chat.ChatId, s.Id) == MemberState.Restricted), "held back until it is clearly a raid");
            Assert.IsTrue(sleepers.All(s => chat.Telegram.MessagesFrom(chat.ChatId, s.Id).Count == 0), "their spam is gone");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task HumanMode_ALatePosterOfTheRaidsPlainText_IsBannedOnceTheAdminsCalledItARaid()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            Regulars(chat, 3);
            var raiders = new List<HarnessUser>();
            for (var i = 0; i < 18; i++)
            {
                var r = chat.Add($"Raider{i}", MemberState.Left);
                raiders.Add(r);
                chat.Deliver(chat.Join(r));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(1));
                if (i < 4) chat.Deliver(chat.Text(r, "Earn 500 dollars a day, message me now"));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            await chat.PressAsync(admin, Alert(chat), $"raid:{chat.ChatId}:lock");

            await chat.AdvanceAsync(TimeSpan.FromMinutes(3));
            // The same text, from one of them, long after the first wave.
            chat.Deliver(chat.Text(raiders[10], "Earn 700 dollars a day, message me now!"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));

            Assert.IsTrue(raiders.Take(4).All(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Kicked), "the first posters");
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, raiders[10].Id), "and the late one");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task ALinkPosterAnAdminIgnored_IsNotBannedInTheNextRaid()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            Regulars(chat, 3);
            var newcomer = chat.Add("Newcomer", MemberState.Left);
            await chat.JoinAsync(newcomer);
            await chat.SendAsync(newcomer, "https://example.com/my-portfolio");
            var report = chat.Telegram.SentMessages.Single(m => m.Text?.Contains("posted nothing but a link") == true);
            await chat.PressAsync(admin, report, $"raidlink:{chat.ChatId}:ignore");

            await chat.AdvanceAsync(TimeSpan.FromHours(2));
            await RaidAsync(chat, 20, TimeSpan.FromSeconds(2));
            await chat.PressAsync(admin, Alert(chat), $"raid:{chat.ChatId}:ban");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, newcomer.Id), "the admin had looked at them");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task NewcomersReplyingAtAdmin_AreNotLinkPosters()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            var regular = Regulars(chat, 1)[0];
            var spam = await chat.SendAsync(regular, "Some long message the newcomers want an admin to look at");
            for (var i = 0; i < 4; i++)
            {
                var n = chat.Add($"Newcomer{i}", MemberState.Left);
                await chat.JoinAsync(n);
                await chat.AdvanceAsync(TimeSpan.FromMinutes(2));
                var report = chat.Text(n, "@admin", spam);
                report.Entities = new[] { new MessageEntity { Type = MessageEntityType.Mention, Offset = 0, Length = 6 } };
                await chat.PostAsync(report);
            }
            Assert.IsFalse(chat.Telegram.SentMessages.Any(m => m.Text?.Contains("posted nothing but a link") == true), "a report is not a link");
            Assert.IsTrue(RaidGuard.IncidentIn(chat.ChatId)?.Suspects.Count is null or 0, "nobody suspected for reporting");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task AnAdminsLink_IsNotReported()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "https://t.me/+ourotherchat");
            Assert.IsFalse(chat.Telegram.SentMessages.Any(m => m.Text?.Contains("posted nothing but a link") == true));
            AssertOnly429s(chat);
        }

        // ------------------------------------------------------------ raids that hide

        [TestMethod]
        public async Task AMessageEditedIntoALink_IsReported()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer", MemberState.Left);
            await chat.JoinAsync(newcomer);
            var message = await chat.SendAsync(newcomer, "Hello everyone, glad to be here with you all");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(5));

            var edited = chat.Text(newcomer, "https://t.me/+cheapfollowers");
            edited.Id = message.MessageId;
            edited.EditDate = DateTime.UtcNow;
            chat.Deliver(new Update { Id = 900001, EditedMessage = edited });
            await chat.CompleteAsync();

            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Text?.Contains("posted nothing but a link") == true));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task RaidersPostingAsChannels_AreBannedAsChannels()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            var channels = Enumerable.Range(0, 8).Select(i => -1009000000000L - i).ToList();
            var placeholder = new HarnessUser(136817688, "Channel");
            foreach (var channel in channels)
            {
                var post = chat.Photo(placeholder, uniqueId: "channel-scam", caption: "FREE crypto t.me/scam");
                post.SenderChat = new Chat { Id = channel, Type = ChatType.Channel, Title = $"Scam {channel}" };
                chat.Deliver(post);
                await chat.AdvanceAsync(TimeSpan.FromSeconds(2));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.IsTrue(channels.All(c => chat.Telegram.SenderChatBanned(chat.ChatId, c)), "every channel banned");
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, placeholder.Id).Count, "their posts gone");
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task LookalikeLettersAndRandomEndings_AreStillTheSameSpam()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            Regulars(chat, 3);
            var texts = new[]
            {
                "Earn five hundred dollars a day from home, message @earnfast now xkq",
                "Еаrn fivе hundrеd dollars a dаy from home, message @earnfast now pzw", // Cyrillic е and а
                "E̲a̲rn five hundred dollars a day from home, message @earnfast now bnv", // combining marks
                "Earn five hundred dollars a day from home message @earnfast now!! jjr"
            };
            var spammers = new List<HarnessUser>();
            for (var i = 0; i < texts.Length; i++)
            {
                spammers.Add(chat.Add($"Spammer{i}", MemberState.Left));
                await chat.JoinAsync(spammers[i]);
                await chat.AdvanceAsync(TimeSpan.FromMinutes(2));
            }
            for (var i = 0; i < texts.Length; i++)
            {
                chat.Deliver(WithMentions(chat, spammers[i], texts[i]));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(3));
            }
            Assert.IsNotNull(Alert(chat), "seen as the same text from several newcomers");
            Assert.AreEqual(4, RaidGuard.IncidentIn(chat.ChatId).Suspects.Count);
            AssertOnly429s(chat);
        }

        // ------------------------------------------------------------ surges that are no raid

        [TestMethod]
        public async Task AutoMode_ABatchOfApprovedJoinRequests_IsNoRaid()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            for (var i = 0; i < 40; i++)
            {
                var u = chat.Add($"Approved{i}", MemberState.Left);
                // Telegram sends the "joined" service message too, sometimes before the member update
                // that says it was an approved request.
                if (i % 2 == 1) chat.Deliver(chat.Join(u));
                chat.Deliver(chat.MemberJoined(u, addedBy: admin, viaRequest: true));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(1));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
            Assert.IsNull(RaidGuard.IncidentIn(chat.ChatId), "the admins let each of them in");
            Assert.IsFalse(Locked(chat));
            AssertOnly429s(chat);
        }

        [TestMethod]
        public async Task AutoMode_AGiveawaySurge_IsNeitherLockedNorBanned()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            Regulars(chat, 5);
            var entrants = new List<HarnessUser>();
            for (var i = 0; i < 25; i++)
            {
                var u = chat.Add($"Entrant{i}", MemberState.Left);
                entrants.Add(u);
                chat.Deliver(chat.MemberJoined(u, "https://t.me/+giveaway", "giveaway"));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(1));
                chat.Deliver(chat.Text(u, "I would love to take part in the giveaway please"));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.IsFalse(Locked(chat), "nothing harmful was posted, so the group stays open");
            Assert.IsTrue(entrants.All(u => chat.Telegram.GetMember(chat.ChatId, u.Id) != MemberState.Kicked), "nobody banned");
            AssertOnly429s(chat);
        }
    }
}
