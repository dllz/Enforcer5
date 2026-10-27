using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Menus = Enforcer5.Helpers.CommandMenus;

namespace Enforcer5.Tests.MenuTests
{
    /// <summary>The menus Telegram shows are built from the commands' attributes; these keep them valid.</summary>
    [TestClass]
    public class CommandMenuTests
    {
        private static readonly XDocument English = XDocument.Load(
            System.IO.Path.Combine(TestEnvironment.RepoRoot, "src", "Enforcer5", "Languages", "English.xml"));

        [TestMethod]
        public void EveryMenuCommand_HasAShortDescriptionInEnglish()
        {
            var missing = Menus.AdminCommands.Concat(Menus.PrivateCommands).Distinct()
                .Where(c => string.IsNullOrWhiteSpace(Menus.Plain(English, Menus.DescriptionKey(c)))).ToList();
            Assert.AreEqual(0, missing.Count, "Add cmd{trigger} to English.xml for: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void NoDescriptionIsLeftOver()
        {
            var listed = Menus.AdminCommands.Concat(Menus.PrivateCommands).Select(Menus.DescriptionKey).ToHashSet();
            var unused = English.Descendants("string").Select(s => s.Attribute("key")!.Value)
                .Where(k => k.StartsWith("cmd", StringComparison.Ordinal) && !listed.Contains(k)).ToList();
            Assert.AreEqual(0, unused.Count, "Not in any menu: " + string.Join(", ", unused));
        }

        [TestMethod]
        public void Menus_FitTelegramsRules()
        {
            foreach (var (scope, commands) in Menus.Menus)
            {
                Assert.IsTrue(commands.Length <= Menus.MaxCommands, $"{scope.Type} has {commands.Length}");
                Assert.AreEqual(commands.Length, commands.Distinct().Count(), $"{scope.Type} lists a command twice");
                foreach (var c in commands) Assert.IsTrue(Regex.IsMatch(c, "^[a-z0-9_]{1,32}$"), c);
                foreach (var d in Menus.Build(commands, k => Menus.Plain(English, k) ?? k))
                    Assert.IsTrue(d.Description.Length is >= 1 and <= 256, d.Command);
            }
            var staff = Menus.BuildStaff(Menus.StaffRole.Dev, k => Menus.Plain(English, k) ?? k);
            Assert.IsTrue(staff.Count <= Menus.MaxCommands);
        }

        [TestMethod]
        public void StaffCommands_SayWhatTheyDo()
        {
            var bare = Menus.StaffCommands(Menus.StaffRole.Dev).Where(c => string.IsNullOrWhiteSpace(c.Attribute.Description))
                .Select(c => c.Command).ToList();
            Assert.AreEqual(0, bare.Count, "Give these a Description: " + string.Join(", ", bare));
        }

        [TestMethod]
        public void StaffCommands_AreInNoPublicMenu_AndAdminCommandsOnlyInTheAdminMenu()
        {
            var staff = Menus.StaffCommands(Menus.StaffRole.Dev).Select(c => c.Command).ToList();
            foreach (var (_, commands) in Menus.Menus) CollectionAssert.DoesNotContain(commands, staff.FirstOrDefault());
            Assert.IsFalse(staff.Intersect(Menus.AdminCommands).Any());
            CollectionAssert.DoesNotContain(Menus.GroupCommands, "ban");
            CollectionAssert.Contains(Menus.AdminCommands, "ban");
            CollectionAssert.Contains(Menus.GroupCommands, "rules");
            CollectionAssert.Contains(Menus.PrivateCommands, "start");
            CollectionAssert.DoesNotContain(Menus.GroupCommands, "start");
        }
    }
}
