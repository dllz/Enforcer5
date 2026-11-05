using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enforcer5.Helpers;

namespace Enforcer5.Detection.Images
{
    /// <summary>
    /// What the two NSFW models made of one image (Daniel, 2026-11-05). The ViT model decides (<see cref="Nsfw"/>, 0 to 1,
    /// for the whole image; flagged at <see cref="NsfwThresholds.Nsfw"/>); NudeNet says why: the best score of each exposed
    /// body part it found (<see cref="Parts"/>, keyed by <see cref="NsfwModels.ExposedParts"/>), and the report names those at
    /// or over <see cref="NsfwThresholds.Part"/>. In the eval (deepghs/nsfw_detect, 1000 images a class) NudeNet flagging on its
    /// own added more false alarms on normal photos than it caught, so it never flags: ViT at 0.9 caught 99.5% of porn and
    /// 79% of hentai, with 0.6% of normal photos and 0.9% of drawings flagged (and 98.5% of swimwear and lingerie, which it
    /// cannot tell apart from porn: a report without exposed parts is most likely that).
    /// </summary>
    internal sealed record NsfwScore(double Nsfw, IReadOnlyDictionary<string, double> Parts)
    {
        private static readonly IReadOnlyDictionary<string, double> NoParts = new Dictionary<string, double>();

        /// <summary>An image nothing could be made of (it could not be read).</summary>
        internal static NsfwScore None { get; } = new NsfwScore(0, NoParts);

        internal IReadOnlyDictionary<string, double> Parts { get; init; } = Parts ?? NoParts;

        /// <summary>Frames scored for an animation or video (the score is the worst one's); 0 for a still image or one read from the store.</summary>
        internal int Frames { get; init; }

        /// <summary>The worst frame, from 0 (the even frames in order, then the cuts); -1 for none.</summary>
        internal int Frame { get; init; } = -1;

        /// <summary>The part NudeNet is surest of, or null for none.</summary>
        internal string Part => Parts.Count == 0 ? null : Parts.OrderByDescending(p => p.Value).First().Key;

        /// <summary>Its score; 0 for none.</summary>
        internal double PartScore => Parts.Count == 0 ? 0 : Parts.Values.Max();

        internal bool Flagged(NsfwThresholds thresholds) => Nsfw >= thresholds.Nsfw;

        /// <summary>The exposed parts a report names: at or over the threshold, the surest first.</summary>
        internal IReadOnlyList<(string Part, double Score)> Reasons(NsfwThresholds thresholds) =>
            Parts.Where(p => p.Value >= thresholds.Part).OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList();

        /// <summary>The highest score of each kind over several images (a profile's).</summary>
        internal static NsfwScore Max(IEnumerable<NsfwScore> scores)
        {
            var list = scores.ToList();
            if (list.Count == 0) return None;
            var parts = new Dictionary<string, double>();
            foreach (var (part, value) in list.SelectMany(s => s.Parts))
                parts[part] = parts.TryGetValue(part, out var had) ? Math.Max(had, value) : value;
            return new NsfwScore(list.Max(s => s.Nsfw), parts);
        }

        /// <summary>
        /// A video's score from its frames' (<paramref name="frames"/>, in order): the worst frame's, whole, so the reasons are
        /// what that frame shows. Null for none.
        /// </summary>
        internal static NsfwScore WorstFrame(IReadOnlyList<NsfwScore> frames)
        {
            if (frames == null || frames.Count == 0) return null;
            var worst = 0;
            for (var i = 1; i < frames.Count; i++)
                if (frames[i].Nsfw > frames[worst].Nsfw) worst = i;
            return frames[worst] with { Frames = frames.Count, Frame = worst };
        }

        public override string ToString() =>
            (Frames > 1 ? $"frame {Frame + 1} of {Frames}, " : "") +
            string.Create(CultureInfo.InvariantCulture, $"nsfw {Nsfw:0.000}, ") +
            (Parts.Count == 0 ? "-" : string.Join(", ", Parts.OrderByDescending(p => p.Value)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key} {p.Value:0.000}"))));
    }

    /// <param name="Nsfw">The ViT model's score at which an image is flagged.</param>
    /// <param name="Part">NudeNet's score at which an exposed part is named as a reason (it never flags on its own).</param>
    internal sealed record NsfwThresholds(double Nsfw, double Part)
    {
        /// <summary>
        /// From the eval (Daniel, 2026-11-05; /workspace/scratch/nsfw/eval on the quasi box): ViT 0.9 (0.95 halves the false
        /// alarms on drawings, 0.9% to 0.4%, and catches 9 points less hentai). Parts at 0.5: 70% of the porn the ViT model
        /// flagged had one named, and none of the normal photos it flagged in error did at 0.6. <c>NsfwThreshold</c> and
        /// <c>NsfwPartThreshold</c> override.
        /// </summary>
        internal const double DefaultNsfw = 0.9;
        internal const double DefaultPart = 0.5;

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

        /// <summary>Scores an encoded image (JPEG, PNG, WebP, GIF's first frame; frames of animations and videos come as PNG). <see cref="NsfwScore.None"/> when it cannot be read.</summary>
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
