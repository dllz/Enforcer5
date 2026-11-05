using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enforcer5.Helpers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Enforcer5.Detection.Images
{
    /// <summary>
    /// The two NSFW models, on the CPU with ONNX Runtime (Daniel, 2026-11-05):
    /// <list type="bullet">
    /// <item><b>ViT</b> (<c>AdamCodd/vit-base-nsfw-detector</c>, Apache 2.0, the int8 ONNX export): one SFW/NSFW score for the
    /// whole image; trained on photos, 3D and drawn images, so it covers stickers. 384×384, mean and std 0.5.</item>
    /// <item><b>NudeNet</b> 320n (YOLOv8, the <c>nudenet</c> 3.4.2 package): exposed body parts with a score each. 320×320,
    /// the image padded to a square with black at the right and bottom first, as the package does.</item>
    /// </list>
    /// The files are not in the repo or the image: <c>NsfwModelPath</c> names the folder holding <see cref="VitFile"/> and
    /// <see cref="NudeNetFile"/>; unset, or a file missing, the checks are off. Both sessions are thread-safe; the callers
    /// (<see cref="ImageScans"/>) keep it to one image at a time, with <c>NsfwThreads</c> threads (default 2).
    /// Python reference with the same preprocessing: <c>/workspace/scratch/nsfw/ref.py</c> on the quasi box.
    /// </summary>
    internal sealed class NsfwModels : INsfwModel, IDisposable
    {
        internal const string VitFile = "vit-nsfw-int8.onnx";
        internal const string NudeNetFile = "nudenet-320n.onnx";
        internal const int VitSize = 384;
        internal const int NudeNetSize = 320;

        /// <summary>NudeNet's classes in its output order (nudenet 3.4.2).</summary>
        internal static readonly string[] NudeNetLabels =
        {
            "FEMALE_GENITALIA_COVERED", "FACE_FEMALE", "BUTTOCKS_EXPOSED", "FEMALE_BREAST_EXPOSED", "FEMALE_GENITALIA_EXPOSED",
            "MALE_BREAST_EXPOSED", "ANUS_EXPOSED", "FEET_EXPOSED", "BELLY_COVERED", "FEET_COVERED", "ARMPITS_COVERED",
            "ARMPITS_EXPOSED", "FACE_MALE", "BELLY_EXPOSED", "MALE_GENITALIA_EXPOSED", "ANUS_COVERED", "FEMALE_BREAST_COVERED",
            "BUTTOCKS_COVERED"
        };

        /// <summary>The parts that flag an image (Daniel's list); covered parts, faces, feet and bellies never do.</summary>
        internal static readonly HashSet<string> ExposedParts = new()
        {
            "MALE_GENITALIA_EXPOSED", "FEMALE_BREAST_EXPOSED", "FEMALE_GENITALIA_EXPOSED", "ANUS_EXPOSED", "BUTTOCKS_EXPOSED"
        };

        private readonly InferenceSession _vit;
        private readonly InferenceSession _nudeNet;
        private readonly string _vitInput;
        private readonly string _nudeNetInput;

        public string Version => "vit-nsfw-int8+nudenet-320n";

        private NsfwModels(InferenceSession vit, InferenceSession nudeNet)
        {
            _vit = vit;
            _nudeNet = nudeNet;
            _vitInput = vit.InputMetadata.Keys.First();
            _nudeNetInput = nudeNet.InputMetadata.Keys.First();
        }

        /// <summary>The models in <paramref name="folder"/>; null (and why, in <paramref name="why"/>) when they are not there.</summary>
        internal static NsfwModels Load(string folder, int threads, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(folder))
            {
                why = "NsfwModelPath is not set";
                return null;
            }
            var vit = Path.Combine(folder, VitFile);
            var nudeNet = Path.Combine(folder, NudeNetFile);
            var missing = new[] { vit, nudeNet }.Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0)
            {
                why = "missing " + string.Join(", ", missing);
                return null;
            }
            var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Max(1, threads),
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            InferenceSession first = null;
            try
            {
                first = new InferenceSession(vit, options);
                return new NsfwModels(first, new InferenceSession(nudeNet, options));
            }
            catch
            {
                first?.Dispose();
                throw;
            }
        }

        public NsfwScore Score(byte[] image)
        {
            using var bitmap = Decode(image);
            if (bitmap == null) return NsfwScore.None;

            float[] vitOut;
            using (var input = OrtValue.CreateTensorValueFromMemory(VitTensor(bitmap), new long[] { 1, 3, VitSize, VitSize }))
            using (var results = _vit.Run(new RunOptions(), new Dictionary<string, OrtValue> { [_vitInput] = input }, _vit.OutputNames))
                vitOut = results[0].GetTensorDataAsSpan<float>().ToArray();

            float[] nudeOut;
            long[] shape;
            using (var input = OrtValue.CreateTensorValueFromMemory(NudeNetTensor(bitmap), new long[] { 1, 3, NudeNetSize, NudeNetSize }))
            using (var results = _nudeNet.Run(new RunOptions(), new Dictionary<string, OrtValue> { [_nudeNetInput] = input }, _nudeNet.OutputNames))
            {
                nudeOut = results[0].GetTensorDataAsSpan<float>().ToArray();
                shape = results[0].GetTensorTypeAndShape().Shape;
            }
            var (part, partScore) = BestExposed(nudeOut, (int)shape[^1]);
            return new NsfwScore(NsfwOf(vitOut), part, partScore);
        }

        // ------------------------------------------------------------------ pure, for tests

        /// <summary>The NSFW probability from the ViT model's two logits (sfw, nsfw).</summary>
        internal static double NsfwOf(IReadOnlyList<float> logits)
        {
            if (logits == null || logits.Count < 2) return 0;
            var max = Math.Max(logits[0], logits[1]);
            var sfw = Math.Exp(logits[0] - max);
            var nsfw = Math.Exp(logits[1] - max);
            return nsfw / (sfw + nsfw);
        }

        /// <summary>
        /// NudeNet's most certain exposed part: its output is (4 box values + one score per class) × <paramref name="anchors"/>,
        /// row-major; the best score of each exposed class over all anchors, and the best of those. No boxes are needed.
        /// </summary>
        internal static (string Part, double Score) BestExposed(IReadOnlyList<float> output, int anchors)
        {
            string best = null;
            double bestScore = 0;
            if (output == null || anchors <= 0) return (null, 0);
            for (var c = 0; c < NudeNetLabels.Length; c++)
            {
                if (!ExposedParts.Contains(NudeNetLabels[c])) continue;
                var row = (4 + c) * anchors;
                if (row + anchors > output.Count) break;
                for (var a = 0; a < anchors; a++)
                {
                    var score = output[row + a];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = NudeNetLabels[c];
                    }
                }
            }
            return (best, bestScore);
        }

        /// <summary>
        /// The image, opaque: a transparent sticker is put on white, as Telegram shows it in a light theme. Null when it
        /// cannot be read.
        /// </summary>
        internal static SKBitmap Decode(byte[] image)
        {
            if (image == null || image.Length == 0) return null;
            using var data = SKData.CreateCopy(image);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null; // not an image Skia can read
            using var decoded = SKBitmap.Decode(codec);
            if (decoded == null || decoded.Width == 0 || decoded.Height == 0) return null;
            var opaque = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(opaque);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            canvas.Flush();
            return opaque;
        }

        /// <summary>The ViT model's input: resized to 384×384, channels first, (v/255 − 0.5)/0.5.</summary>
        internal static float[] VitTensor(SKBitmap bitmap)
        {
            using var resized = Resize(bitmap, VitSize, VitSize);
            return Planar(resized, v => (v / 255f - 0.5f) / 0.5f);
        }

        /// <summary>NudeNet's input: padded with black to a square at the right and bottom, resized to 320×320, v/255.</summary>
        internal static float[] NudeNetTensor(SKBitmap bitmap)
        {
            var side = Math.Max(bitmap.Width, bitmap.Height);
            using var square = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(square))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
                canvas.Flush();
            }
            using var resized = Resize(square, NudeNetSize, NudeNetSize);
            return Planar(resized, v => v / 255f);
        }

        private static SKBitmap Resize(SKBitmap bitmap, int width, int height) =>
            bitmap.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("Could not resize the image.");

        private static float[] Planar(SKBitmap bitmap, Func<float, float> scale)
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var plane = width * height;
            var tensor = new float[3 * plane];
            var pixels = bitmap.Pixels;
            for (var i = 0; i < plane; i++)
            {
                var p = pixels[i];
                tensor[i] = scale(p.Red);
                tensor[plane + i] = scale(p.Green);
                tensor[2 * plane + i] = scale(p.Blue);
            }
            return tensor;
        }

        public void Dispose()
        {
            _vit.Dispose();
            _nudeNet.Dispose();
        }
    }
}
