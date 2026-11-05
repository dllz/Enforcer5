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
        public void AStaticSticker_IsScoredItself_AVideoStickerAsAVideo_AVectorOneByItsPreview()
        {
            var still = new Message { Sticker = new Sticker { FileId = "fs", FileUniqueId = "us", Thumbnail = Size("t", 128, 128) } };
            Assert.AreEqual(new NsfwSigns.Picked("us", "fs", "sticker"), NsfwSigns.Pick(still));
            var preview = new NsfwSigns.Picked("ut", "ft", "thumbnail");
            var video = new Message { Sticker = new Sticker { FileId = "fv", FileUniqueId = "uv", IsVideo = true, Thumbnail = Size("t", 128, 128) } };
            Assert.AreEqual(new NsfwSigns.Picked("uv", "fv", "sticker", MediaHint.Video, preview), NsfwSigns.Pick(video));
            var vector = new Message { Sticker = new Sticker { FileId = "fa", FileUniqueId = "ua", IsAnimated = true, Thumbnail = Size("t", 128, 128) } };
            Assert.AreEqual(preview, NsfwSigns.Pick(vector));
            Assert.IsNull(NsfwSigns.Pick(new Message { Sticker = new Sticker { FileId = "a", FileUniqueId = "a", IsAnimated = true } }));
        }

        [TestMethod]
        public void GifsAndVideos_AreReadAsVideos_WithTheirPreviewFirst_OrByThePreviewAloneWhenTooLarge()
        {
            var preview = new NsfwSigns.Picked("ug", "fg", "thumbnail");
            Assert.AreEqual(new NsfwSigns.Picked("a", "fa", "animation", MediaHint.Video, preview),
                NsfwSigns.Pick(new Message { Animation = new Animation { FileId = "fa", FileUniqueId = "a", Thumbnail = Size("g", 320, 180) } }));
            Assert.AreEqual(new NsfwSigns.Picked("v", "fv", "video", MediaHint.Video, preview),
                NsfwSigns.Pick(new Message { Video = new Video { FileId = "fv", FileUniqueId = "v", FileSize = 5_000_000, Thumbnail = Size("g", 320, 180) } }));
            Assert.AreEqual(new NsfwSigns.Picked("n", "fn", "video_note", MediaHint.Video),
                NsfwSigns.Pick(new Message { VideoNote = new VideoNote { FileId = "fn", FileUniqueId = "n" } }), "no preview: the video alone");
            Assert.AreEqual(preview, NsfwSigns.Pick(new Message { Video = new Video { FileId = "fv", FileUniqueId = "v", FileSize = 21 * 1024 * 1024, Thumbnail = Size("g", 320, 180) } }),
                "over 20 MB: the Bot API would not give it");
            Assert.IsNull(NsfwSigns.Pick(new Message { Video = new Video { FileId = "fv", FileUniqueId = "v", FileSize = 21 * 1024 * 1024 } }));
            Assert.AreEqual(preview, new NsfwSigns.Picked("v", "fv", "video", MediaHint.Video, preview).Still, "a channel's video: its preview");
            Assert.AreEqual(new NsfwSigns.Picked("p", "fp", "photo"), new NsfwSigns.Picked("p", "fp", "photo").Still);
        }

        private static Message DocumentOf(string name, string mime, long? size = 1000, PhotoSize thumbnail = null) =>
            new() { Document = new Document { FileId = "fd", FileUniqueId = "d", FileName = name, MimeType = mime, FileSize = size, Thumbnail = thumbnail } };

        [TestMethod]
        public void Files_AreReadWhenTheyAreImagesOrVideos_OrSayNothingAboutThemselves_NeverWhenTheyAreSomethingElse()
        {
            Assert.AreEqual(new NsfwSigns.Picked("d", "fd", "file"), NsfwSigns.Pick(DocumentOf("a.png", "image/png")));
            Assert.AreEqual(new NsfwSigns.Picked("d", "fd", "file"), NsfwSigns.Pick(DocumentOf(null, "image/heic")));
            Assert.AreEqual(new NsfwSigns.Picked("d", "fd", "file", MediaHint.Video), NsfwSigns.Pick(DocumentOf("clip.webm", "video/webm")));
            Assert.AreEqual(new NsfwSigns.Picked("d", "fd", "file", MediaHint.Video), NsfwSigns.Pick(DocumentOf("clip.mkv", "application/octet-stream")), "the name says video");
            Assert.AreEqual(new NsfwSigns.Picked("d", "fd", "file", MediaHint.Unknown), NsfwSigns.Pick(DocumentOf("photo.bin", "application/octet-stream")), "read to find out");
            Assert.AreEqual(new NsfwSigns.Picked("d", "fd", "file", MediaHint.Unknown), NsfwSigns.Pick(DocumentOf("noname", null)));
            Assert.IsNull(NsfwSigns.Pick(DocumentOf("x.zip", "application/zip")));
            Assert.IsNull(NsfwSigns.Pick(DocumentOf("x.zip", "application/octet-stream")), "a zip is never downloaded");
            Assert.IsNull(NsfwSigns.Pick(DocumentOf("doc.pdf", "application/pdf")));
            Assert.IsNull(NsfwSigns.Pick(DocumentOf("song.mp3", "audio/mpeg")));
            Assert.IsNull(NsfwSigns.Pick(DocumentOf("sticker.tgs", "application/x-tgsticker")));
            Assert.AreEqual(new NsfwSigns.Picked("ut", "ft", "thumbnail"), NsfwSigns.Pick(DocumentOf("doc.pdf", "application/pdf", thumbnail: Size("t", 90, 90))),
                "another file is still judged by its preview, as before");
            Assert.AreEqual(new NsfwSigns.Picked("ut", "ft", "thumbnail"), NsfwSigns.Pick(DocumentOf("big.jpg", "image/jpeg", 30_000_000, Size("t", 90, 90))),
                "too large: its preview");
            Assert.IsNull(NsfwSigns.Pick(new Message { Text = "hello" }));
            Assert.IsNull(NsfwSigns.Pick(null));
        }

        [TestMethod]
        public void AFile_IsKnownByItsFirstBytes()
        {
            static byte[] Bytes(params object[] parts)
            {
                var list = new System.Collections.Generic.List<byte>();
                foreach (var part in parts)
                    if (part is string text) list.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
                    else list.AddRange((byte[])part);
                while (list.Count < 200) list.Add(0x20);
                return list.ToArray();
            }
            Assert.AreEqual(MediaFormat.Image, MediaFiles.Sniff(Png(4, 4, SKColors.Red)));
            Assert.AreEqual(MediaFormat.Image, MediaFiles.Sniff(Bytes(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })));
            Assert.AreEqual(MediaFormat.Image, MediaFiles.Sniff(Convert.FromBase64String(Gif)));
            Assert.AreEqual(MediaFormat.Image, MediaFiles.Sniff(Convert.FromBase64String(WebP)));
            Assert.AreEqual(MediaFormat.Video, MediaFiles.Sniff(Bytes(new byte[] { 0, 0, 0, 0x20 }, "ftypisom")));
            Assert.AreEqual(MediaFormat.Video, MediaFiles.Sniff(Bytes(new byte[] { 0, 0, 0, 0x14 }, "ftypqt  ")));
            Assert.AreEqual(MediaFormat.OtherImage, MediaFiles.Sniff(Bytes(new byte[] { 0, 0, 0, 0x18 }, "ftypheic")));
            Assert.AreEqual(MediaFormat.OtherImage, MediaFiles.Sniff(Bytes(new byte[] { 0, 0, 0, 0x1C }, "ftypavif")));
            Assert.AreEqual(MediaFormat.Video, MediaFiles.Sniff(Bytes(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })), "WebM, MKV");
            Assert.AreEqual(MediaFormat.Video, MediaFiles.Sniff(Bytes("RIFF", new byte[4], "AVI LIST")));
            Assert.AreEqual(MediaFormat.Unknown, MediaFiles.Sniff(Bytes("PK", new byte[] { 3, 4 })), "a zip");
            Assert.AreEqual(MediaFormat.Unknown, MediaFiles.Sniff(Bytes("%PDF-1.7")));
            Assert.AreEqual(MediaFormat.Unknown, MediaFiles.Sniff(System.Text.Encoding.UTF8.GetBytes("genitals")));
            Assert.AreEqual(MediaFormat.Unknown, MediaFiles.Sniff(null));

            // An animated PNG: acTL before the image data (right after the 8-byte signature and the 25-byte IHDR chunk).
            var png = Png(4, 4, SKColors.Red).ToList();
            png.InsertRange(33, new byte[] { 0, 0, 0, 8 }.Concat(System.Text.Encoding.ASCII.GetBytes("acTL")).Concat(new byte[] { 0, 0, 0, 2, 0, 0, 0, 0, 1, 2, 3, 4 }));
            Assert.AreEqual(MediaFormat.AnimatedPng, MediaFiles.Sniff(png.ToArray()));
        }

        [TestMethod]
        public void FramesAreSpreadOverTheWhole_FirstAndLastIncluded()
        {
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, MediaFiles.Spread(3, 8).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 14, 28, 42, 57, 71, 85, 99 }, MediaFiles.Spread(100, 8).ToArray());
            CollectionAssert.AreEqual(new[] { 0 }, MediaFiles.Spread(5, 1).ToArray());
            Assert.AreEqual(0, MediaFiles.Spread(0, 8).Count);
            Assert.AreEqual(1, MediaFiles.Step(null), "unknown length: one a second");
            Assert.AreEqual((10 - 0.2) / 7, MediaFiles.Step(10), 1e-9, "eight frames reach the end of a 10 s video");
            Assert.AreEqual((1 - 0.1) / 7, MediaFiles.Step(1), 1e-9);
        }

        [TestMethod]
        public void FfmpegsOutput_GivesTheLength_AndItsVersionTheSyncOption()
        {
            Assert.AreEqual(62.5, MediaFiles.Duration("Input #0, mov,mp4\n  Duration: 00:01:02.50, start: 0.000000, bitrate: 35 kb/s"));
            Assert.IsNull(MediaFiles.Duration("  Duration: N/A, bitrate: N/A"));
            Assert.IsNull(MediaFiles.Duration(null));
            Assert.IsTrue(MediaFiles.KnowsFpsMode("ffmpeg version 5.1.6-0+deb12u1 Copyright (c) 2000-2024"));
            Assert.IsTrue(MediaFiles.KnowsFpsMode("ffmpeg version n9.0.2-17-g2a571b6068-20260930"));
            Assert.IsTrue(MediaFiles.KnowsFpsMode("ffmpeg version N-112233-gabcdef"));
            Assert.IsFalse(MediaFiles.KnowsFpsMode("ffmpeg version 4.4.2-0ubuntu0.22.04.1"));
            Assert.IsFalse(MediaFiles.KnowsFpsMode("ffmpeg version 5.0.1"));
            var args = string.Join(" ", MediaFiles.FrameArguments("in", "out", 0.7, fpsMode: false));
            Assert.AreEqual(2, MediaFiles.FrameArguments("in", "out", 0.7, fpsMode: false).Count(a => a == "-vsync"));
            StringAssert.Contains(args, "gte(t-prev_selected_t,0.7)");
            StringAssert.Contains(args, "-frames:v 9 " + System.IO.Path.Combine("out", "e%02d.png"));
            StringAssert.Contains(args, "-frames:v 4 " + System.IO.Path.Combine("out", "s%02d.png"));
        }

        [TestMethod]
        public void AVideosScore_IsItsWorstFrame_WithThatFramesReasons()
        {
            var worst = NsfwScore.WorstFrame(new[] { Score(0.1, ("ANUS_EXPOSED", 0.9)), Score(0.96, ("MALE_GENITALIA_EXPOSED", 0.8)), Score(0.5) });
            Assert.AreEqual(0.96, worst.Nsfw);
            CollectionAssert.AreEqual(new[] { "MALE_GENITALIA_EXPOSED" }, worst.Parts.Keys.ToArray(), "the worst frame's parts, not the others'");
            Assert.AreEqual(3, worst.Frames);
            Assert.AreEqual(1, worst.Frame);
            StringAssert.StartsWith(worst.ToString(), "frame 2 of 3, nsfw 0.960");
            Assert.IsNull(NsfwScore.WorstFrame(Array.Empty<NsfwScore>()));
        }

        /// <summary>16×16, red; frames 1 to 8 change one pixel each (drawn over the one before), the last has a green square at the top left.</summary>
        private const string Gif = "R0lGODlhEAAQAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAEAAQAAAIHgABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaJFgQECAgAh+QQBCgACACwPAA8AAQABAIH/AAAUABQAAAAAAAAIBAADBAQAIfkEAQoAAgAsDwAPAAEAAQCB/wAAKAAoAAAAAAAACAQAAwQEACH5BAEKAAIALA8ADwABAAEAgf8AADwAPAAAAAAAAAgEAAMEBAAh+QQBCgACACwPAA8AAQABAIH/AABQAFAAAAAAAAAIBAADBAQAIfkEAQoAAgAsDwAPAAEAAQCB/wAAZABkAAAAAAAACAQAAwQEACH5BAEKAAIALA8ADwABAAEAgf8AAHgAeAAAAAAAAAgEAAMEBAAh+QQBCgACACwPAA8AAQABAIH/AACMAIwAAAAAAAAIBAADBAQAIfkEAQoAAgAsDwAPAAEAAQCB/wAAoACgAAAAAAAACAQAAwQEACH5BAEKAAMALAAAAAAQABAAgQD/AP8AALQAtAAAAAgxAAEIHChwgMGDBgkSRIhQ4UCGBx0WhDhAIgCKFSVitLhRI8aPIEOKHEmypMmTHwUEBAA7";

        /// <summary>The same as an animated WebP.</summary>
        private const string WebP = "UklGRjACAABXRUJQVlA4WAoAAAASAAAADwAADwAAQU5JTQYAAAAAAAAAAABBTk1GLgAAAAAAAAAAAA8AAA8AAGQAAAJWUDhMFgAAAC8PwAMADxDzH/Mfhv/9R/Q//v///wBBTk1GKgAAAAcAAAcAAAEAAAEAAGQAAABWUDhMEgAAAC8BQAAQDxBDMRTzHzhGIKL/IUFOTUYqAAAABwAABwAAAQAAAQAAZAAAAFZQOEwSAAAALwFAABAPEIMyKPMfOEYgov8hQU5NRioAAAAHAAAHAAABAAABAABkAAAAVlA4TBIAAAAvAUAAEA8QwzM88x84RiCi/yFBTk1GKgAAAAcAAAcAAAEAAAEAAGQAAABWUDhMEgAAAC8BQAAQDxADNVDzHzhGIKL/IUFOTUYqAAAABwAABwAAAQAAAQAAZAAAAFZQOEwSAAAALwFAABAPEEM2ZPMfOEYgov8hQU5NRioAAAAHAAAHAAABAAABAABkAAAAVlA4TBIAAAAvAUAAEA8Qgzd48x84RiCi/yFBTk1GKgAAAAcAAAcAAAEAAAEAAGQAAABWUDhMEgAAAC8BQAAQDxDDOIzzHzhGIKL/IUFOTUYqAAAABwAABwAAAQAAAQAAZAAAAFZQOEwSAAAALwFAABAPEAM6oPMfOEYgov8hQU5NRj4AAAAAAAAAAAAPAAAPAABkAAAAVlA4TCYAAAAvD8ADEBcgEEjaH3qNoR3a+Q+YDQTZNpWcz/VvENH/KD4AAADAAA==";

        [TestMethod]
        [DataRow(Gif, DisplayName = "GIF")]
        [DataRow(WebP, DisplayName = "WebP")]
        public void AnAnimatedImage_GivesFramesSpreadOverIt_EachDrawnOverTheOnesBefore(string base64)
        {
            var frames = AnimatedImages.Frames(Convert.FromBase64String(base64), 4);
            Assert.AreEqual(4, frames.Count, "frames 0, 3, 6 and 9 of 10");
            using var first = SKBitmap.Decode(frames[0]);
            using var middle = SKBitmap.Decode(frames[2]);
            using var last = SKBitmap.Decode(frames[3]);
            Assert.AreEqual(new SKColor(255, 0, 0), first.GetPixel(2, 2));
            Assert.AreEqual(new SKColor(255, 0, 0), middle.GetPixel(2, 2), "a frame that changes one pixel still shows the rest");
            Assert.AreEqual(new SKColor(0, 255, 0), last.GetPixel(2, 2), "the last frame");
            Assert.AreEqual(new SKColor(255, 0, 0), last.GetPixel(12, 12));
        }

        [TestMethod]
        public async System.Threading.Tasks.Task TheDecoder_PassesAStillImageAsItIs_AndReadsNothingFromAFileThatIsNeither()
        {
            var decoder = new MediaDecoder();
            var png = Png(4, 4, SKColors.Red);
            Assert.AreSame(png, (await decoder.FramesAsync(png, MediaHint.Unknown, default)).Single(), "one image: the file itself");
            Assert.AreEqual(10, (await decoder.FramesAsync(Convert.FromBase64String(Gif), MediaHint.Image, default)).Count, "a GIF sent as a photo or file: all 10 frames");
            var text = System.Text.Encoding.UTF8.GetBytes("PK not an image at all");
            Assert.IsNull(await decoder.FramesAsync(text, MediaHint.Unknown, default), "a file of unknown kind that is no image");
            Assert.AreSame(text, (await decoder.FramesAsync(text, MediaHint.Image, default)).Single(), "Telegram said photo: the models try");
        }

        /// <summary>ffmpeg itself, when it is there (<c>FfmpegPath</c> or the PATH): a video with a clean first second.</summary>
        [TestMethod]
        public async System.Threading.Tasks.Task Ffmpeg_ReadsFramesOverTheWholeVideo_AndTheCut()
        {
            Ffmpeg.ResetForTests();
            try
            {
                if (!Ffmpeg.Available) Assert.Inconclusive("ffmpeg is not installed here");
                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "enforcer-ffmpeg-test-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(folder);
                try
                {
                    var clip = System.IO.Path.Combine(folder, "clip.mp4");
                    var start = new System.Diagnostics.ProcessStartInfo(Ffmpeg.BinaryForTests) { RedirectStandardError = true };
                    foreach (var arg in new[]
                             {
                                 "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x240:d=1,format=yuv420p", "-f", "lavfi", "-i",
                                 "color=c=red:s=320x240:d=4,format=yuv420p", "-filter_complex", "[0:v][1:v]concat=n=2:v=1[v]", "-map", "[v]", "-r", "25", clip
                             })
                        start.ArgumentList.Add(arg);
                    using var make = System.Diagnostics.Process.Start(start);
                    var errors = await make.StandardError.ReadToEndAsync();
                    await make.WaitForExitAsync();
                    Assert.AreEqual(0, make.ExitCode, errors);

                    var frames = await new MediaDecoder().FramesAsync(await System.IO.File.ReadAllBytesAsync(clip), MediaHint.Unknown, default);
                    Assert.IsTrue(frames.Count is >= MediaFiles.EvenFrames and <= MediaFiles.EvenFrames + 1 + MediaFiles.SceneFrames, $"{frames.Count} frames");
                    var colours = frames.Select(f =>
                    {
                        using var b = SKBitmap.Decode(f);
                        return b.GetPixel(10, 10);
                    }).ToList();
                    Assert.IsTrue(colours[0].Blue > 200 && colours[0].Red < 50, "the first frame is the clean one");
                    Assert.IsTrue(colours.Count(c => c.Red > 200) >= 6, "most frames are after it");
                    Assert.IsTrue(SKBitmap.DecodeBounds(frames[0]).Width == 320, "kept at its size: under 640");

                    Assert.IsNull(await new MediaDecoder().FramesAsync(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, MediaHint.Video, default),
                        "a broken video: nothing read, no error");
                }
                finally
                {
                    System.IO.Directory.Delete(folder, true);
                }
            }
            finally
            {
                Ffmpeg.ResetForTests();
            }
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
