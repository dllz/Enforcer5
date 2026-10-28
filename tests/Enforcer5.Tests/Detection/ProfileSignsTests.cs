using System.Linq;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 11: what is read from a bio or a channel's description, and kept as flags.</summary>
    [TestClass]
    public class ProfileSignsTests
    {
        [DataTestMethod]
        [DataRow("dm me https://shop.example/deal", 1)]
        [DataRow("my channel t.me/cryptogems", 1)]
        [DataRow("visit www.example.org", 1)]
        [DataRow("best prices at cheapdeals.shop!", 1)]
        [DataRow("love dogs and long walks. 25", 0)]
        [DataRow("mail me at anna@example", 0)]
        [DataRow("version 1.2.3", 0)]
        public void Links(string bio, int expected) => Assert.AreEqual(expected, ProfileSigns.Links(bio).Count, bio);

        [DataTestMethod]
        [DataRow("contact @CryptoGems for signals", "cryptogems")]
        [DataRow("anna@example.com", "")]
        [DataRow("@ab is too short", "")]
        [DataRow("@one and @twoo and @three", "twoo,three")]
        public void Mentions(string bio, string expected) =>
            Assert.AreEqual(expected, string.Join(",", ProfileSigns.Mentions(bio)), bio);

        [DataTestMethod]
        [DataRow("https://t.me/CryptoGems", "cryptogems")]
        [DataRow("t.me/cryptogems/55", "cryptogems")]
        [DataRow("telegram.me/somegroup?start=1", "somegroup")]
        [DataRow("https://t.me/+AbCdEf123", null)]
        [DataRow("https://t.me/joinchat/AbCdEf", null)]
        [DataRow("t.me/addstickers/Pack", null)]
        [DataRow("t.me/c/123456/7", null)]
        [DataRow("https://example.com/cryptogems", null)]
        public void TelegramName(string link, string expected) => Assert.AreEqual(expected, ProfileSigns.TelegramName(link), link);

        [TestMethod]
        public void ChannelNames_FromMentionsAndLinks() =>
            CollectionAssert.AreEquivalent(new[] { "gems", "deals" }, ProfileSigns.ChannelNames("join @gems or t.me/deals now").ToArray());

        [TestMethod]
        public void AccountFlags_SayWhatIsThere_NeverWhatItSays()
        {
            var flags = ProfileSigns.AccountFlags(new ChatFullInfo
            {
                Id = 5, Bio = "Official support. Write @helpdeskx or +44 20 7946 0958, t.me/+invite123",
                PersonalChat = new Chat { Id = -1001 }, EmojiStatusCustomEmojiId = "123"
            });
            CollectionAssert.AreEquivalent(new[]
            {
                "bio", "bio_link", "bio_mention", "bio_invite_link", "bio_phone", "bio_staff_word", "personal_channel", "emoji_status"
            }, flags.ToArray());
            Assert.IsFalse(flags.Any(f => f.Contains("helpdeskx") || f.Contains("support.")), "no text");
        }

        [TestMethod]
        public void AccountFlags_OfAnEmptyProfile_AreEmpty() =>
            Assert.AreEqual(0, ProfileSigns.AccountFlags(new ChatFullInfo { Id = 5 }).Count);

        [TestMethod]
        public void ChannelFlags()
        {
            var flags = ProfileSigns.ChannelFlags(new ChatFullInfo
            {
                Id = -100, Title = "Official Crypto Support", Description = "signals daily https://pump.example", LinkedChatId = -200
            });
            CollectionAssert.AreEquivalent(new[] { "description", "description_link", "title_staff_word", "linked_chat" }, flags.ToArray());
        }

        [TestMethod]
        public void OwnRules_OnBiosAndChannels_MatchOnlyThere()
        {
            var bio = new FilterRule(1, FilterRule.RegexKind, null, "bio", "casino", "ban", true, FilterRule.Live, null, null);
            var channel = new FilterRule(2, FilterRule.RegexKind, null, "channel", "signals", "delete", true, FilterRule.Live, null, null);
            var filters = new FilterSettings(new[] { bio, channel }, true, false);

            Assert.AreEqual(1L, FilterCheck.FindIn(filters, FilterTarget.Bio, "best casino in town").Single().Rule.Id);
            Assert.AreEqual(0, FilterCheck.FindIn(filters, FilterTarget.Text, "best casino in town").Count);
            Assert.AreEqual(2L, FilterCheck.FindIn(filters, FilterTarget.Channel, "Crypto Signals VIP").Single().Rule.Id);
            Assert.AreEqual(0, FilterCheck.FindIn(filters, FilterTarget.Bio, "Crypto Signals VIP").Count);
            Assert.AreEqual(0, FilterCheck.Find(filters, new User { Id = 1, FirstName = "casino" }, "signals").Count,
                "names and text are not bios or channels");
        }

        [TestMethod]
        public void ThePresets_KeepTheirTargets()
        {
            var filters = FilterSettings.Empty.WithPreset("phone", "delete");
            Assert.AreEqual(0, FilterCheck.FindIn(filters, FilterTarget.Bio, "+44 20 7946 0958").Count);
        }
    }
}
