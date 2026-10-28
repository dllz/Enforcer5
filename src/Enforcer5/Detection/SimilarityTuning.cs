using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enforcer5.Helpers;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Every similarity threshold Detection and raid detection compare with, in one place (design section 11, build step
    /// 13), like <see cref="Raids.RaidLimits"/>. Each can be set for the whole bot from the configuration (an environment
    /// variable or appsettings.json key of the same name), so a threshold the tuning script proposes can be tried without
    /// a build. Changed thresholds go out in shadow first: the near misses (<see cref="NearMisses"/>) show what a lower one
    /// would have caught.
    /// <para>
    /// Two kinds so far, both without a model: texts (three-letter runs of the normalised text, Jaccard) and names
    /// (edit distance of the normalised names). Embeddings and image hashes come with the AI step.
    /// </para>
    /// </summary>
    internal static class SimilarityTuning
    {
        /// <summary>Two texts sharing this much of their three-letter runs are the same text (raids, newcomers posting alike).</summary>
        internal static double Text = Read("SimilarityText", 0.7);

        /// <summary>Texts this alike but under <see cref="Text"/> are recorded as near misses.</summary>
        internal static double TextNear = Read("SimilarityTextNear", 0.5);

        /// <summary>Names with fewer letters are only compared for being the same.</summary>
        internal static int NameCloseFrom = (int)Read("SimilarityNameCloseFrom", 5);

        /// <summary>Up to this many letters a name may be <see cref="NameShortDistance"/> letters off; longer ones <see cref="NameLongDistance"/>.</summary>
        internal static int NameShort = (int)Read("SimilarityNameShort", 12);

        internal static int NameShortDistance = (int)Read("SimilarityNameShortDistance", 1);
        internal static int NameLongDistance = (int)Read("SimilarityNameLongDistance", 2);

        /// <summary>Names this alike (1 − edit distance / the longer name's letters) that did not match are recorded as near misses.</summary>
        internal static double NameNear = Read("SimilarityNameNear", 0.6);

        private static double Read(string key, double fallback)
        {
            try
            {
                var value = RegHelper.GetRegValue(key);
                return value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            }
            catch (Exception)
            {
                // No configuration at all (a test host): the default.
                return fallback;
            }
        }

        /// <summary>The thresholds as they are now, for /simtest and /detectreview.</summary>
        internal static IReadOnlyList<(string Name, string Value)> Current() => new[]
        {
            ("SimilarityText", Text.ToString("0.##", CultureInfo.InvariantCulture)),
            ("SimilarityTextNear", TextNear.ToString("0.##", CultureInfo.InvariantCulture)),
            ("SimilarityNameCloseFrom", NameCloseFrom.ToString(CultureInfo.InvariantCulture)),
            ("SimilarityNameShort", NameShort.ToString(CultureInfo.InvariantCulture)),
            ("SimilarityNameShortDistance", NameShortDistance.ToString(CultureInfo.InvariantCulture)),
            ("SimilarityNameLongDistance", NameLongDistance.ToString(CultureInfo.InvariantCulture)),
            ("SimilarityNameNear", NameNear.ToString("0.##", CultureInfo.InvariantCulture))
        };

        // ------------------------------------------------------------------ texts

        /// <summary>How much of their three-letter runs two sketches share (Jaccard, 0 to 1); 0 when either is empty.</summary>
        internal static double Jaccard(HashSet<int> a, HashSet<int> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return 0;
            var small = a.Count <= b.Count ? a : b;
            var large = ReferenceEquals(small, a) ? b : a;
            var shared = small.Count(large.Contains);
            return (double)shared / (a.Count + b.Count - shared);
        }

        /// <summary>How alike two texts are, after <see cref="MessageFingerprint.Normalize"/>: 1 for the same letters.</summary>
        internal static double TextSimilarity(string a, string b)
        {
            var na = MessageFingerprint.Normalize(a);
            var nb = MessageFingerprint.Normalize(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            if (na == nb) return 1;
            return Jaccard(MessageFingerprint.Sketch(a), MessageFingerprint.Sketch(b));
        }

        // ------------------------------------------------------------------ names

        /// <summary>1 − edit distance (a swap of neighbours counting one) / the longer name's length; both already normalised.</summary>
        internal static double NameSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            var longer = Math.Max(a.Length, b.Length);
            return 1 - (double)NameSigns.Distance(a, b, longer) / longer;
        }

        /// <summary>How many letters apart a name of <paramref name="length"/> letters may be and still be the same name.</summary>
        internal static int NameDistanceFor(int length) => length > NameShort ? NameLongDistance : NameShortDistance;

        /// <summary>Sets every threshold back to its default; for tests.</summary>
        internal static void ResetForTests()
        {
            Text = 0.7;
            TextNear = 0.5;
            NameCloseFrom = 5;
            NameShort = 12;
            NameShortDistance = 1;
            NameLongDistance = 2;
            NameNear = 0.6;
        }
    }
}
