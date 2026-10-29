using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Recovery;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Data recovery offers (Recovery/RecoveryOffers): a dev imports and releases a file, the group's admins get the
    /// offer, untick what they do not want and apply the rest, or none; values changed since are added to or left
    /// alone; members at the warn limit get a Ban button; an offer nobody answers expires and changes nothing; a group
    /// none of whose admins can be reached is told in the group. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class RecoveryScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static string S(HarnessChat chat, string key, params object[] args) =>
            Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, key, args);

        private const long LogChannel = -1007777;

        private static string WarnsItem(HarnessChat chat, HarnessUser member, string before, string after, string totalBefore = null,
            string totalAfter = null, long post = 100) =>
            $@"{{ ""kind"": ""warns"", ""member"": {member.Id}, ""changes"": [
  {{ ""type"": ""hash"", ""key"": ""chat:{chat.ChatId}:warns"", ""field"": ""{member.Id}"", ""before"": {Json(before)}, ""after"": {Json(after)} }}
  {(totalAfter != null ? $@", {{ ""type"": ""hash"", ""key"": ""chat:{chat.ChatId}:totalWarns"", ""field"": ""{member.Id}"", ""before"": {Json(totalBefore)}, ""after"": {Json(totalAfter)} }}" : "")} ],
  ""evidence"": [ {{ ""at"": ""2026-09-10T14:33:00Z"", ""log"": {LogChannel}, ""post"": {post}, ""what"": ""/warn"" }} ] }}";

        private static string ExtraItem(HarnessChat chat, string tag, string before, string after) =>
            $@"{{ ""kind"": ""extra"", ""changes"": [ {{ ""type"": ""hash"", ""key"": ""chat:{chat.ChatId}:extra"", ""field"": ""{tag}"", ""before"": {Json(before)}, ""after"": {Json(after)} }} ],
  ""evidence"": [ {{ ""at"": ""2026-09-01T10:00:00Z"", ""log"": {LogChannel}, ""post"": 200, ""what"": ""/extra"" }} ] }}";

        private static string InlineItem(HarnessChat chat, string bot) =>
            $@"{{ ""kind"": ""inline"", ""changes"": [ {{ ""type"": ""inline"", ""key"": ""chat:{chat.ChatId}:blockedinline"", ""field"": ""{bot}"", ""before"": ""false"", ""after"": ""true"" }} ] }}";

        private static string Json(string value) => value == null ? "null" : $"\"{value}\"";

        private static string File(HarnessChat chat, params string[] items) =>
            $@"{{ ""batch"": ""test-1"", ""from"": ""2026-08-22T04:12:00Z"", ""to"": ""2026-10-26T09:26:00Z"",
  ""groups"": [ {{ ""chat"": {chat.ChatId}, ""items"": [ {string.Join(",", items)} ] }} ] }}";

        /// <summary>Imports and releases the file as a dev, and lets the offer go out.</summary>
        private static async Task OfferAsync(HarnessChat chat, string file)
        {
            var dev = chat.Add("Dev", MemberState.Administrator, id: Constants.Devs[0]);
            chat.Telegram.SetNotStarted(dev.Id);
            var document = chat.Document(dev, "recovery.json", Encoding.UTF8.GetBytes(file));
            await chat.SendAsync(dev, "/recovery", replyTo: document);
            StringAssert.Contains(chat.Replies.Last(), "1 new offers");
            await chat.SendAsync(dev, "/recovery release test-1");
            await RecoveryOffers.CheckOnceAsync();
            await chat.SettleAsync();
        }

        private static Message Offer(HarnessChat chat, HarnessUser admin) =>
            chat.Telegram.SentMessages.Single(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛠") == true);

        private static InlineKeyboardButton[] Buttons(Message message) =>
            message.ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray() ?? Array.Empty<InlineKeyboardButton>();

        private static Task Press(HarnessChat chat, HarnessUser admin, Message message, string op, int n = 0)
        {
            var button = Buttons(message).FirstOrDefault(b => b.CallbackData?.EndsWith($":{op}:{n}") == true);
            Assert.IsNotNull(button, $"No {op}:{n} button under: {message.Text}");
            return chat.PressDataAsync(admin, message, button.CallbackData);
        }

        /// <summary>A member as the bot knows them: their name is stored when they post (CollectStats).</summary>
        private static HarnessUser Known(HarnessChat chat, string name, MemberState state = MemberState.Member)
        {
            var user = chat.Add(name, state);
            chat.Hashes[$"user:{user.Id}", "name"] = name;
            return user;
        }

        private static string Warns(HarnessChat chat, HarnessUser member) => chat.Hashes[$"chat:{chat.ChatId}:warns", member.Id.ToString()];

        private static async Task<(string Operation, long Quantity, string Reason)[]> WarnEvents(HarnessChat chat, HarnessUser member)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(string, long, string)>(@"
SELECT e.operation, e.quantity, e.reason FROM warning_events e
JOIN telegram_accounts a ON a.account_id = e.account_id WHERE a.telegram_user_id = @Id ORDER BY e.warning_event_id",
                new { member.Id })).ToArray();
        }

        [TestMethod]
        public async Task TheOffer_ReachesTheAdmins_AndChangesNothingByItself()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Known(chat, "Member");
            chat.Hashes[$"chat:{chat.ChatId}:warns", member.Id.ToString()] = "1";

            await OfferAsync(chat, File(chat, WarnsItem(chat, member, "1", "3", post: 4242)));

            var offer = Offer(chat, admin);
            StringAssert.Contains(offer.Text, chat.Title);
            StringAssert.Contains(offer.Text, "2026-08-22");
            StringAssert.Contains(offer.Text, "1 → 3");
            StringAssert.Contains(offer.Text, "Member");
            // The log channel post it was read from.
            StringAssert.Contains(offer.Text, Methods.GetChatMessageLink(LogChannel, "4242"));
            Assert.AreEqual(ParseMode.Html, ((Telegram.Bot.Requests.SendMessageRequest)chat.Telegram.Accepted
                .Single(c => c.Method == "sendMessage" && c.ChatId == admin.Id).Request).ParseMode);
            Assert.AreEqual("1", Warns(chat, member), "nothing is applied before an admin says so");
            // Sent once, even when checked again.
            await RecoveryOffers.CheckOnceAsync();
            await chat.SettleAsync();
            Assert.AreEqual(1, chat.Telegram.SentMessages.Count(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛠") == true));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ApplyingTicked_SetsThemAndRecordsTheWarns_AndLeavesTheUnticked()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var kept = chat.Add("Kept");
            var unticked = chat.Add("Unticked");
            chat.Hashes[$"chat:{chat.ChatId}:warns", kept.Id.ToString()] = "1";
            chat.Hashes[$"chat:{chat.ChatId}:warns", unticked.Id.ToString()] = "1";
            await OfferAsync(chat, File(chat,
                WarnsItem(chat, kept, "1", "3", null, "3"),
                WarnsItem(chat, unticked, "1", "2"),
                ExtraItem(chat, "#rules", null, "Be nice"),
                InlineItem(chat, "@spambot")));

            var offer = Offer(chat, admin);
            await Press(chat, admin, offer, "tg", 2);
            StringAssert.Contains(offer.Text, "❌ 2.");
            StringAssert.Contains(offer.Text, S(chat, "recoveryPage", 1, 1, 3, 4));
            await Press(chat, admin, offer, "ap");
            StringAssert.Contains(offer.Text, S(chat, "recoveryConfirm", 3, 4, chat.Title));
            Assert.AreEqual("1", Warns(chat, kept), "asking is not applying");
            await Press(chat, admin, offer, "ok");

            Assert.AreEqual("3", Warns(chat, kept));
            Assert.AreEqual("3", chat.Hashes[$"chat:{chat.ChatId}:totalWarns", kept.Id.ToString()]);
            Assert.AreEqual("1", Warns(chat, unticked), "unticked: left alone");
            Assert.AreEqual("Be nice", chat.Hashes[$"chat:{chat.ChatId}:extra", "#rules"]);
            Assert.IsTrue((await Repositories.InlineBotBlocks.GetAsync(chat.ChatId)).Any(b => b.Value == "spambot"));
            CollectionAssert.AreEqual(new[] { ("add", 2L, RecoveryOffers.Reason) }, await WarnEvents(chat, kept));
            Assert.AreEqual(0, (await WarnEvents(chat, unticked)).Length);
            StringAssert.Contains(offer.Text, S(chat, "recoveryAppliedCount", 3, 4, 1));
            Assert.AreEqual(0, Buttons(offer).Length, "nothing left to press");

            // A second press, by anyone, changes nothing more.
            await chat.PressDataAsync(admin, offer, $"{RecoveryOffers.Trigger}:{chat.ChatId}:1:ok:0");
            Assert.AreEqual("3", Warns(chat, kept));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ValuesChangedSince_AddTheDifferenceToWarns_AndSkipTheRest()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await OfferAsync(chat, File(chat, WarnsItem(chat, member, "1", "3"), ExtraItem(chat, "#rules", "Old", "Older")));
            // Since the offer was worked out: warned twice more, and the extra was rewritten.
            chat.Hashes[$"chat:{chat.ChatId}:warns", member.Id.ToString()] = "2";
            chat.Hashes[$"chat:{chat.ChatId}:extra", "#rules"] = "New";

            var offer = Offer(chat, admin);
            await Press(chat, admin, offer, "ap");
            await Press(chat, admin, offer, "ok");

            Assert.AreEqual("4", Warns(chat, member), "2 now, plus the 2 recovered");
            Assert.AreEqual("New", chat.Hashes[$"chat:{chat.ChatId}:extra", "#rules"], "changed since: kept");
            StringAssert.Contains(offer.Text, S(chat, "recoverySkipped"));
            StringAssert.Contains(offer.Text, "#rules");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ApplyNone_ChangesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await OfferAsync(chat, File(chat, WarnsItem(chat, member, null, "2")));

            await Press(chat, admin, Offer(chat, admin), "no");

            Assert.IsNull(Warns(chat, member));
            StringAssert.Contains(Offer(chat, admin).Text, "❌");
            // Every copy says it, and the other admin cannot apply it any more.
            StringAssert.Contains(Offer(chat, other).Text, "❌");
            await chat.PressDataAsync(other, Offer(chat, other), $"{RecoveryOffers.Trigger}:{chat.ChatId}:1:ok:0");
            Assert.IsNull(Warns(chat, member));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MembersAtTheWarnLimit_AreNotBanned_ButCanBeWithAButton()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = Known(chat, "Admin", MemberState.Administrator);
            var member = Known(chat, "Member");
            var max = Methods.MaxWarns(chat.ChatId);
            await OfferAsync(chat, File(chat, WarnsItem(chat, member, null, max.ToString())));

            var offer = Offer(chat, admin);
            await Press(chat, admin, offer, "ap");
            await Press(chat, admin, offer, "ok");

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, member.Id), "not banned by the recovery");
            StringAssert.Contains(offer.Text, S(chat, "recoveryOverMax", max));
            await Press(chat, admin, offer, "ban", 1);
            await chat.SettleAsync();

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(0, Buttons(offer).Length);
            StringAssert.Contains(offer.Text, "Admin");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnOfferNobodyAnswers_Expires_AndChangesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await OfferAsync(chat, File(chat, WarnsItem(chat, member, null, "2")));
            var offer = Offer(chat, admin);

            // Its time is up (moved back rather than running the clock through two weeks of timers).
            await using (var connection = await chat.Database.OpenConnectionAsync())
                await connection.ExecuteAsync("UPDATE recovery_offers SET expires_at = @At", new { At = chat.Clock.UtcNow.AddMinutes(-1) });
            await RecoveryOffers.CheckOnceAsync();
            await chat.SettleAsync();

            StringAssert.Contains(offer.Text, S(chat, "recoveryExpired"));
            Assert.AreEqual(0, Buttons(offer).Length);
            await chat.PressDataAsync(admin, offer, $"{RecoveryOffers.Trigger}:{chat.ChatId}:1:ok:0");
            Assert.IsNull(Warns(chat, member));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WithNoAdminToTell_TheGroupIsTold_AndAnAdminOpensItInPrivate()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            chat.Telegram.SetNotStarted(admin.Id);
            await OfferAsync(chat, File(chat, WarnsItem(chat, member, null, "2")));

            var notice = chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId && m.Text == S(chat, "recoveryGroupNotice"));
            var url = Buttons(notice).Single().Url;
            Assert.AreEqual($"https://t.me/{HarnessChat.BotUser.Username}?start={RecoveryOffers.StartPrefix}{chat.ChatId}", url);

            Message Private(HarnessUser from) => new Message
            {
                Id = chat.Telegram.NextMessageId(), Chat = new Chat { Id = from.Id, Type = ChatType.Private }, From = from.ToUser(),
                Date = chat.Clock.UtcNow, Text = $"/start {RecoveryOffers.StartPrefix}{chat.ChatId}"
            };
            // A member who is no admin is not shown it.
            await chat.PostAsync(Private(member));
            Assert.AreEqual(S(chat, "recoveryNotAdmin"), chat.Telegram.SentMessages.Last(m => m.Chat.Id == member.Id).Text);

            chat.Telegram.SetNotStarted(admin.Id, false);
            await chat.PostAsync(Private(admin));
            var offer = Offer(chat, admin);
            await Press(chat, admin, offer, "ap");
            await Press(chat, admin, offer, "ok");
            Assert.AreEqual("2", Warns(chat, member));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AFileForAnotherGroupsKeys_IsNotImported()
        {
            await using var chat = await HarnessChat.StartAsync();
            var dev = chat.Add("Dev", MemberState.Administrator, id: Constants.Devs[0]);
            var file = File(chat, ExtraItem(chat, "#rules", null, "x").Replace($"chat:{chat.ChatId}:extra", "chat:-100999:extra"));

            await chat.SendAsync(dev, "/recovery", replyTo: chat.Document(dev, "recovery.json", Encoding.UTF8.GetBytes(file)));

            StringAssert.StartsWith(chat.Replies.Last(), "Not imported");
            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual(0L, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM recovery_offers"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OnlyDevs_CanImport()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            await chat.SendAsync(admin, "/recovery", replyTo: chat.Document(admin, "recovery.json",
                Encoding.UTF8.GetBytes(File(chat, WarnsItem(chat, member, null, "2")))));

            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual(0L, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM recovery_offers"));
        }

        [TestMethod]
        public async Task AMemberWhoIsNoAdmin_CannotPressTheButtons()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await OfferAsync(chat, File(chat, WarnsItem(chat, member, null, "2")));

            await chat.PressDataAsync(member, Offer(chat, admin), $"{RecoveryOffers.Trigger}:{chat.ChatId}:1:ok:0");

            Assert.IsNull(Warns(chat, member));
            StringAssert.Contains(Offer(chat, admin).Text, "✅ 1.");
        }
    }
}
