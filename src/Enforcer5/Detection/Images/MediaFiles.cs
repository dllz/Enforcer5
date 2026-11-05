using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Enforcer5.Detection.Images
{
    /// <summary>What a post's file is expected to be, before it is downloaded.</summary>
    internal enum MediaHint
    {
        /// <summary>One image (a photo, a static sticker, an image file); an animated one is found from its bytes.</summary>
        Image,

        /// <summary>Moving: a GIF (Telegram sends them as MP4), a video, a round video, a video sticker, a video file.</summary>
        Video,

        /// <summary>A file Telegram could not say anything about (no type, or <c>application/octet-stream</c>): its bytes decide.</summary>
        Unknown
    }

    /// <summary>What a file's bytes say it is.</summary>
    internal enum MediaFormat
    {
        Unknown,

        /// <summary>A still or animated image Skia reads (JPEG, PNG, GIF, WebP, BMP, ICO).</summary>
        Image,

        /// <summary>An image only ffmpeg reads (HEIC, AVIF, TIFF): one frame.</summary>
        OtherImage,

        /// <summary>An animated PNG: Skia only reads its first frame, so ffmpeg does.</summary>
        AnimatedPng,

        /// <summary>A video container (MP4, MOV, WebM, MKV, AVI, FLV, MPEG, MPEG-TS).</summary>
        Video
    }

    /// <summary>
    /// Which files the NSFW check opens and how (Daniel, 2026-11-06): pure, for tests. A file sent as a document is looked at
    /// when its extension or type says image or video, and downloaded to be recognised by its first bytes only when Telegram
    /// says nothing about it (no type, or a generic one, and an extension that is not a known other kind): an image renamed
    /// <c>.bin</c> is found, a zip or a PDF is never downloaded. What the bytes say then wins over the name.
    /// </summary>
    internal static class MediaFiles
    {
        /// <summary>Frames spread evenly over a video or animation, the first and the last included.</summary>
        internal const int EvenFrames = 8;

        /// <summary>Frames where the picture changes (a cut), on top of the even ones: a clean opening, then the real thing.</summary>
        internal const int SceneFrames = 4;

        /// <summary>Frames of an animated image (GIF, WebP): there is no cut detection there, so more even ones.</summary>
        internal const int AnimationFrames = EvenFrames + SceneFrames;

        /// <summary>Frames are scaled to fit this square before scoring; the models read 384 and 320.</summary>
        internal const int FrameSide = 640;

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "jpg", "jpeg", "jpe", "jfif", "png", "apng", "gif", "webp", "bmp", "heic", "heif", "avif", "tif", "tiff", "ico", "jxl"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "mp4", "m4v", "mov", "qt", "webm", "mkv", "avi", "3gp", "3g2", "flv", "wmv", "asf", "mpg", "mpeg", "ts", "mts", "m2ts",
            "ogv", "gifv", "vob"
        };

        /// <summary>Kinds of files that are never images or videos: never downloaded, whatever their type says.</summary>
        private static readonly HashSet<string> OtherExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "apk", "xapk", "ipa", "exe", "msi", "dmg", "deb", "rpm", "iso",
            "jar", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "rtf", "txt", "csv", "json", "xml", "html", "htm",
            "js", "py", "sh", "bat", "cmd", "ps1", "epub", "mobi", "ttf", "otf", "woff", "woff2", "mp3", "ogg", "oga", "opus", "m4a",
            "wav", "flac", "aac", "wma", "tgs", "svg", "torrent", "srt", "vcf", "ics", "log", "md", "sql", "db", "sqlite", "ovpn", "conf"
        };

        /// <summary>Types that say nothing about the file.</summary>
        private static readonly HashSet<string> GenericTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/octet-stream", "application/x-binary", "binary/octet-stream", "application/binary", "application/x-unknown",
            "application/unknown", "application/x-download", "application/force-download"
        };

        /// <summary>
        /// What a document is expected to be, from its type and name; null for a file that is never looked at. An image or
        /// video extension or type wins; then a known other kind (a zip, a PDF, audio) is left alone; what is left with a
        /// generic type or none is <see cref="MediaHint.Unknown"/>.
        /// </summary>
        internal static MediaHint? Classify(string mimeType, string fileName)
        {
            var extension = Extension(fileName);
            var mime = mimeType?.Trim() ?? "";
            if (extension != null && ImageExtensions.Contains(extension)) return MediaHint.Image;
            if (extension != null && VideoExtensions.Contains(extension)) return MediaHint.Video;
            if (mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)) return null; // drawn by code, not pixels
            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return MediaHint.Image;
            if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return MediaHint.Video;
            if (extension != null && OtherExtensions.Contains(extension)) return null;
            if (mime.Length == 0 || GenericTypes.Contains(mime)) return MediaHint.Unknown;
            return null;
        }

        private static string Extension(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var extension = Path.GetExtension(fileName.Trim());
            return string.IsNullOrEmpty(extension) || extension.Length < 2 ? null : extension[1..];
        }

        /// <summary>What <paramref name="bytes"/> are, from their first bytes.</summary>
        internal static MediaFormat Sniff(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return MediaFormat.Unknown;
            bool At(int offset, params byte[] magic) => bytes.Length >= offset + magic.Length && bytes.AsSpan(offset, magic.Length).SequenceEqual(magic);
            bool Text(int offset, string magic) => At(offset, Encoding.ASCII.GetBytes(magic));

            if (At(0, 0xFF, 0xD8, 0xFF)) return MediaFormat.Image;
            if (At(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return IsAnimatedPng(bytes) ? MediaFormat.AnimatedPng : MediaFormat.Image;
            if (Text(0, "GIF87a") || Text(0, "GIF89a")) return MediaFormat.Image;
            if (Text(0, "RIFF") && Text(8, "WEBP")) return MediaFormat.Image;
            if (Text(0, "RIFF") && Text(8, "AVI ")) return MediaFormat.Video;
            if (Text(0, "BM") && bytes.Length > 26) return MediaFormat.Image;
            if (At(0, 0x00, 0x00, 0x01, 0x00)) return MediaFormat.Image; // ICO
            if (At(0, 0x49, 0x49, 0x2A, 0x00) || At(0, 0x4D, 0x4D, 0x00, 0x2A)) return MediaFormat.OtherImage; // TIFF
            if (At(0, 0xFF, 0x0A) || At(0, 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20)) return MediaFormat.OtherImage; // JPEG XL
            if (Text(4, "ftyp"))
            {
                var brand = Encoding.ASCII.GetString(bytes, 8, 4);
                return brand is "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "mif1" or "msf1" or "avif" or "avis"
                    ? MediaFormat.OtherImage
                    : MediaFormat.Video;
            }
            if (At(0, 0x1A, 0x45, 0xDF, 0xA3)) return MediaFormat.Video; // Matroska, WebM
            if (Text(0, "FLV")) return MediaFormat.Video;
            if (At(0, 0x00, 0x00, 0x01, 0xBA) || At(0, 0x00, 0x00, 0x01, 0xB3)) return MediaFormat.Video; // MPEG program/elementary
            if (At(0, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11)) return MediaFormat.Video; // ASF, WMV
            if (Text(0, "OggS") && bytes.AsSpan(0, Math.Min(bytes.Length, 128)).IndexOf("theora"u8) >= 0) return MediaFormat.Video;
            if (bytes.Length >= 189 && bytes[0] == 0x47 && bytes[188] == 0x47) return MediaFormat.Video; // MPEG-TS
            if (Text(4, "moov") || Text(4, "mdat") || Text(4, "wide") || Text(4, "free")) return MediaFormat.Video; // QuickTime without ftyp
            return MediaFormat.Unknown;
        }

        /// <summary>A PNG with an <c>acTL</c> chunk before its image data is animated.</summary>
        private static bool IsAnimatedPng(byte[] bytes)
        {
            var offset = 8;
            while (offset + 8 <= bytes.Length)
            {
                var length = (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
                var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                if (type == "acTL") return true;
                if (type == "IDAT" || length < 0) return false;
                offset += 12 + length;
            }
            return false;
        }

        /// <summary>
        /// <paramref name="count"/> frame indexes spread evenly over <paramref name="frames"/>, the first and the last
        /// included, each once; all of them when there are no more than that.
        /// </summary>
        internal static IReadOnlyList<int> Spread(int frames, int count)
        {
            if (frames <= 0 || count <= 0) return Array.Empty<int>();
            if (frames <= count) return Enumerable.Range(0, frames).ToList();
            if (count == 1) return new[] { 0 };
            return Enumerable.Range(0, count).Select(i => (int)Math.Round(i * (frames - 1) / (double)(count - 1))).Distinct().ToList();
        }

        /// <summary>The length of the media ffmpeg describes (its "Duration: 00:01:02.50" line); null when it gives none.</summary>
        internal static double? Duration(string ffmpegOutput)
        {
            var match = Regex.Match(ffmpegOutput ?? "", @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
            if (!match.Success) return null;
            var seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                          int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                          double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            return seconds > 0 ? seconds : null;
        }

        /// <summary>
        /// True when the ffmpeg of <paramref name="versionLine"/> ("ffmpeg version 5.1.6-0+deb12u1 …") knows
        /// <c>-fps_mode</c> (5.1 and later; a build from git, "N-…" or "n9.0…", is taken as recent). Older ones need <c>-vsync</c>.
        /// </summary>
        internal static bool KnowsFpsMode(string versionLine)
        {
            var match = Regex.Match(versionLine ?? "", @"version\s+n?(\d+)\.(\d+)");
            if (!match.Success) return true;
            var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return major > 5 || (major == 5 && minor >= 1);
        }

        /// <summary>
        /// The seconds between two even frames of a video of <paramref name="duration"/> seconds, so that
        /// <see cref="EvenFrames"/> of them reach the end: the last one is taken a little before it, where a frame still is.
        /// One a second when the length is unknown.
        /// </summary>
        internal static double Step(double? duration)
        {
            if (duration is not > 0) return 1;
            var d = duration.Value;
            var margin = Math.Min(0.2, d / 10);
            return Math.Max(0.001, (d - margin) / (EvenFrames - 1));
        }

        /// <summary>
        /// ffmpeg's arguments for a video's frames: one pass, split in two, the even frames (every <paramref name="step"/>
        /// seconds, at most <see cref="EvenFrames"/> + 1) to <c>e%02d.png</c> and the cuts (a scene change over 0.4, at most
        /// <see cref="SceneFrames"/>) to <c>s%02d.png</c> in <paramref name="folder"/>, each scaled to fit
        /// <see cref="FrameSide"/>. No sound, subtitles or data; two decoding threads.
        /// </summary>
        internal static IReadOnlyList<string> FrameArguments(string input, string folder, double step, bool fpsMode)
        {
            var stepText = step.ToString("0.###", CultureInfo.InvariantCulture);
            var graph = "[0:v:0]split=2[a][b];" +
                        $"[a]select='isnan(prev_selected_t)+gte(t-prev_selected_t,{stepText})',{Scale}[e];" +
                        $"[b]select='gt(scene,0.4)',{Scale}[s]";
            var sync = fpsMode ? new[] { "-fps_mode", "passthrough" } : new[] { "-vsync", "0" };
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-threads", "2", "-i", input, "-an", "-sn", "-dn", "-filter_complex", graph };
            args.AddRange(new[] { "-map", "[e]" });
            args.AddRange(sync);
            args.AddRange(new[] { "-frames:v", (EvenFrames + 1).ToString(CultureInfo.InvariantCulture), Path.Combine(folder, "e%02d.png") });
            args.AddRange(new[] { "-map", "[s]" });
            args.AddRange(sync);
            args.AddRange(new[] { "-frames:v", SceneFrames.ToString(CultureInfo.InvariantCulture), Path.Combine(folder, "s%02d.png") });
            return args;
        }

        /// <summary>Fits a frame in <see cref="FrameSide"/>, never making it larger.</summary>
        private static string Scale => $"scale=w='min({FrameSide},iw)':h='min({FrameSide},ih)':force_original_aspect_ratio=decrease";

        /// <summary>ffmpeg's arguments for an image only it reads (HEIC, AVIF, TIFF): its first frame as <c>e01.png</c>.</summary>
        internal static IReadOnlyList<string> StillArguments(string input, string folder) => new[]
        {
            "-hide_banner", "-nostdin", "-v", "error", "-threads", "2", "-i", input, "-an", "-sn", "-dn",
            "-vf", Scale, "-frames:v", "1", Path.Combine(folder, "e01.png")
        };
    }
}
