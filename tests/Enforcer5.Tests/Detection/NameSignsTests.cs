using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Detection
{
    /// <summary>
    /// Detection step 10: which names copy an admin's or a bot's, and which claim to be staff. The names are made
    /// up for the tests (design section 11: must and must-not fixtures, no player data).
    /// </summary>
    [TestClass]
    public class NameSignsTests
    {
        private static readonly User Admin = new User { Id = 1, FirstName = "Daniel", LastName = "Kayo", Username = "danielkayo" };
        private static readonly User ShortAdmin = new User { Id = 2, FirstName = "Max" };
        private static readonly User GroupBot = new User { Id = 3, FirstName = "Werewolf Moderator", Username = "WerewolfModBot", IsBot = true };

        private static NameSigns.Likeness Of(string first, string last = null, string username = null) =>
            NameSigns.Impersonates(new User { Id = 99, FirstName = first, LastName = last, Username = username },
                new[] { (Admin, "admin"), (ShortAdmin, "admin"), (GroupBot, "bot") });

        [DataTestMethod]
        [DataRow("Daniel Kayo", null, null, "same")]
        [DataRow("Dаniel Kаyo", null, null, "same")]          // Cyrillic а
        [DataRow("D\u200Daniel Kayo", null, null, "same")]    // zero-width joiner
        [DataRow("🛡 Daniel Kayo 🛡", null, null, "same")]     // emoji padding
        [DataRow("Daniel", "Kayo1", null, "same")]            // digits are not letters
        [DataRow("Daniel Kayoo", null, null, "close")]
        [DataRow("Danile Kayo", null, null, "close")]         // two letters swapped
        [DataRow("danielkayo", null, null, "same")]           // their username as a name
        [DataRow("Anyone", null, "daniel_kayo", "same")]      // their name as a username
        [DataRow("Daniel Kayo | Support", null, null, "keyword")]
        [DataRow("Anyone", null, "danielkayo_admin", "keyword")]
        [DataRow("Official Max", null, null, "keyword")]
        [DataRow("Max", null, null, "same")]
        [DataRow("Werewolf Moderator", null, null, "same")]
        [DataRow("WerewolfModB0t", null, null, "close")]      // a long name may be two letters off
        public void Copies(string first, string last, string username, string how)
        {
            var like = Of(first, last, username);
            Assert.IsNotNull(like, first);
            Assert.AreEqual(how, like.How, first);
        }

        [DataTestMethod]
        [DataRow("Daniel", null, null)]                       // a first name is not their full name
        [DataRow("Danielle Kayode", null, null)]
        [DataRow("Maximilian", null, null)]                   // starts like Max, is not Max
        [DataRow("Maximilian Support", null, null)]           // nor with a staff word
        [DataRow("Mix", null, null)]                          // short names only when the same
        [DataRow("Badminton fan", null, null)]
        [DataRow("Werewolf player", null, null)]
        [DataRow("🙂", null, null)]
        public void DoesNotCopy(string first, string last, string username) =>
            Assert.IsNull(Of(first, last, username), first);

        [TestMethod]
        public void TheAdminThemself_IsNoCopy() =>
            Assert.IsNull(NameSigns.Impersonates(Admin, new[] { (Admin, "admin") }));

        [DataTestMethod]
        [DataRow("Support Team", null, true)]
        [DataRow("Anna", "official_anna", true)]
        [DataRow("Аdmin", null, true)]                        // Cyrillic А
        [DataRow("Badminton fan", "badminton", false)]
        [DataRow("Anna", "anna_k", false)]
        public void Keyword(string first, string username, bool expected) =>
            Assert.AreEqual(expected, NameSigns.HasKeyword(new User { Id = 5, FirstName = first, Username = username }));

        [DataTestMethod]
        [DataRow("abcdef", "abcdef", 0)]
        [DataRow("abcdef", "abcdxf", 1)]
        [DataRow("abcdef", "abdcef", 1)]
        [DataRow("abcdef", "abcdefg", 1)]
        [DataRow("abcdef", "uvwxyz", 2)]                      // stops past the limit
        public void Distance(string a, string b, int expected) =>
            Assert.AreEqual(expected, NameSigns.Distance(a, b, 1));

        [TestMethod]
        public void ReportDetail_NamesWhomTheyCopy() =>
            Assert.AreEqual("Daniel Kayo (@danielkayo)", NameSigns.Describe(Admin));
    }
}
