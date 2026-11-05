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
        private static readonly NsfwThresholds Thresholds = new(0.9, 0.5);

        [TestMethod]
        public void TheVitScore_IsTheSoftmaxOfItsNsfwLogit()
        {
            Assert.AreEqual(0.5, NsfwModels.NsfwOf(new[] { 1f, 1f }), 1e-9);
            Assert.AreEqual(1 / (1 + Math.Exp(-4)), NsfwModels.NsfwOf(new[] { -2f, 2f }), 1e-9);
            Assert.AreEqual(0, NsfwModels.NsfwOf(new[] { 1f }));
            Assert.IsTrue(NsfwModels.NsfwOf(new[] { 1000f, -1000f }) < 1e-9, "no overflow");
        }

        [TestMethod]
        public void NudeNet_ScoresEachExposedPart_AndIgnoresCoveredPartsAndFaces()
        {
            const int anchors = 3;
            var output = new float[(4 + NsfwModels.NudeNetLabels.Length) * anchors];
            void Set(string label, int anchor, float score) =>
                output[(4 + Array.IndexOf(NsfwModels.NudeNetLabels, label)) * anchors + anchor] = score;
            Set("FACE_FEMALE", 0, 0.99f);
            Set("FEMALE_BREAST_COVERED", 1, 0.95f);
            Set("BUTTOCKS_EXPOSED", 2, 0.4f);
            Set("BUTTOCKS_EXPOSED", 0, 0.3f);
            Set("MALE_GENITALIA_EXPOSED", 1, 0.8f);
            Set("ANUS_EXPOSED", 1, 0.01f); // under the floor
            output[0] = 300; // box values are not scores

            var parts = NsfwModels.ExposedScores(output, anchors);
            CollectionAssert.AreEquivalent(new[] { "MALE_GENITALIA_EXPOSED", "BUTTOCKS_EXPOSED" }, parts.Keys.ToArray());
            Assert.AreEqual(0.8, parts["MALE_GENITALIA_EXPOSED"], 1e-4);
            Assert.AreEqual(0.4, parts["BUTTOCKS_EXPOSED"], 1e-4, "the best anchor");
            Assert.AreEqual(0, NsfwModels.ExposedScores(new float[(4 + 18) * anchors], anchors).Count);
        }

        private static NsfwScore Score(double nsfw, params (string Part, double Score)[] parts) =>
            new(nsfw, parts.ToDictionary(p => p.Part, p => p.Score));

        [TestMethod]
        public void OnlyTheVitScoreFlags_NudeNetNamesTheReasons()
        {
            Assert.IsFalse(Score(0.89, ("MALE_GENITALIA_EXPOSED", 0.99)).Flagged(Thresholds), "NudeNet never flags on its own");
            var both = Score(0.95, ("BUTTOCKS_EXPOSED", 0.6), ("FEMALE_BREAST_EXPOSED", 0.8), ("ANUS_EXPOSED", 0.3));
            Assert.IsTrue(both.Flagged(Thresholds));
            CollectionAssert.AreEqual(new[] { ("FEMALE_BREAST_EXPOSED", 0.8), ("BUTTOCKS_EXPOSED", 0.6) }, both.Reasons(Thresholds).ToArray());
            Assert.AreEqual("FEMALE_BREAST_EXPOSED", both.Part);
            Assert.AreEqual(0.8, both.PartScore);
            Assert.AreEqual(0, Score(0.97).Reasons(Thresholds).Count);
            Assert.IsNull(NsfwScore.None.Part);
        }

        [TestMethod]
        public void AProfilesHighestScores_AreTakenPerKind()
        {
            var max = NsfwScore.Max(new[] { Score(0.2, ("ANUS_EXPOSED", 0.7)), Score(0.9, ("ANUS_EXPOSED", 0.1), ("BUTTOCKS_EXPOSED", 0.4)) });
            Assert.AreEqual(0.9, max.Nsfw);
            Assert.AreEqual(0.7, max.Parts["ANUS_EXPOSED"]);
            Assert.AreEqual(0.4, max.Parts["BUTTOCKS_EXPOSED"]);
            Assert.AreSame(NsfwScore.None, NsfwScore.Max(Array.Empty<NsfwScore>()));
        }

        [TestMethod]
        public void TheReportSaysWhy_OrThatNoExposedPartWasSeen()
        {
            var lang = System.Xml.Linq.XDocument.Load(TestEnvironment.EnglishXmlPath);
            Assert.AreEqual("97% NSFW: exposed genitals 92%, exposed breasts 64%",
                NsfwSigns.Describe(lang, Score(0.968, ("MALE_GENITALIA_EXPOSED", 0.92), ("FEMALE_BREAST_EXPOSED", 0.64), ("BUTTOCKS_EXPOSED", 0.2)), Thresholds));
            Assert.AreEqual("96% NSFW: no exposed body parts seen", NsfwSigns.Describe(lang, Score(0.96, ("ANUS_EXPOSED", 0.4)), Thresholds));
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
            Assert.AreSame(NsfwScore.None, models.Score(new byte[] { 1, 2, 3 }));
            var plain = models.Score(Png(300, 200, new SKColor(40, 90, 200)));
            Assert.IsFalse(plain.Flagged(Thresholds), plain.ToString());
        }

        /// <summary>
        /// 2026-11-05: the bot died with SIGSEGV during /nsfwscan. A temporary RunOptions was finalized while ONNX Runtime ran
        /// on it. Only a Release build collects it that early (Debug keeps locals alive): run with <c>-c Release</c>; it
        /// crashed the test host within 150 images before the fix.
        /// </summary>
        [TestMethod]
        [TestCategory("Models")]
        public void TheModels_SurviveTheGarbageCollector()
        {
            var folder = Environment.GetEnvironmentVariable("ENFORCER_NSFW_MODELS");
            if (string.IsNullOrEmpty(folder)) Assert.Inconclusive("ENFORCER_NSFW_MODELS is not set.");
            using var models = NsfwModels.Load(folder, 2, out var why);
            Assert.IsNotNull(models, why);
            var image = Png(320, 320, new SKColor(200, 150, 120));
            var stop = 0;
            var collector = new System.Threading.Thread(() =>
            {
                while (System.Threading.Volatile.Read(ref stop) == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    System.Threading.Thread.Sleep(1);
                }
            });
            collector.Start();
            try
            {
                for (var i = 0; i < 300; i++) models.Score(image);
            }
            finally
            {
                System.Threading.Volatile.Write(ref stop, 1);
                collector.Join();
            }
        }
    }
}
