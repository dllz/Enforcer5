using System;
using System.Linq;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 5: the presets, admins' regex rules and what a message offers them. Pure.</summary>
    [TestClass]
    public class FilterTests
    {
        private static bool Preset(string name, string text) => FilterPresets.Get(name).Matches(text);

        [TestMethod]
        [DataRow("سلام")]
        [DataRow("ع")] // missing from the old hand-written letter set
        [DataRow("پ")] // Persian
        [DataRow("ﻻ")] // presentation form
        public void Arabic_IsTheWholeScript(string text) => Assert.IsTrue(Preset("arabic", $"hi {text}"));

        [TestMethod]
        [DataRow("hello")]
        [DataRow("\uFEFFhello")]
        [DataRow("Привет")]
        public void Arabic_LeavesOtherTextAlone(string text) => Assert.IsFalse(Preset("arabic", text));

        [TestMethod]
        [DataRow("\u202E")]
        [DataRow("\u202D")]
        [DataRow("\u202B")]
        [DataRow("\u2067")]
        [DataRow("\u2068")]
        public void Rtl_IsEveryOverrideAndIsolate(string c) => Assert.IsTrue(Preset("rtl", $"file{c}gpj.exe"));

        [TestMethod]
        public void Rtl_LeavesHebrewAndPlainMarksAlone()
        {
            Assert.IsFalse(Preset("rtl", "שלום"));
            Assert.IsFalse(Preset("rtl", "abc\u200Fdef"), "a right-to-left mark is ordinary in Persian and Hebrew text");
        }

        [TestMethod]
        public void Scripts()
        {
            Assert.IsTrue(Preset("cyrillic", "Привет"));
            Assert.IsTrue(Preset("cjk", "你好"));
            Assert.IsTrue(Preset("cjk", "こんにちは"));
            Assert.IsTrue(Preset("cjk", "안녕"));
            Assert.IsTrue(Preset("hebrew", "שלום"));
            Assert.IsTrue(Preset("devanagari", "नमस्ते"));
            foreach (var preset in new[] { "cyrillic", "cjk", "hebrew", "devanagari" })
                Assert.IsFalse(Preset(preset, "Hello, café! 123"), preset);
        }

        [TestMethod]
        public void Lookalikes_AreLatinAndCyrillicOrGreekInOneWord()
        {
            Assert.IsTrue(Preset("lookalikes", "\u0410dmin")); // Cyrillic А
            Assert.IsTrue(Preset("lookalikes", "supp\u03BFrt")); // Greek ο
            Assert.IsFalse(Preset("lookalikes", "Привет, hello"), "two scripts in two words is just two languages");
            Assert.IsFalse(Preset("lookalikes", "hello"));
        }

        [TestMethod]
        public void ZeroWidth_ButNotInEmojiOrPersian()
        {
            Assert.IsTrue(Preset("zero_width", "free\u200Bmoney"));
            Assert.IsTrue(Preset("zero_width", "\u3164"), "a Hangul filler, used for blank names");
            Assert.IsTrue(Preset("zero_width", "ad\u200Dmin"));
            Assert.IsFalse(Preset("zero_width", "👨\u200D👩\u200D👧"), "the joiner inside an emoji family");
            Assert.IsFalse(Preset("zero_width", "می\u200Cخواهم"), "the non-joiner in Persian");
            Assert.IsFalse(Preset("zero_width", "\uFEFFhello"), "a byte order mark at the start");
        }

        [TestMethod]
        [DataRow("join t.me/+AbCdEf123", true)]
        [DataRow("https://t.me/joinchat/AAAAAE", true)]
        [DataRow("telegram.me/+xyz", true)]
        [DataRow("tg://join?invite=abc", true)]
        [DataRow("see t.me/durov", false)]
        [DataRow("https://example.com/+abc", false)]
        public void InviteLinks(string text, bool match) => Assert.AreEqual(match, Preset("invite_link", text));

        [TestMethod]
        [DataRow("call +31 6 1234 5678", true)]
        [DataRow("+1 (555) 123-4567", true)]
        [DataRow("bel 0612345678", true)]
        [DataRow("on 2026-10-28 at 12:30", false)]
        [DataRow("costs 1 000 000", false)]
        [DataRow("room 101", false)]
        [DataRow("+31 6 12", false)]
        public void PhoneNumbers(string text, bool match) => Assert.AreEqual(match, Preset("phone", text));

        [TestMethod]
        [DataRow("send to 0x52908400098527886E0F7030069857D2E4169EE7", true)]
        [DataRow("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq", true)]
        [DataRow("1BoatSLRHtKNngkdXEeobR76b53LETtpyT", true)]
        [DataRow("TJCnKsPa7y5okkXvQAidZBzqx3QyQ6sxMW", true)]
        [DataRow("EQDtFpEwcFAEcRe5mLVh2N6C0x-_hJEM7W61_JLnSF74p4q2", true)]
        [DataRow("just a normal sentence about bitcoin", false)]
        public void Wallets(string text, bool match) => Assert.AreEqual(match, Preset("crypto", text));

        [TestMethod]
        [DataRow("Telegram Support", true)]
        [DataRow("\u0410dmin Team", true)] // Cyrillic А
        [DataRow("Official_Updates", true)]
        [DataRow("Customer Service", true)]
        [DataRow("Badminton Club", false)]
        [DataRow("Anna", false)]
        public void StaffWords(string name, bool match) => Assert.AreEqual(match, Preset("staff_words", name));

        [TestMethod]
        public void Regex_RefusesWhatCannotRunSafely()
        {
            Assert.IsFalse(FilterRegex.TryCompile("(?=spam)spam", out _, out var lookaround));
            Assert.IsFalse(string.IsNullOrEmpty(lookaround));
            Assert.IsFalse(FilterRegex.TryCompile(@"(a)\1", out _, out _), "backreference");
            Assert.IsFalse(FilterRegex.TryCompile("[", out _, out _));
            Assert.IsFalse(FilterRegex.TryCompile(new string('a', FilterRegex.MaxLength + 1), out _, out _));
            Assert.IsFalse(FilterRegex.TryCompile(@"^[\s\S]{4000,}$", out _, out _), "too large for the automaton");
            Assert.IsTrue(FilterRegex.TryCompile(@"free +crypto", out _, out _));
        }

        [TestMethod]
        public void Regex_IgnoresCase_AndSeesThroughLookalikes()
        {
            Assert.IsTrue(FilterRegex.Matches("free +crypto", "Get FREE   Crypto now"));
            Assert.IsTrue(FilterRegex.Matches("admin", "\u0410DMIN"), "Cyrillic А folded");
            Assert.IsFalse(FilterRegex.Matches("free +crypto", "freedom"));
            Assert.IsFalse(FilterRegex.Matches("(?=x)", "x"), "a pattern that cannot run matches nothing");
        }

        [TestMethod]
        public void A_RuleOnlyLooksAtItsTargets()
        {
            var name = new FilterRule(1, FilterRule.RegexKind, null, "name", "bad", "ban", true, FilterRule.Live, null, null);
            Assert.IsTrue(name.Matches(FilterTarget.Name, "bad guy"));
            Assert.IsFalse(name.Matches(FilterTarget.Text, "bad guy"));
            var invite = FilterRule.NewPreset("invite_link", "delete");
            Assert.IsFalse(invite.Matches(FilterTarget.Name, "t.me/+abc"), "a text preset");
            Assert.IsTrue(invite.Matches(FilterTarget.Text, "t.me/+abc"));
            Assert.AreEqual("filter:invite_link", invite.Sign);
            Assert.AreEqual("filter:1", name.Sign);
            Assert.IsNotNull(SignCatalog.Get("filter:1"));
            Assert.AreEqual("filter:1", SignCatalog.Get("filter:1").Name);
            Assert.IsNull(SignCatalog.Get("filter:"));
        }

        [TestMethod]
        public void TheTextTarget_IsTextOrCaption_LinkAddresses_AndWhereItWasForwardedFrom()
        {
            var message = new Message
            {
                Caption = "look",
                CaptionEntities = new[] { new MessageEntity { Type = MessageEntityType.TextLink, Offset = 0, Length = 4, Url = "https://t.me/+secret" } },
                ForwardOrigin = new MessageOriginUser { SenderUser = new User { Id = 5, FirstName = "بدر" } }
            };
            var text = FilterCheck.TextOf(message);
            StringAssert.Contains(text, "look");
            StringAssert.Contains(text, "t.me/+secret");
            StringAssert.Contains(text, "بدر");
        }

        [TestMethod]
        public void Find_ChecksTextNameAndUsername_AndSkipsWhatIsOff()
        {
            var filters = FilterSettings.Empty.WithPreset("arabic", "kick").WithPreset("crypto", null) with
            {
                Rules = FilterSettings.Empty.WithPreset("arabic", "kick").WithPreset("crypto", null).Rules
                    .Append(new FilterRule(9, FilterRule.RegexKind, null, "username", "^spam", "ban", true, FilterRule.Trial, DateTimeOffset.MaxValue, null))
                    .ToList()
            };
            var hits = FilterCheck.Find(filters, new User { Id = 1, FirstName = "Ann", Username = "spammer" },
                "hi 0x52908400098527886E0F7030069857D2E4169EE7");
            Assert.AreEqual(1, hits.Count, "crypto is off");
            Assert.AreEqual((9L, FilterTarget.Username), (hits[0].Rule.Id, hits[0].Target));

            hits = FilterCheck.Find(filters, new User { Id = 1, FirstName = "بدر" }, null);
            Assert.AreEqual(("arabic", FilterTarget.Name), (hits.Single().Rule.Preset, hits.Single().Target));
        }

        [TestMethod]
        public void Settings_AreTheSame_WhateverTheIdsAndAPresetThatIsOff()
        {
            var a = DetectionPolicy.Empty with { Filters = FilterSettings.Empty.WithPreset("phone", "delete") };
            var b = a with { Filters = a.Filters with { Rules = a.Filters.Rules.Select(r => r with { Id = 42 }).ToList() } };
            Assert.IsTrue(a.SameSettingsAs(b));
            Assert.IsTrue(DetectionPolicy.Empty.SameSettingsAs(DetectionPolicy.Empty with { Filters = FilterSettings.Empty.WithPreset("phone", null) }));
            Assert.IsFalse(a.SameSettingsAs(a with { Filters = a.Filters.WithPreset("phone", null) }));
            Assert.IsFalse(a.SameSettingsAs(a with { Filters = a.Filters with { ExemptGroupBots = true } }));
        }
    }
}
