using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Enforcer5.Detection.Ml;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// similar_to_banned (build step 15): new members' messages and images compared, by the model service's vectors, with the
    /// ones that got someone removed. The service is a stand-in (<see cref="FakeModels"/>) that maps words to fixed vectors.
    /// Needs the test database with pgvector (subprod has it).
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class SimilarToBannedScenarios
    {
        /// <summary>
        /// Texts and images by the first word of <see cref="Topics"/> they contain: "airdrop" and "giveaway" are 0.90 alike
        /// (a reworded scam), "almost" 0.80 to "airdrop" (a near miss), "game" and "match" 0.90, an "edited" poster 0.80 to the
        /// "poster"; anything else stands alone.
        /// </summary>
        private sealed class FakeModels : IMlClient
        {
            private static readonly (string Word, float[] Vector)[] Topics =
            {
                ("airdrop", new[] { 1f, 0, 0, 0, 0 }),
                ("giveaway", new[] { 0.9f, 0.43589f, 0, 0, 0 }),
                ("almost", new[] { 0.8f, 0, 0.6f, 0, 0 }),
                ("game", new[] { 0, 0, 0, 1f, 0 }),
                ("match", new[] { 0, 0.43589f, 0, 0.9f, 0 }),
                ("edited", new[] { 0.8f, 0.6f, 0, 0, 0 }),
                ("poster", new[] { 1f, 0, 0, 0, 0 })
            };

            internal readonly List<string> Texts = new();
            internal int Images;
            internal bool Down;

            public string TextModel => "fake-text";
            public string ImageModel => "fake-image";

            private static float[] Of(string content) =>
                Topics.FirstOrDefault(t => content.Contains(t.Word, StringComparison.OrdinalIgnoreCase)).Vector ?? new[] { 0, 0, 0, 0, 1f };

            public Task<float[][]> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancel)
            {
                if (Down) throw new System.Net.Http.HttpRequestException("Infinity answered 503: down");
                Texts.AddRange(texts);
                return Task.FromResult(texts.Select(Of).ToArray());
            }

            public Task<float[][]> EmbedImagesAsync(IReadOnlyList<MlImage> images, CancellationToken cancel)
            {
                if (Down) throw new System.Net.Http.HttpRequestException("Infinity answered 503: down");
                Images += images.Count;
                return Task.FromResult(images.Select(i => Of(Encoding.UTF8.GetString(i.Bytes))).ToArray());
            }
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static async Task<(HarnessChat Chat, FakeModels Models)> StartAsync()
        {
            var chat = await HarnessChat.StartAsync();
            var models = new FakeModels();
            Embeddings.Client = models;
            return (chat, models);
        }

        private static HarnessUser Established(HarnessChat chat, string name)
        {
            var user = chat.Add(name);
            chat.Hashes[$"{chat.ChatId}:users:{user.Id}", "msgs"] = "5000";
            return user;
        }

        private static async Task<(long User, string Mode, double Similarity, string Kind)[]> Hits(HarnessChat chat)
        {
            await chat.SettleAsync();
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string, double, string)>(@"
SELECT a.telegram_user_id, d.mode, d.similarity, d.evidence ->> 'kind' FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE d.sign = 'similar_to_banned' ORDER BY d.detection_id")).ToArray();
        }

        private static async Task<string[]> Vectors(HarnessChat chat)
        {
            await chat.SettleAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>(@"
SELECT a.telegram_user_id || ':' || e.purpose || ':' || e.model || ':' || array_length(e.embedding::real[], 1)
FROM message_embeddings e JOIN telegram_accounts a ON a.account_id = e.account_id ORDER BY e.embedding_id")).ToArray();
        }

        private static Message Photo(HarnessChat chat, HarnessUser from, string uniqueId, string content)
        {
            chat.Files.Add("small-" + uniqueId, Encoding.UTF8.GetBytes(content));
            var message = chat.Photo(from, uniqueId);
            message.Photo = new[]
            {
                new PhotoSize { FileId = "small-" + uniqueId, FileUniqueId = "s-" + uniqueId, Width = 320, Height = 320 },
                new PhotoSize { FileId = "large-" + uniqueId, FileUniqueId = uniqueId, Width = 1280, Height = 1280 }
            };
            return message;
        }

        [TestMethod]
        public async Task AScamReworded_ByANewMember_IsWatched_AfterAnAdminBannedTheFirst_AndANearMissIsKept()
        {
            var (chat, models) = await StartAsync();
            await using var _ = chat;
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "Claim the airdrop at my channel right now friends");
            await chat.SendAsync(admin, "/ban spam", replyTo: spam);
            await chat.AdvanceAsync(SimilarToBanned.RefreshEvery + TimeSpan.FromMinutes(1));

            var newcomer = chat.Add("Newcomer");
            var regular = Established(chat, "Regular");
            var other = chat.Add("Other");
            var reworded = await chat.SendAsync(newcomer, "The giveaway is live, tokens for everyone who writes me");
            await chat.SendAsync(regular, "The giveaway is live, tokens for everyone who writes me again");
            await chat.SendAsync(other, "This is almost the same kind of thing, write me");
            await chat.SendAsync(newcomer, "hi all"); // too short to compare

            var hits = await Hits(chat);
            Assert.AreEqual(1, hits.Length, string.Join(", ", hits));
            Assert.AreEqual((newcomer.Id, "trial", "text"), (hits[0].User, hits[0].Mode, hits[0].Kind), "watching: recorded as a trial");
            Assert.AreEqual(0.9, hits[0].Similarity, 0.001);
            Assert.IsTrue(chat.Telegram.MessagesFrom(chat.ChatId, newcomer.Id).Contains(reworded.MessageId), "nothing done while watching");
            Assert.IsFalse(chat.Telegram.SentMessages.Any(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true), "nor reported");
            Assert.IsFalse(models.Texts.Any(t => t.EndsWith("again")), "an established member's text is never sent");

            CollectionAssert.AreEqual(new[]
            {
                $"{spammer.Id}:decision:fake-text:5", $"{newcomer.Id}:newcomer:fake-text:5", $"{other.Id}:newcomer:fake-text:5"
            }, await Vectors(chat), "the spam was embedded as a newcomer's first, then marked the decision's; never any text");
            await using var connection = await chat.Database.OpenConnectionAsync();
            var near = await connection.QuerySingleAsync<(double, string)>(
                "SELECT similarity, evidence ->> 'kind' FROM detection_near_misses WHERE sign = 'similar_to_banned'");
            Assert.AreEqual((0.8, "text"), (Math.Round(near.Item1, 3), near.Item2));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Live_CountsLikeAnySign_AndARemovalForAGroupsOwnRule_CountsOnlyThere()
        {
            var (chat, _) = await StartAsync();
            await using var __ = chat;
            SimilarToBanned.LiveAllowed = true;
            var other = chat.Group(-1009999);
            await chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { AiText = AiModes.Live } });
            await other.ChangeSettingsAsync(p => p with { Engine = p.Engine with { AiText = AiModes.Live } });
            var admin = chat.Add("Admin", MemberState.Administrator);
            var talker = chat.Add("Talker");
            var offTopic = await chat.SendAsync(talker, "Anyone wants to play the game tonight with us here");
            await chat.SendAsync(admin, "/kick off topic", replyTo: offTopic); // not abuse: this group's own rule
            await chat.AdvanceAsync(SimilarToBanned.RefreshEvery + TimeSpan.FromMinutes(1));

            var here = chat.Add("Here");
            var there = other.Add("There");
            await chat.SendAsync(here, "Who is up for a match tonight, write me please");
            await other.SendAsync(there, "Who is up for a match tonight, write me please");

            var hits = await Hits(chat);
            Assert.AreEqual(1, hits.Length, string.Join(", ", hits));
            Assert.AreEqual(here.Id, hits[0].User, "not in the other group");
            Assert.AreEqual("observe", hits[0].Mode, "live: a sign like any other, so observed while the group's first weeks last");
            Assert.AreEqual(2, MemberScores.Of(chat.ChatId, here.Id), "it counts towards the score");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnEditedCopyOfABannedImage_IsWatched_AndAnUnrelatedOneIsNot()
        {
            var (chat, models) = await StartAsync();
            await using var _ = chat;
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var poster = await chat.PostAsync(Photo(chat, member, "ad", "poster"));
            await chat.SendAsync(admin, "/block", replyTo: poster);
            await chat.AdvanceAsync(SimilarToBanned.RefreshEvery + TimeSpan.FromMinutes(1));

            var newcomer = chat.Add("Newcomer");
            await chat.PostAsync(Photo(chat, newcomer, "ad2", "poster edited"));
            await chat.PostAsync(Photo(chat, newcomer, "cat", "a cat"));

            var hits = await Hits(chat);
            Assert.AreEqual(1, hits.Length, string.Join(", ", hits));
            Assert.AreEqual((newcomer.Id, "trial", "image"), (hits[0].User, hits[0].Mode, hits[0].Kind));
            Assert.AreEqual(0.8, hits[0].Similarity, 0.001);
            await using var connection = await chat.Database.OpenConnectionAsync();
            CollectionAssert.AreEquivalent(new[] { "ad", "ad2", "cat" },
                (await connection.QueryAsync<string>("SELECT media_unique_id FROM media_vectors WHERE model = 'fake-image'")).ToArray(),
                "by the file's id, as banned media knows it, from the small size (the large one cannot be downloaded here)");
            Assert.AreEqual(3, models.Images);

            // The same file again costs nothing.
            await chat.PostAsync(Photo(chat, chat.Add("Another"), "cat", "a cat"));
            await chat.SettleAsync();
            Assert.AreEqual(3, models.Images);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Off_SendsNothing_TheMenuCyclesTheModes_AndTheServiceDown_OnlyLeavesTheVectorsOut()
        {
            var (chat, models) = await StartAsync();
            await using var _ = chat;
            var admin = chat.Add("Admin", MemberState.Administrator);
            var lang = Methods.GetGroupLanguage(chat.ChatId).Doc;
            Assert.IsTrue(Commands.GenDetectionMenu(chat.ChatId, lang).InlineKeyboard.SelectMany(r => r)
                .Any(b => b.CallbackData == $"aimenu:{chat.ChatId}"));
            var menu = await chat.SendAsync(admin, "/menu");
            await chat.PressDataAsync(admin, menu, $"aitoggle:{chat.ChatId}:text");
            Assert.AreEqual(AiModes.Off, DetectionSettings.For(chat.ChatId).Engine.TextSimilarity, "watching, then off: live is not offered yet");
            await chat.SendAsync(chat.Add("Newcomer"), "The giveaway is live, tokens for everyone who writes me");
            await chat.SettleAsync();
            Assert.AreEqual(0, models.Texts.Count, "off: nothing sent");

            await chat.PressDataAsync(admin, menu, $"aitoggle:{chat.ChatId}:text");
            Assert.AreEqual(AiModes.Watch, DetectionSettings.For(chat.ChatId).Engine.TextSimilarity);
            models.Down = true;
            await chat.SendAsync(chat.Add("First"), "The giveaway is live, tokens for everyone who writes me");
            await chat.SendAsync(chat.Add("Second"), "Claim the airdrop at my channel right now friends");
            await chat.SettleAsync();
            Assert.AreEqual(1, Embeddings.Requests, "left alone for a minute after it failed");
            Assert.AreEqual(1, chat.Errors.Count, string.Join("\n", chat.Errors));
            StringAssert.Contains(chat.Errors.Single(), "left alone");
            Assert.AreEqual(0, (await Vectors(chat)).Length);

            models.Down = false;
            await chat.AdvanceAsync(Embeddings.PauseAfterFailure);
            await chat.SendAsync(chat.Add("Third"), "Claim the airdrop at my channel right now friends");
            Assert.AreEqual(1, (await Vectors(chat)).Length, "back after the pause");
        }

        [TestMethod]
        public async Task Retention_DeletesNewcomersVectors_AndKeepsThoseOfRemovals_Anonymous()
        {
            var (chat, _) = await StartAsync();
            await using var __ = chat;
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "Claim the airdrop at my channel right now friends");
            await chat.SendAsync(admin, "/ban spam", replyTo: spam);
            await chat.SendAsync(chat.Add("Newcomer"), "The giveaway is live, tokens for everyone who writes me");
            Assert.AreEqual(2, (await Vectors(chat)).Length);

            var later = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero) + TimeSpan.FromDays(400);
            var result = await Data.Repositories.Embeddings.ApplyRetentionAsync(later, 365, SimilarToBanned.ReferenceDays, SimilarToBanned.ImageDays);
            Assert.AreEqual((1, 1, 0), (result.MessagesDeleted, result.MessagesAnonymised, result.MediaDeleted));
            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual("decision:", await connection.ExecuteScalarAsync<string>(
                "SELECT purpose || ':' || coalesce(account_id::text, '') FROM message_embeddings"));

            result = await Data.Repositories.Embeddings.ApplyRetentionAsync(later + TimeSpan.FromDays(365), 365,
                SimilarToBanned.ReferenceDays, SimilarToBanned.ImageDays);
            Assert.AreEqual(1, result.MessagesDeleted, "gone after 2 years");
            AssertNoErrors(chat);
        }
    }
}
