using System;
using System.Collections.Generic;
using System.Globalization;
using Enforcer5.Helpers;

namespace Enforcer5.Detection.Images
{
    /// <summary>
    /// What the two NSFW models made of one image (Daniel, 2026-11-05): the ViT model's score for the whole image
    /// (<see cref="Nsfw"/>, 0 to 1), and NudeNet's most certain exposed body part (<see cref="Part"/>, one of
    /// <see cref="NsfwModels.ExposedParts"/>, with <see cref="PartScore"/>). The image is flagged when either is at or
    /// over its threshold (<see cref="NsfwThresholds"/>). NudeNet's part is the reason a report gives.
    /// </summary>
    internal sealed record NsfwScore(double Nsfw, string Part, double PartScore)
    {
        /// <summary>An image nothing could be made of (it could not be read).</summary>
        internal static NsfwScore None { get; } = new NsfwScore(0, null, 0);

        internal bool Flagged(NsfwThresholds thresholds) =>
            Nsfw >= thresholds.Nsfw || Part != null && PartScore >= thresholds.Part;

        /// <summary>The part, when it is what flagged it; else null (the whole image was).</summary>
        internal string Reason(NsfwThresholds thresholds) => Part != null && PartScore >= thresholds.Part ? Part : null;

        /// <summary>How sure, 0 to 1: the higher of the two scores.</summary>
        internal double Certainty => Math.Max(Nsfw, PartScore);

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"nsfw {Nsfw:0.000}, {Part ?? "-"} {PartScore:0.000}");
    }

    /// <param name="Nsfw">The ViT model's score at which an image is flagged.</param>
    /// <param name="Part">NudeNet's score for an exposed part at which an image is flagged.</param>
    internal sealed record NsfwThresholds(double Nsfw, double Part)
    {
        /// <summary>
        /// Both start high (Daniel, 2026-11-05): the ViT model flags cleavage and much skin, and NudeNet scored up to 0.59
        /// on normal photos (212 COCO and site images: none at 0.8 for either, /workspace/scratch/nsfw/ref.py). Tuned from
        /// the evals (<c>profile_scans</c>, <c>media_scores</c>); <c>NsfwThreshold</c> and <c>NsfwPartThreshold</c> override.
        /// </summary>
        internal const double DefaultNsfw = 0.9;
        internal const double DefaultPart = 0.7;

        internal static NsfwThresholds Current =>
            new NsfwThresholds(Read("NsfwThreshold", DefaultNsfw), Read("NsfwPartThreshold", DefaultPart));

        private static double Read(string key, double fallback)
        {
            var value = RegHelper.GetRegValue(key);
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 && parsed <= 1
                ? parsed
                : fallback;
        }
    }

    /// <summary>Scores an image. The ONNX models in production (<see cref="NsfwModels"/>), a stand-in in the harness.</summary>
    internal interface INsfwModel
    {
        /// <summary>A version string kept with every score, so a model change is visible in the data.</summary>
        string Version { get; }

        /// <summary>Scores an encoded image (JPEG, PNG, WebP, GIF's first frame). <see cref="NsfwScore.None"/> when it cannot be read.</summary>
        NsfwScore Score(byte[] image);
    }

    /// <summary>How a part is named in reports: <c>nsfwPart_{part}</c>.</summary>
    internal static class NsfwParts
    {
        internal static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
        {
            ["MALE_GENITALIA_EXPOSED"] = "nsfwPart_genitals",
            ["FEMALE_GENITALIA_EXPOSED"] = "nsfwPart_genitals",
            ["FEMALE_BREAST_EXPOSED"] = "nsfwPart_breasts",
            ["ANUS_EXPOSED"] = "nsfwPart_anus",
            ["BUTTOCKS_EXPOSED"] = "nsfwPart_buttocks"
        };
    }
}
