using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using SkiaSharp;

namespace Enforcer5.Detection.Images
{
    /// <summary>Turns a downloaded file into the images the models score. Replaced in the harness.</summary>
    internal interface IMediaDecoder
    {
        /// <summary>Videos can be read (ffmpeg is there). Without it a video is judged by its preview alone, and not downloaded.</summary>
        bool ReadsVideo { get; }

        /// <summary>
        /// The images to score in <paramref name="file"/>, each encoded: the file itself for a still image, frames for an
        /// animation or a video. Null when nothing can be read from it (not an image or video, or no way to read it).
        /// </summary>
        Task<IReadOnlyList<byte[]>> FramesAsync(byte[] file, MediaHint hint, CancellationToken cancel);
    }

    /// <summary>
    /// The decoder in production (Daniel, 2026-11-06): what the bytes are decides (<see cref="MediaFiles.Sniff"/>), the
    /// hint only when they say nothing. A still image goes to the models as it is; an animated GIF or WebP gives
    /// <see cref="MediaFiles.AnimationFrames"/> frames spread over it (Skia, no ffmpeg needed); a video, an animated PNG and
    /// an image Skia cannot read (HEIC, AVIF, TIFF) go through ffmpeg (<see cref="Ffmpeg"/>), and are not read without it.
    /// </summary>
    internal sealed class MediaDecoder : IMediaDecoder
    {
        public bool ReadsVideo => Ffmpeg.Available;

        public async Task<IReadOnlyList<byte[]>> FramesAsync(byte[] file, MediaHint hint, CancellationToken cancel)
        {
            if (file == null || file.Length == 0) return null;
            switch (MediaFiles.Sniff(file))
            {
                case MediaFormat.Video:
                    return Ffmpeg.Available ? await Ffmpeg.VideoFramesAsync(file, cancel) : null;
                case MediaFormat.AnimatedPng:
                    return Ffmpeg.Available ? await Ffmpeg.VideoFramesAsync(file, cancel) ?? new[] { file } : new[] { file };
                case MediaFormat.OtherImage:
                    return Ffmpeg.Available ? await Ffmpeg.StillAsync(file, cancel) : null;
                case MediaFormat.Image:
                    // Unreadable after all: the models say so (a score of nothing), as for any broken image.
                    return AnimatedImages.Frames(file, MediaFiles.AnimationFrames) ?? new[] { file };
                default:
                    return hint switch
                    {
                        MediaHint.Image => new[] { file }, // Telegram said image: the models try, as they always did
                        MediaHint.Video when Ffmpeg.Available => await Ffmpeg.VideoFramesAsync(file, cancel),
                        _ => null
                    };
            }
        }
    }

    /// <summary>The frames of an animated GIF or WebP, read with Skia.</summary>
    internal static class AnimatedImages
    {
        /// <summary>Pixels decoded at most for one animation (a 500×500 GIF of 1600 frames): past that, the frames so far and the current one.</summary>
        internal const long PixelBudget = 400_000_000;

        /// <summary>
        /// <paramref name="count"/> frames spread over the animation in <paramref name="file"/> (<see cref="MediaFiles.Spread"/>),
        /// each as a PNG at most <see cref="MediaFiles.FrameSide"/> pixels a side; the file itself when it has one frame; null
        /// when Skia cannot read it. Every frame up to the last one wanted is decoded in turn, as a viewer shows them: a
        /// frame of a GIF often draws only what changed over the ones before.
        /// </summary>
        internal static IReadOnlyList<byte[]> Frames(byte[] file, int count)
        {
            using var data = SKData.CreateCopy(file);
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;
            var frames = codec.FrameCount;
            if (frames <= 1) return new[] { file };
            var width = codec.Info.Width;
            var height = codec.Info.Height;
            if (width <= 0 || height <= 0) return null;
            var infos = codec.FrameInfo;
            var wanted = new HashSet<int>(MediaFiles.Spread(frames, count));
            var last = wanted.Max();
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var buffer = new SKBitmap(info);
            SKBitmap saved = null;
            var savedFrame = -1;
            var bufferFrame = -1;
            var result = new List<byte[]>();
            long decoded = 0;
            try
            {
                for (var i = 0; i <= last && i < infos.Length; i++)
                {
                    var required = infos[i].RequiredFrame;
                    // The frame it builds on is in the buffer, or Skia decodes that one first itself (no prior frame).
                    var prior = required != -1 && bufferFrame >= required ? bufferFrame : -1;
                    if (required == -1) buffer.Erase(SKColors.Transparent);
                    var restore = infos[i].DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious;
                    if (restore)
                    {
                        saved?.Dispose();
                        saved = buffer.Copy();
                        savedFrame = bufferFrame;
                    }
                    var outcome = codec.GetPixels(info, buffer.GetPixels(), new SKCodecOptions(i, prior));
                    var read = outcome is SKCodecResult.Success or SKCodecResult.IncompleteInput;
                    decoded += (long)width * height;
                    var over = decoded > PixelBudget;
                    if (read && (wanted.Contains(i) || over)) result.Add(Encode(buffer));
                    if (restore && saved != null)
                    {
                        using var canvas = new SKCanvas(buffer);
                        canvas.Clear(SKColors.Transparent);
                        canvas.DrawBitmap(saved, 0, 0);
                        bufferFrame = savedFrame;
                    }
                    else bufferFrame = read ? i : -1;
                    if (over) break;
                }
            }
            finally
            {
                saved?.Dispose();
            }
            return result.Count > 0 ? result : null;
        }

        /// <summary>A frame as a PNG (it keeps transparency, which the models put on white), scaled to fit <see cref="MediaFiles.FrameSide"/>.</summary>
        internal static byte[] Encode(SKBitmap frame)
        {
            var side = Math.Max(frame.Width, frame.Height);
            if (side <= MediaFiles.FrameSide)
            {
                using var whole = frame.Encode(SKEncodedImageFormat.Png, 100);
                return whole.ToArray();
            }
            var scale = MediaFiles.FrameSide / (double)side;
            var info = new SKImageInfo(Math.Max(1, (int)(frame.Width * scale)), Math.Max(1, (int)(frame.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
            using var smaller = frame.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var encoded = smaller.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
    }

    /// <summary>
    /// ffmpeg, for videos and the images Skia cannot read (Daniel, 2026-11-06). <c>FfmpegPath</c> names the binary; unset,
    /// <c>ffmpeg</c> is looked for on the PATH (<c>deploy/setup.sh</c> installs it). Not found: <see cref="Available"/> is
    /// false and videos are judged by their preview alone. Each file is written to a folder of its own under the temp
    /// directory and removed after; ffmpeg is killed when the image queue gives the file up.
    /// </summary>
    internal static class Ffmpeg
    {
        /// <summary>How long one ffmpeg run may take; the image queue's own limit (<see cref="ImageScans.ImageLimit"/>) is longer.</summary>
        internal static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(40);

        private static readonly object Lock = new();
        private static bool _resolved;
        private static string _path;
        private static bool _fpsMode;
        private static string _version;

        /// <summary>ffmpeg was found and answers.</summary>
        internal static bool Available
        {
            get
            {
                Resolve();
                return _path != null;
            }
        }

        /// <summary>The binary found, for tests; null for none.</summary>
        internal static string BinaryForTests
        {
            get
            {
                Resolve();
                return _path;
            }
        }

        /// <summary>For the startup log and /nsfwtest: the binary and its version, or why there is none.</summary>
        internal static string Describe()
        {
            Resolve();
            return _path != null ? $"{_path} ({_version})" : "not found: videos are judged by their preview";
        }

        private static void Resolve()
        {
            if (Volatile.Read(ref _resolved)) return;
            lock (Lock)
            {
                if (_resolved) return;
                try
                {
                    var configured = RegHelper.GetRegValue("FfmpegPath");
                    var path = !string.IsNullOrWhiteSpace(configured) ? (File.Exists(configured) ? configured : null) : OnPath("ffmpeg");
                    if (path != null)
                    {
                        // Once, on the image queue's first video: blocking it this once is fine.
                        var version = RunAsync(path, new[] { "-hide_banner", "-version" }, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                        if (version.ExitCode == 0)
                        {
                            _version = version.Output.Split('\n').FirstOrDefault()?.Trim();
                            _fpsMode = MediaFiles.KnowsFpsMode(_version);
                            _path = path;
                        }
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Looking for ffmpeg");
                }
                Volatile.Write(ref _resolved, true);
            }
        }

        private static string OnPath(string name)
        {
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                         .Concat(new[] { "/usr/bin", "/usr/local/bin" }))
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Forgets what was found, for tests.</summary>
        internal static void ResetForTests()
        {
            lock (Lock)
            {
                _resolved = false;
                _path = null;
                _version = null;
            }
        }

        /// <summary>A video's frames (<see cref="MediaFiles.FrameArguments"/>): the even ones, then the cuts. Null when ffmpeg read none.</summary>
        internal static Task<IReadOnlyList<byte[]>> VideoFramesAsync(byte[] file, CancellationToken cancel) =>
            InFolderAsync(file, cancel, async (input, folder) =>
            {
                var probe = await RunAsync(new[] { "-hide_banner", "-nostdin", "-i", input }, cancel); // exits 1: no output named
                var step = MediaFiles.Step(MediaFiles.Duration(probe.Output));
                return await RunAsync(MediaFiles.FrameArguments(input, folder, step, _fpsMode), cancel);
            });

        /// <summary>The first frame of an image only ffmpeg reads. Null when it read none.</summary>
        internal static Task<IReadOnlyList<byte[]>> StillAsync(byte[] file, CancellationToken cancel) =>
            InFolderAsync(file, cancel, (input, folder) => RunAsync(MediaFiles.StillArguments(input, folder), cancel));

        private static async Task<IReadOnlyList<byte[]>> InFolderAsync(byte[] file, CancellationToken cancel,
            Func<string, string, Task<(int ExitCode, string Output)>> run)
        {
            var folder = Path.Combine(Path.GetTempPath(), "enforcer-frames", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var input = Path.Combine(folder, "input");
                await File.WriteAllBytesAsync(input, file, cancel);
                var (exitCode, output) = await run(input, folder);
                var frames = Directory.GetFiles(folder, "e*.png").OrderBy(f => f, StringComparer.Ordinal)
                    .Concat(Directory.GetFiles(folder, "s*.png").OrderBy(f => f, StringComparer.Ordinal))
                    .Select(File.ReadAllBytes).Where(b => b.Length > 0).ToList();
                if (frames.Count == 0)
                {
                    // A broken or unreadable file: an answer about the file, not a failure of ours.
                    LogHelper.Info($"ffmpeg read no frames (exit {exitCode}): {Shorten(output)}");
                    return null;
                }
                return frames;
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Left in the temp folder, which the system empties; nothing else depends on it.
                }
            }
        }

        private static string Shorten(string text) =>
            string.IsNullOrWhiteSpace(text) ? "no output" : text.Trim().Length > 300 ? text.Trim()[..300] + "…" : text.Trim();

        private static Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken cancel) =>
            RunAsync(_path, args, RunLimit, cancel);

        /// <summary>Runs ffmpeg; its stderr (where it writes everything) and stdout, at most 64 KB. Killed when over <paramref name="limit"/> or cancelled.</summary>
        private static async Task<(int ExitCode, string Output)> RunAsync(string path, IReadOnlyList<string> args, TimeSpan limit, CancellationToken cancel = default)
        {
            var start = new ProcessStartInfo(path)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start");
            var output = new StringBuilder();
            void Collect(object _, DataReceivedEventArgs line)
            {
                if (line.Data == null) return;
                lock (output)
                    if (output.Length < 64 * 1024) output.AppendLine(line.Data);
            }
            process.ErrorDataReceived += Collect;
            process.OutputDataReceived += Collect;
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(limit);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                    // It ended just now.
                }
                cancel.ThrowIfCancellationRequested();
                lock (output) return (-1, "ran over " + limit.TotalSeconds + " s\n" + output);
            }
            await process.WaitForExitAsync(CancellationToken.None); // the output is read to its end
            lock (output) return (process.ExitCode, output.ToString());
        }
    }
}
