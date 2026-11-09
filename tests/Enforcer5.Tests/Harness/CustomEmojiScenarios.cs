using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Enforcer5.Detection.Images;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Custom emoji (Daniel, 2026-11-09; <see cref="EmojiCheck"/>, /block): NSFW images posted as custom emoji are deleted and
    /// reported, their pack known NSFW in every group and banned in the group it was posted in; admins block emoji packs,
    /// sticker packs, inline bots and files with one /block. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class CustomEmojiScenarios
    {
        /// <summary>Scores a "file" by its text: "nude" flagged, anything else safe.</summary>
        private sealed class FakeModel : INsfwModel
        {
            public string Version => "fake-1";

            public NsfwScore Score(byte[] image) => Encoding.UTF8.GetString(image).Contains("nude")
                ? new NsfwScore(0.97, new Dictionary<string, double> { ["BUTTOCKS_EXPOSED"] = 0.9 })
                : new NsfwScore(0.01, new Dictionary<string, double>());
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static async Task NsfwOnAsync(HarnessChat chat, bool on = true) =>
            await chat.ChangeSettingsAsync(p => p with
            {
                Engine = p.Engine with
                {
                    Signs = new Dictionary<string, SignPolicy>(p.Engine.Signs)
                        { [SignCatalog.ImageNsfw.Name] = p.Engine.For(SignCatalog.ImageNsfw.Name) with { Enabled = on } }
                }
            });

        private static async Task<HarnessChat> StartAsync(bool nsfw = true)
        {
            var chat = await HarnessChat.StartAsync();
            ImageScans.Model = new FakeModel();
            if (nsfw) await NsfwOnAsync(chat);
            return chat;
        }

        /// <summary>A custom emoji Telegram knows: a static one whose file says <paramref name="content"/>.</summary>
        private static void Emoji(HarnessChat chat, string id, string pack, string content)
        {
            chat.Files.Add("f-" + id, Encoding.UTF8.GetBytes(content));
            chat.Telegram.AddCustomEmoji(new Sticker
            {
                FileId = "f-" + id, FileUniqueId = "u-" + id, CustomEmojiId = id, SetName = pack, Width = 100, Height = 100,
                Type = StickerType.CustomEmoji, Emoji = "🍑"
            });
        }

        /// <summary>A text message of nothing but the custom emoji <paramref name="ids"/>.</summary>
        private static Message EmojiMessage(HarnessChat chat, HarnessUser from, params string[] ids)
        {
            var message = chat.Text(from, string.Concat(ids.Select(_ => "🍑")));
            message.Entities = ids.Select((id, i) => new MessageEntity { Type = MessageEntityType.CustomEmoji, Offset = i * 2, Length = 2, CustomEmojiId = id })
                .ToArray();
            return message;
        }

        private static bool Deleted(HarnessChat chat, HarnessUser user, Message message) =>
            !chat.Telegram.MessagesFrom(chat.ChatId, user.Id).Contains(message.MessageId);

        private static Message[] Reports(HarnessChat chat, HarnessUser admin) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true).ToArray();

        private static async Task<IReadOnlyList<EmojiSetEntry>> Packs(HarnessChat chat)
        {
            DetectionSettings.ResetForTests(); // read back from PostgreSQL
            return (await DetectionSettings.GetAsync(chat.ChatId)).EmojiSets;
        }

        /// <summary>The string as the group sees it: marked ids as code.</summary>
        private static string Reply(HarnessChat chat, string key, params object[] args) =>
            Ids.ToHtml(Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, key, args));

        [TestMethod]
        public async Task NsfwEmoji_AreDeletedAndReported_AndTheirPackIsMarkedForEveryGroupAndBannedHere()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            Emoji(chat, "e1", "Peaches_by_bot", "nude");
            Emoji(chat, "e2", "Peaches_by_bot", "a peach");
            Emoji(chat, "c1", "Cats", "a cat");

            var fine = await chat.PostAsync(EmojiMessage(chat, member, "c1", "c1"));
            var bad = await chat.PostAsync(EmojiMessage(chat, member, "e2", "e1", "c1"));

            Assert.IsFalse(Deleted(chat, member, fine));
            Assert.IsTrue(Deleted(chat, member, bad));
            var report = Reports(chat, admin).Single();
            StringAssert.Contains(report.Text, "NSFW emoji pack peaches_by_bot");
            var show = report.ReplyMarkup.InlineKeyboard.SelectMany(r => r).Single(b => b.CallbackData?.EndsWith(":show") == true);
            await chat.PressDataAsync(admin, report, show.CallbackData);
            Assert.AreEqual(new FakeTelegram.ResentFile(admin.Id, "sendSticker", "f-e1", true), chat.Telegram.ResentFiles.Single());

            Assert.IsTrue(await NsfwEmojiSets.IsNsfwAsync("peaches_by_bot"));
            CollectionAssert.AreEqual(new[] { new EmojiSetEntry("peaches_by_bot", EmojiSetEntry.Auto) }, (await Packs(chat)).ToArray());
            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM nsfw_emoji_sets WHERE source = 'auto'"));
            Assert.AreEqual(3, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM custom_emoji"));
            Assert.AreEqual(0.97f, await connection.ExecuteScalarAsync<float>("SELECT nsfw FROM custom_emoji WHERE custom_emoji_id = 'e1'"), 0.001);

            // Banned here now: a clean emoji of the pack goes too, without a report or scoring.
            var scored = ImageScans.Scored;
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));
            var again = await chat.PostAsync(EmojiMessage(chat, member, "e2"));
            Assert.IsTrue(Deleted(chat, member, again));
            Assert.AreEqual(scored, ImageScans.Scored);
            Assert.AreEqual(2, CustomEmojis.Calls, "each emoji looked up once (and e1 again for its file)");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AKnownNsfwPack_IsBannedInAnotherGroupWithTheCheckOn_WithoutScoring_AndNotWhereItIsOff()
        {
            await using var chat = await StartAsync();
            var member = chat.Add("Member");
            Emoji(chat, "e1", "Peaches", "nude");
            Emoji(chat, "e2", "Peaches", "a peach");
            await chat.PostAsync(EmojiMessage(chat, member, "e1"));
            Assert.IsTrue(await NsfwEmojiSets.IsNsfwAsync("peaches"));
            var scored = ImageScans.Scored;

            var other = chat.Group(-100777);
            await NsfwOnAsync(other);
            var otherAdmin = other.Add("Other admin", MemberState.Administrator);
            var raider = other.Add("Raider");
            var post = await other.PostAsync(EmojiMessage(other, raider, "e2"));
            Assert.IsTrue(Deleted(other, raider, post));
            Assert.AreEqual(scored, ImageScans.Scored, "known NSFW, not scored");
            StringAssert.Contains(Reports(other, otherAdmin).Single().Text, "NSFW emoji pack peaches");
            CollectionAssert.AreEqual(new[] { new EmojiSetEntry("peaches", EmojiSetEntry.Auto) }, (await Packs(other)).ToArray());

            var off = chat.Group(-100778);
            var someone = off.Add("Someone");
            var kept = await off.PostAsync(EmojiMessage(off, someone, "e1"));
            Assert.IsFalse(Deleted(off, someone, kept), "the check is off there");
            Assert.AreEqual(0, (await Packs(off)).Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AFalseReport_AllowsThePackInThatGroupOnly()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            Emoji(chat, "e1", "Beach", "nude");
            Emoji(chat, "e2", "Beach", "a beach");
            await chat.PostAsync(EmojiMessage(chat, member, "e1"));
            var report = Reports(chat, admin).Single();

            await chat.PressDataAsync(admin, report, report.ReplyMarkup.InlineKeyboard.SelectMany(r => r).Single(b => b.CallbackData?.EndsWith(":fine") == true).CallbackData);
            await chat.SettleAsync();

            CollectionAssert.AreEqual(new[] { new EmojiSetEntry("beach", EmojiSetEntry.Allowed) }, (await Packs(chat)).ToArray());
            var newcomer = chat.Add("Newcomer");
            var clean = await chat.PostAsync(EmojiMessage(chat, newcomer, "e2"));
            Assert.IsFalse(Deleted(chat, newcomer, clean), "the pack is not banned here any more");
            Assert.IsTrue(await NsfwEmojiSets.IsNsfwAsync("beach"), "still NSFW for the other groups");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminsNsfwEmoji_AreNotScored_AndMarkNothing()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            Emoji(chat, "e1", "Peaches", "nude");
            var post = await chat.PostAsync(EmojiMessage(chat, admin, "e1"));
            Assert.IsFalse(Deleted(chat, admin, post));
            Assert.AreEqual(0, ImageScans.Scored);
            Assert.IsFalse(await NsfwEmojiSets.IsNsfwAsync("peaches"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task NothingIsLookedUp_WhereNoPackIsBannedAndTheCheckIsOff()
        {
            await using var chat = await StartAsync(nsfw: false);
            var member = chat.Add("Member");
            Emoji(chat, "e1", "Peaches", "nude");
            var post = await chat.PostAsync(EmojiMessage(chat, member, "e1"));
            Assert.IsFalse(Deleted(chat, member, post));
            Assert.AreEqual(0, CustomEmojis.Calls);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BlockAsAReply_BansTheEmojiPack_AndUnblockLiftsIt()
        {
            await using var chat = await StartAsync(nsfw: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            Emoji(chat, "h1", "Hearts", "a heart");
            Emoji(chat, "h2", "Hearts", "another heart");
            var first = await chat.PostAsync(EmojiMessage(chat, member, "h1"));

            await chat.SendAsync(admin, "/block", replyTo: first);
            Assert.AreEqual(Reply(chat, "emojiBlockAdded", Ids.Mark("hearts")), chat.Replies.Last());
            Assert.IsTrue(Deleted(chat, member, first), "the post replied to goes");
            CollectionAssert.AreEqual(new[] { new EmojiSetEntry("hearts", EmojiSetEntry.Admin) }, (await Packs(chat)).ToArray());

            var second = await chat.PostAsync(EmojiMessage(chat, member, "h2"));
            Assert.IsTrue(Deleted(chat, member, second));
            var adminsOwn = await chat.PostAsync(EmojiMessage(chat, admin, "h2"));
            Assert.IsFalse(Deleted(chat, admin, adminsOwn));

            await chat.AdvanceAsync(TimeSpan.FromSeconds(20)); // the command limiter
            await chat.SendAsync(admin, "/blocked");
            StringAssert.Contains(chat.Replies.Last(), "hearts");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/unblock", replyTo: adminsOwn);
            Assert.AreEqual(Reply(chat, "emojiBlockRemoved", Ids.Mark("hearts")), chat.Replies.Last());
            var third = await chat.PostAsync(EmojiMessage(chat, member, "h2"));
            Assert.IsFalse(Deleted(chat, member, third));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BlockAsAReplyToAPostWithSeveralThings_OffersButtons_AndAllBlocksEach()
        {
            await using var chat = await StartAsync(nsfw: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            Emoji(chat, "h1", "Hearts", "a heart");
            var photo = chat.Photo(member, uniqueId: "meme", caption: "🍑");
            photo.CaptionEntities = new[] { new MessageEntity { Type = MessageEntityType.CustomEmoji, Offset = 0, Length = 2, CustomEmojiId = "h1" } };
            var post = await chat.PostAsync(photo);

            await chat.SendAsync(admin, "/block", replyTo: post);
            var question = chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId);
            Assert.AreEqual(Reply(chat, "blockChoose"), question.Text);
            CollectionAssert.AreEqual(new[] { Reply(chat, "blockChoiceEmojiSet", "hearts"), Reply(chat, "blockChoiceFile"), Reply(chat, "blockChoiceAll") },
                question.ReplyMarkup.InlineKeyboard.SelectMany(r => r).Select(b => b.Text).ToArray());
            Assert.IsFalse(Deleted(chat, member, post), "nothing yet");

            await chat.PressAsync(admin, question, $"{Commands.BlockTrigger}:{chat.ChatId}:");
            // The first button: the emoji pack only.
            CollectionAssert.AreEqual(new[] { new EmojiSetEntry("hearts", EmojiSetEntry.Admin) }, (await Packs(chat)).ToArray());
            Assert.IsTrue(Deleted(chat, member, post));
            Assert.AreEqual(0, await BannedMedia.CountAsync(chat.ChatId));

            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            var again = await chat.PostAsync(chat.Photo(member, uniqueId: "meme2"));
            await chat.SendAsync(admin, "/block", replyTo: again);
            Assert.AreEqual(Reply(chat, "banMediaAdded"), chat.Replies.Last(), "one thing: blocked at once");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BlockByName_AsksTelegramWhichKindOfPackItIs()
        {
            await using var chat = await StartAsync(nsfw: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.AddStickerSet(new StickerSet { Name = "Hearts", Title = "Hearts", StickerType = StickerType.CustomEmoji, Stickers = Array.Empty<Sticker>() });
            chat.Telegram.AddStickerSet(new StickerSet { Name = "Animals", Title = "Animals", StickerType = StickerType.Regular, Stickers = Array.Empty<Sticker>() });

            await chat.SendAsync(admin, "/block Hearts");
            Assert.AreEqual(Reply(chat, "emojiBlockAdded", Ids.Mark("hearts")), chat.Replies.Last());
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/block https://t.me/addstickers/Animals");
            Assert.AreEqual(Reply(chat, "stickerBlockAdded", Ids.Mark("animals")), chat.Replies.Last());
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/block Typo");
            Assert.AreEqual(Reply(chat, "packNotFound", "typo"), chat.Replies.Last());
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/block @gif");
            Assert.AreEqual(Reply(chat, "inlineBlockAdded", "@gif"), chat.Replies.Last());

            var policy = await DetectionSettings.GetAsync(chat.ChatId);
            CollectionAssert.AreEqual(new[] { "animals" }, policy.StickerSets.ToArray());
            Assert.AreEqual("gif", policy.InlineBots.Single().Value);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/unblock animals");
            Assert.AreEqual(Reply(chat, "stickerBlockRemoved", Ids.Mark("animals")), chat.Replies.Last());
            AssertNoErrors(chat);
        }
    }
}
