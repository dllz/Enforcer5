using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests
{
    [TestClass]
    public class BotSenderFilterTests
    {
        [TestMethod]
        public void PeopleAndTrustedBotsAreHandled_OtherBotsAreNot()
        {
            Assert.IsTrue(BotSenderFilter.Accept(new User { Id = 42, IsBot = false }));
            Assert.IsTrue(BotSenderFilter.Accept(new User { Id = BotSenderFilter.EsmeBotId, IsBot = true }));
            Assert.IsTrue(BotSenderFilter.Accept(new User { Id = 1087968824, IsBot = true }), "anonymous admin");
            Assert.IsTrue(BotSenderFilter.Accept(new User { Id = ChannelPosts.PlaceholderSenderId, IsBot = true }), "post as a channel");
            Assert.IsFalse(BotSenderFilter.Accept(new User { Id = 618096097, IsBot = true }));
            Assert.IsTrue(BotSenderFilter.Accept((User)null), "service messages without a sender");
        }

        [TestMethod]
        public void OnlyEsme_OnlyTheLanguageUpload()
        {
            Assert.IsTrue(BotSenderFilter.IsGrantedCommand(BotSenderFilter.EsmeBotId, "uploadlanguage"));
            Assert.IsTrue(BotSenderFilter.IsGrantedCommand(BotSenderFilter.EsmeBotId, "UploadLanguage"));
            Assert.IsFalse(BotSenderFilter.IsGrantedCommand(BotSenderFilter.EsmeBotId, "makelangadmin"));
            Assert.IsFalse(BotSenderFilter.IsGrantedCommand(BotSenderFilter.EsmeBotId, "reloadLang"));
            Assert.IsFalse(BotSenderFilter.IsGrantedCommand(42, "uploadlanguage"));
            Assert.IsFalse(BotSenderFilter.IsGrantedCommand(BotSenderFilter.EsmeBotId, null));
        }

        [DataTestMethod]
        [DataRow("ban", "ban", false, true)]
        [DataRow("ban@EnforcedBot", "ban", true, true)]
        [DataRow("ban@enforcedbot", "ban", true, true)]
        [DataRow("ban@ENFORCEDBOT", "ban", true, true)]
        [DataRow("ban@enforcerbot", "ban", true, false)]
        [DataRow("ban@", "ban", true, false)]
        public void CommandAddress_IsCaseInsensitive(string word, string trigger, bool addressed, bool forMe)
        {
            var address = CommandAddress.Parse(word, "EnforcedBot");
            Assert.AreEqual(trigger, address.Trigger);
            Assert.AreEqual(addressed, address.Addressed);
            Assert.AreEqual(forMe, address.ForMe);
        }

        [TestMethod]
        public void PlainUploadInTheTranslatorsGroup_OnlyPremiumAnswers()
        {
            var upload = new Models.Commands { Trigger = "uploadlanguage" };
            var plain = CommandAddress.Parse("uploadlanguage", "enforcerbot");
            var addressed = CommandAddress.Parse("uploadlanguage@enforcerbot", "enforcerbot");

            Assert.IsTrue(UpdateHandler.AnswersHere(upload, plain, Constants.TranslatorsId, premium: true));
            Assert.IsFalse(UpdateHandler.AnswersHere(upload, plain, Constants.TranslatorsId, premium: false));
            Assert.IsTrue(UpdateHandler.AnswersHere(upload, addressed, Constants.TranslatorsId, premium: false));
            Assert.IsTrue(UpdateHandler.AnswersHere(upload, plain, -100123, premium: false), "elsewhere both answer");
            Assert.IsTrue(UpdateHandler.AnswersHere(new Models.Commands { Trigger = "id" }, plain, Constants.TranslatorsId, premium: false));
        }
    }
}
