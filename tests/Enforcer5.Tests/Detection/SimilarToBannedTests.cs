using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Detection.Ml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Detection
{
    /// <summary>
    /// similar_to_banned's own logic (build step 15): the modes, which image and text of a post are embedded, and which
    /// references a group compares with. No database, no service.
    /// </summary>
    [TestClass]
    public class SimilarToBannedTests
    {
        [TestMethod]
        public void Modes_DefaultToWatching_AndLiveIsOnlyOfferedWhenAllowed()
        {
            Assert.AreEqual(AiModes.Watch, AiModes.Of(null));
            Assert.AreEqual(AiModes.Watch, AiModes.Of("nonsense"));
            Assert.AreEqual(AiModes.Off, AiModes.Of("off"));
            Assert.AreEqual(AiModes.Watch, EngineSettings.Default.TextSimilarity);
            Assert.AreEqual(AiModes.Watch, EngineSettings.Default.ImageSimilarity);

            Assert.AreEqual(AiModes.Off, AiModes.Next(AiModes.Watch, liveAllowed: false));
            Assert.AreEqual(AiModes.Watch, AiModes.Next(AiModes.Off, liveAllowed: false));
            Assert.AreEqual(AiModes.Live, AiModes.Next(AiModes.Watch, liveAllowed: true));
            Assert.AreEqual(AiModes.Off, AiModes.Next(AiModes.Live, liveAllowed: true));
        }

        [TestMethod]
        public void Pick_TakesTheSizeAbove320_ByTheFilesId_AndPreviewsOfMovingThings()
        {
            var photo = new Message
            {
                Photo = new[]
                {
                    new PhotoSize { FileId = "s", FileUniqueId = "us", Width = 90, Height = 67 },
                    new PhotoSize { FileId = "m", FileUniqueId = "um", Width = 320, Height = 240 },
                    new PhotoSize { FileId = "l", FileUniqueId = "ul", Width = 1280, Height = 960 }
                }
            };
            Assert.AreEqual(new SimilarToBanned.PickedImage("ul", "m", "photo", "image/jpeg"), SimilarToBanned.Pick(photo),
                "known by the largest size's id, as labels and banned media know it; the 320 size is downloaded");

            var sticker = new Message { Sticker = new Sticker { FileId = "st", FileUniqueId = "ust", Width = 512, Height = 512 } };
            Assert.AreEqual(new SimilarToBanned.PickedImage("ust", "st", "sticker", "image/webp"), SimilarToBanned.Pick(sticker));

            var animated = new Message
            {
                Sticker = new Sticker { FileId = "a", FileUniqueId = "ua", IsAnimated = true, Thumbnail = new PhotoSize { FileId = "at", FileUniqueId = "uat" } }
            };
            Assert.AreEqual(new SimilarToBanned.PickedImage("ua", "at", "sticker", "image/jpeg"), SimilarToBanned.Pick(animated));

            var gif = new Message { Animation = new Animation { FileId = "g", FileUniqueId = "ug", Thumbnail = new PhotoSize { FileId = "gt", FileUniqueId = "ugt" } } };
            Assert.AreEqual(new SimilarToBanned.PickedImage("ug", "gt", "thumbnail", "image/jpeg"), SimilarToBanned.Pick(gif));

            var big = new Message
            {
                Document = new Document { FileId = "d", FileUniqueId = "ud", MimeType = "image/png", FileSize = 9_000_000, Thumbnail = new PhotoSize { FileId = "dt" } }
            };
            Assert.AreEqual(new SimilarToBanned.PickedImage("ud", "dt", "thumbnail", "image/jpeg"), SimilarToBanned.Pick(big));

            Assert.IsNull(SimilarToBanned.Pick(new Message { Voice = new Voice { FileId = "v", FileUniqueId = "uv" } }));
            Assert.IsNull(SimilarToBanned.Pick(new Message { Text = "hello" }));
        }

        [TestMethod]
        public void TextOf_TakesTextsAndCaptionsOf20Letters()
        {
            Assert.IsNull(SimilarToBanned.TextOf(new Message { Text = "hello everyone 123456789 !!!" }), "14 letters");
            Assert.AreEqual("Join my channel for free signals", SimilarToBanned.TextOf(new Message { Text = "Join my channel for free signals" }));
            Assert.AreEqual("/start 77 @freecoinsbot claim your reward",
                SimilarToBanned.TextOf(new Message { Caption = "/start 77 @freecoinsbot claim your reward" }), "commands too: the bot-referral spam is one");
        }

        [TestMethod]
        public void Closest_ComparesWithAbuseFromEveryGroup_AndOtherDecisionsOnlyInTheirOwn()
        {
            float[] V(float x, float y) => Vectors.Normalized(new[] { x, y });
            var references = new[]
            {
                new EmbeddingReference("text", -2, Global: false, 5, "1", V(1, 0)),   // another group's own rule
                new EmbeddingReference("text", -2, Global: true, 6, "2", V(1, 1)),    // abuse elsewhere
                new EmbeddingReference("text", -1, Global: false, 7, "3", V(0, 1))    // this group's own rule
            };

            var here = SimilarToBanned.Closest(references, -1, V(1, 0.05f), null)!.Value;
            Assert.AreEqual("2", here.Reference.Ref, "the other group's own rule is not compared with");
            Assert.AreEqual(Vectors.Cosine(V(1, 0.05f), V(1, 1)), here.Similarity, 1e-6);
            Assert.AreEqual("1", SimilarToBanned.Closest(references, -2, V(1, 0.05f), null)!.Value.Reference.Ref);
            Assert.IsNull(SimilarToBanned.Closest(references.Take(1).ToArray(), -1, V(1, 0), null));

            // The same file banned in this group is banned_media's; banned elsewhere as abuse, it is a copy.
            var files = new[]
            {
                new EmbeddingReference("image", -1, Global: false, null, "file", V(1, 0)),
                new EmbeddingReference("image", -3, Global: true, null, "file", V(1, 0))
            };
            Assert.AreEqual(-3, SimilarToBanned.Closest(files, -1, V(1, 0), "file")!.Value.Reference.ChatId);
        }
    }
}
