using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests
{
    /// <summary>The explanations behind the settings' names (<see cref="MenuInfo"/>): there, and short enough for a popup.</summary>
    [TestClass]
    public class MenuInfoTests
    {
        /// <summary>Every explanation a menu asks for, and the string of the name shown with it.</summary>
        private static IEnumerable<(string Key, string Label)> Used() =>
            new[]
            {
                ("Flood", "FloodButton"), ("Report", "ReportButton"), ("Welcome", "WelcomeButton"),
                ("DeleteLastWelcome", "DeleteLastWelcomeButton"), ("sendIn", "ModlistButton"), ("MuteOnJoin", "MuteOnJoinButton"),
                ("ChannelPosts", "ChannelPostsButton"), ("WarnReasons", "WarnReasonsButton"),
                ("floodType", "stickerButton"), ("mediaType", "stickerButton"),
                ("nameLength", "nameSettingsHeader"), ("textLength", "textSettingsHeader"),
                ("editsLate", "editsLateButton"), ("editsDeleteNew", "editsDeleteNewButton"), ("editsEstablished", "editsEstablishedButton"),
                ("filterEstablished", "filterEstablishedButton"), ("filterGroupBots", "filterGroupBotsButton")
            }.Concat(FilterPresets.All.Select(p => ($"filter_{p.Name}", $"filterPreset_{p.Name}")));

        private static Dictionary<string, string> Strings(string path) =>
            XDocument.Load(path).Descendants("string")
                .GroupBy(s => (string)s.Attribute("key")).ToDictionary(g => g.Key, g => g.Last().Element("value")?.Value.Trim() ?? "");

        private static IEnumerable<string> Files() =>
            new[] { TestEnvironment.EnglishXmlPath }.Concat(Directory.GetFiles(Path.Combine(TestEnvironment.RepoRoot, "translations"), "*.xml"));

        [TestMethod]
        public void EveryExplanation_IsInEveryFile_AndFitsAPopup_WithItsName()
        {
            var english = Strings(TestEnvironment.EnglishXmlPath);
            var problems = new List<string>();
            foreach (var file in Files())
            {
                var strings = Strings(file);
                foreach (var (key, labelKey) in Used())
                {
                    if (!strings.TryGetValue(MenuInfo.Prefix + key, out var body))
                    {
                        problems.Add($"{Path.GetFileName(file)}: no {MenuInfo.Prefix}{key}");
                        continue;
                    }
                    var label = strings.GetValueOrDefault(labelKey) is { Length: > 0 } own ? own : english.GetValueOrDefault(labelKey, "");
                    var length = $"{label}: {body}".Length; // UTF-16 units, as Telegram counts
                    if (length > MenuInfo.MaxLength) problems.Add($"{Path.GetFileName(file)}: {key} is {length} long");
                    if (body.Contains('{')) problems.Add($"{Path.GetFileName(file)}: {key} has a placeholder");
                }
            }
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }
    }
}
