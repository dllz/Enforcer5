using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests
{
    [TestClass]
    public class LogChannelLinkTests
    {
        private const long Group = -1001234567890;
        private const long AdminPm = 42;

        private static readonly XDocument Lang = XDocument.Parse(
            "<strings><string key=\"goToMessage\"><value>Go to message</value></string></strings>");

        private static Message On(long chatId, int messageId) => new() { Id = messageId, Chat = new Chat { Id = chatId } };

        private static long? LinkFor(string data, Message buttonMessage) =>
            Service.CallbackLinkMessageId(Group, data.Split(':'), buttonMessage);

        [TestMethod]
        public void ReportButtons_LinkTheReport_WherePressed()
        {
            Assert.AreEqual(77L, LinkFor($"banflag:{Group}:555:77", On(AdminPm, 9)));
            Assert.AreEqual(77L, LinkFor($"kickflag:{Group}:555:77", On(AdminPm, 9)));
            Assert.AreEqual(77L, LinkFor($"warnflag:{Group}:555:77", On(AdminPm, 9)));
            Assert.AreEqual(77L, LinkFor($"delflag:{Group}:80:77", On(AdminPm, 9)));
            Assert.AreEqual(77L, LinkFor($"solveflag:{Group}:77", On(AdminPm, 9)));
        }

        [TestMethod]
        public void OtherButtons_LinkTheMessageTheyAreOn_OnlyInTheGroup()
        {
            Assert.AreEqual(12L, LinkFor($"resetwarns:{Group}:555", On(Group, 12)));
            Assert.IsNull(LinkFor($"resetwarns:{Group}:555", On(AdminPm, 12)));
            Assert.IsNull(LinkFor($"resetwarns:{Group}:555", null));
        }

        [TestMethod]
        public void OldReportButtonsWithoutReportId_FallBackToTheButtonMessage()
        {
            Assert.IsNull(LinkFor($"banflag:{Group}:555", On(AdminPm, 9)));
        }

        [TestMethod]
        public void Supergroup_GetsAGoToMessageButton()
        {
            var markup = Service.LogLinkButton(Lang, Group, 77);
            var button = markup.InlineKeyboard.Single().Single();
            Assert.AreEqual("Go to message", button.Text);
            Assert.AreEqual("https://t.me/c/1234567890/77", button.Url);
        }

        [TestMethod]
        public void NoButton_WithoutAMessageOrALinkableChat()
        {
            Assert.IsNull(Service.LogLinkButton(Lang, Group, null));
            Assert.IsNull(Service.LogLinkButton(Lang, -123456, 77)); // basic group: no message links
            Assert.IsNull(Service.LogLinkButton(Lang, AdminPm, 77));
        }
    }
}
