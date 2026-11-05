using System;
using System.IO;
using System.Linq;
using Enforcer5.Detection;
using Enforcer5.Detection.Images;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Detection
{
    /// <summary>
    /// The NSFW check's own logic (Daniel, 2026-11-05): reading the models' outputs, preparing their inputs, which image of a
    /// post is scored, and when a score flags. The models themselves are not in the repo; <see cref="TheModels_ScoreLikeTheReference"/>
    /// runs them when <c>ENFORCER_NSFW_MODELS</c> names their folder.
    /// </summary>
    [TestClass]
    public class NsfwTests
    {
        private static readonly NsfwThresholds Thresholds = new(0.9, 0.7);

        [TestMethod]
        public void TheVitScore_IsTheSoftmaxOfItsNsfwLogit()
        {
            Assert.AreEqual(0.5, NsfwModels.NsfwOf(new[] { 1f, 1f }), 1e-9);
            Assert.AreEqual(1 / (1 + Math.Exp(-4)), NsfwModels.NsfwOf(new[] { -2f, 2f }), 1e-9);
            Assert.AreEqual(0, NsfwModels.NsfwOf(new[] { 1f }));
            Assert.IsTrue(NsfwModels.NsfwOf(new[] { 1000f, -1000f }) < 1e-9, "no overflow");
        }

        [TestMethod]
        public void NudeNet_BestExposedPart_IgnoresCoveredPartsAndFaces()
        {
            const int anchors = 3;
            var output = new float[(4 + NsfwModels.NudeNetLabels.Length) * anchors];
            void Set(string label, int anchor, float score) =>
                output[(4 + Array.IndexOf(NsfwModels.NudeNetLabels, label)) * anchors + anchor] = score;
            Set("FACE_FEMALE", 0, 0.99f);
            Set("FEMALE_BREAST_COVERED", 1, 0.95f);
            Set("BUTTOCKS_EXPOSED", 2, 0.4f);
            Set("MALE_GENITALIA_EXPOSED", 1, 0.8f);
            output[0] = 300; // box values are not scores

            var (part, score) = NsfwModels.BestExposed(output, anchors);
            Assert.AreEqual("MALE_GENITALIA_EXPOSED", part);
            Assert.AreEqual(0.8, score, 1e-6);
            Assert.AreEqual((null, 0.0), NsfwModels.BestExposed(new float[(4 + 18) * anchors], anchors));
        }

        [TestMethod]
        public void AScore_Flags_OnEitherModel_AndNamesThePartOnlyWhenItFlagged()
        {
            Assert.IsFalse(new NsfwScore(0.89, "FEMALE_BREAST_EXPOSED", 0.69).Flagged(Thresholds));
            Assert.IsTrue(new NsfwScore(0.9, null, 0).Flagged(Thresholds));
            Assert.IsNull(new NsfwScore(0.95, "BUTTOCKS_EXPOSED", 0.3).Reason(Thresholds), "the whole image flagged it");
            var part = new NsfwScore(0.1, "ANUS_EXPOSED", 0.75);
            Assert.IsTrue(part.Flagged(Thresholds));
            Assert.AreEqual("ANUS_EXPOSED", part.Reason(Thresholds));
            Assert.AreEqual(0.75, part.Certainty);
        }

        private static PhotoSize Size(string id, int w, int h) => new() { FileId = "f" + id, FileUniqueId = "u" + id, Width = w, Height = h };

        [TestMethod]
        public void APhoto_IsScoredAtTheSmallestSizeOf640OrMore()
        {
            var message = new Message { Photo = new[] { Size("s", 90, 60), Size("m", 320, 213), Size("l", 800, 533), Size("x", 1280, 853) } };
            Assert.AreEqual(new NsfwSigns.Picked("ul", "fl", "photo"), NsfwSigns.Pick(message));
            var small = new Message { Photo = new[] { Size("s", 90, 60), Size("m", 320, 213) } };
            Assert.AreEqual("um", NsfwSigns.Pick(small).UniqueId, "the largest when none is 640");
            Assert.AreEqual("ux", NsfwSigns.ProfileSize(new[] { Size("x", 640, 640), Size("y", 160, 160) }).FileUniqueId);
        }

        [TestMethod]
        public void AStaticSticker_IsScoredItself_AnAnimatedOneByItsPreview()
        {
            var still = new Message { Sticker = new Sticker { FileId = "fs", FileUniqueId = "us", Thumbnail = Size("t", 128, 128) } };
            Assert.AreEqual(new NsfwSigns.Picked("us", "fs", "sticker"), NsfwSigns.Pick(still));
            var video = new Message { Sticker = new Sticker { FileId = "fv", FileUniqueId = "uv", IsVideo = true, Thumbnail = Size("t", 128, 128) } };
            Assert.AreEqual(new NsfwSigns.Picked("ut", "ft", "sticker"), NsfwSigns.Pick(video));
            Assert.IsNull(NsfwSigns.Pick(new Message { Sticker = new Sticker { FileId = "a", FileUniqueId = "a", IsAnimated = true } }));
        }

        [TestMethod]
        public void GifsVideosAndImageFiles_AreScored_TextAndOtherFilesAreNot()
        {
            Assert.AreEqual("thumbnail", NsfwSigns.Pick(new Message { Animation = new Animation { FileId = "a", FileUniqueId = "a", Thumbnail = Size("g", 320, 180) } }).Kind);
            Assert.AreEqual("ug", NsfwSigns.Pick(new Message { Video = new Video { FileId = "v", FileUniqueId = "v", Thumbnail = Size("g", 320, 180) } }).UniqueId);
            Assert.AreEqual("photo", NsfwSigns.Pick(new Message { Document = new Document { FileId = "d", FileUniqueId = "d", MimeType = "image/png", FileSize = 1000 } }).Kind);
            Assert.IsNull(NsfwSigns.Pick(new Message { Document = new Document { FileId = "d", FileUniqueId = "d", MimeType = "application/pdf" } }));
            Assert.IsNull(NsfwSigns.Pick(new Message { Text = "hello" }));
            Assert.IsNull(NsfwSigns.Pick(null));
        }

        private static byte[] Png(int width, int height, SKColor color)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        [TestMethod]
        public void ATransparentSticker_IsReadOnWhite()
        {
            using var bitmap = NsfwModels.Decode(Png(10, 10, SKColors.Transparent));
            Assert.AreEqual(SKColors.White, bitmap.GetPixel(5, 5));
            Assert.IsNull(NsfwModels.Decode(new byte[] { 1, 2, 3 }), "not an image");
            Assert.IsNull(NsfwModels.Decode(null));
        }

        [TestMethod]
        public void TheVitInput_Is384Square_ChannelsFirst_ScaledToMinusOneToOne()
        {
            using var bitmap = NsfwModels.Decode(Png(50, 20, new SKColor(255, 0, 0)));
            var tensor = NsfwModels.VitTensor(bitmap);
            const int plane = NsfwModels.VitSize * NsfwModels.VitSize;
            Assert.AreEqual(3 * plane, tensor.Length);
            Assert.AreEqual(1f, tensor[plane / 2], 1e-5, "red");
            Assert.AreEqual(-1f, tensor[plane + plane / 2], 1e-5, "green");
            Assert.AreEqual(-1f, tensor[2 * plane + plane / 2], 1e-5, "blue");
        }

        [TestMethod]
        public void TheNudeNetInput_PadsToASquareWithBlackAtTheBottom()
        {
            using var bitmap = NsfwModels.Decode(Png(40, 20, new SKColor(255, 255, 255)));
            var tensor = NsfwModels.NudeNetTensor(bitmap);
            const int side = NsfwModels.NudeNetSize;
            Assert.AreEqual(3 * side * side, tensor.Length);
            Assert.AreEqual(1f, tensor[10 * side + 10], 1e-5, "the image at the top left");
            Assert.AreEqual(0f, tensor[(side - 10) * side + 10], 1e-5, "black padding below it");
        }

        [TestMethod]
        [TestCategory("Models")]
        public void TheModels_ScoreLikeTheReference()
        {
            var folder = Environment.GetEnvironmentVariable("ENFORCER_NSFW_MODELS");
            if (string.IsNullOrEmpty(folder)) Assert.Inconclusive("ENFORCER_NSFW_MODELS is not set.");
            using var models = NsfwModels.Load(folder, 2, out var why);
            Assert.IsNotNull(models, why);
            var images = Environment.GetEnvironmentVariable("ENFORCER_NSFW_IMAGES");
            foreach (var path in (images ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine($"{Path.GetFileName(path)}: {models.Score(File.ReadAllBytes(path))}");
            Assert.AreEqual(NsfwScore.None, models.Score(new byte[] { 1, 2, 3 }));
            var plain = models.Score(Png(300, 200, new SKColor(40, 90, 200)));
            Assert.IsFalse(plain.Flagged(Thresholds), plain.ToString());
        }
    }
}
