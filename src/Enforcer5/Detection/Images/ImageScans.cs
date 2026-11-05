using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection.Images
{
    /// <summary>
    /// One queue for every image the NSFW check looks at (Daniel, 2026-11-05): photos, stickers and thumbnails posted in a
    /// group, members' profile photos and the posts of their personal channel; since 2026-11-06 also GIFs, videos and files,
    /// read as frames (<see cref="Decoder"/>, the worst frame counts). Built like <see cref="Profiles"/>:
    /// <list type="bullet">
    /// <item>an image is known by its <c>file_unique_id</c>: the same file asked for by many is scored once; scores are kept
    /// in memory (<see cref="Remembered"/>) and in PostgreSQL (<c>media_scores</c>, with the model version), so a sticker
    /// pack seen every day costs nothing after the first time, for both editions;</item>
    /// <item>one image at a time, on dispatched work, never on the message path: the caller gets a task;</item>
    /// <item>at most <see cref="MaxWaiting"/> waiting (a raid of pictures is held by raid mode, not here); over that, and for
    /// a file over <see cref="MaxBytes"/>, the answer is null: not scored.</item>
    /// </list>
    /// Off (every answer null) until the models are loaded: <c>NsfwModelPath</c> unset, or its files missing.
    /// The image itself is never kept; only its scores.
    /// </summary>
    internal static class ImageScans
    {
        internal const int MaxWaiting = 500;
        /// <summary>The largest file looked at: the most the Bot API lets a bot download. Over it, a video is judged by its preview.</summary>
        internal const long MaxBytes = 20 * 1024 * 1024;
        internal const int Remembered = 50_000;

        private static readonly object Lock = new();
        private static readonly Dictionary<string, NsfwScore> Known = new(StringComparer.Ordinal);
        private static readonly Queue<string> KnownOrder = new();
        private static readonly Dictionary<string, TaskCompletionSource<NsfwScore>> Waiting = new(StringComparer.Ordinal);
        private sealed record Item(string UniqueId, string FileId, string Kind, MediaHint Hint);

        // Two lanes: what groups are waiting on (posts, joins) always goes before the bulk of a dev's /nsfwscan.
        private static readonly Queue<Item> Urgent = new();
        private static readonly Queue<Item> Bulk = new();
        private static readonly HashSet<string> InUrgent = new(StringComparer.Ordinal);
        private static bool _draining;
        private static int _scored;
        private static int _timedOut;

        /// <summary>How long one image may take, download and scoring together, before it is given up (2026-11-05: one
        /// download that never ended stopped the queue, and with it the check in every group, until a restart).</summary>
        internal static TimeSpan ImageLimit = TimeSpan.FromSeconds(60);

        /// <summary>Images given up after <see cref="ImageLimit"/>.</summary>
        internal static int TimedOut => _timedOut;

        /// <summary>The queue's state for the watchdog and /queues.</summary>
        internal static readonly QueueStatus Status = QueueStatus.Register("images");

        /// <summary>The models; null while they are off. Replaced in tests.</summary>
        internal static INsfwModel Model { get; set; }

        internal static bool Enabled => Model != null;

        /// <summary>Reads files into the images to score (<see cref="MediaDecoder"/>). Replaced in tests.</summary>
        internal static IMediaDecoder Decoder { get; set; } = new MediaDecoder();

        /// <summary>Images the models scored (not from a cache), for tests.</summary>
        internal static int Scored => _scored;

        /// <summary>Loads the models in the background; the checks stay off until they are in (or for good, when they are not there).</summary>
        internal static void Start(CancellationToken shutdown)
        {
            var folder = RegHelper.GetRegValue("NsfwModelPath");
            if (string.IsNullOrWhiteSpace(folder)) return; // off: nothing to say
            new Thread(() =>
            {
                try
                {
                    var threads = int.TryParse(RegHelper.GetRegValue("NsfwThreads"), out var t) && t > 0 ? t : 2;
                    var models = NsfwModels.Load(folder, threads, out var why);
                    if (models == null)
                    {
                        ErrorReporter.Report(new InvalidOperationException($"NSFW models not loaded: {why}"), "Loading the NSFW models");
                        return;
                    }
                    if (shutdown.IsCancellationRequested) return;
                    LogHelper.Info($"ffmpeg for videos: {Ffmpeg.Describe()}"); // looked for once, here, not on the first video
                    Model = models;
                    LogHelper.Info($"NSFW models loaded from {folder} ({models.Version}, {threads} threads).");
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, "Loading the NSFW models");
                }
            }) { IsBackground = true, Name = "NsfwModels" }.Start();
        }

        /// <summary>
        /// The scores of the image <paramref name="uniqueId"/>, downloaded by <paramref name="fileId"/> if it was never
        /// scored; null when it could not be (off, queue full, too large, Telegram refused, over <see cref="ImageLimit"/>).
        /// </summary>
        /// <param name="kind">photo, sticker, thumbnail, animation, video, video_note, file, profile_photo or channel_post: kept with the score.</param>
        /// <param name="bulk">For a dev's scan: waits behind everything a group is waiting on.</param>
        /// <param name="hint">What the file is expected to be (<see cref="IMediaDecoder.FramesAsync"/>).</param>
        internal static Task<NsfwScore> ScoreAsync(string uniqueId, string fileId, string kind, bool bulk = false, MediaHint hint = MediaHint.Image)
        {
            if (Model == null || string.IsNullOrEmpty(uniqueId) || string.IsNullOrEmpty(fileId)) return Task.FromResult<NsfwScore>(null);
            bool start;
            TaskCompletionSource<NsfwScore> waiting;
            lock (Lock)
            {
                if (Known.TryGetValue(uniqueId, out var known)) return Task.FromResult(known);
                if (Waiting.TryGetValue(uniqueId, out waiting))
                {
                    // Waiting behind a scan, and now a group needs it: it goes ahead too (the later copy is skipped).
                    if (!bulk && InUrgent.Add(uniqueId)) Urgent.Enqueue(new Item(uniqueId, fileId, kind, hint));
                    return waiting.Task;
                }
                if ((bulk ? Bulk : Urgent).Count >= MaxWaiting) return Task.FromResult<NsfwScore>(null);
                waiting = new TaskCompletionSource<NsfwScore>(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiting[uniqueId] = waiting;
                if (bulk) Bulk.Enqueue(new Item(uniqueId, fileId, kind, hint));
                else
                {
                    InUrgent.Add(uniqueId);
                    Urgent.Enqueue(new Item(uniqueId, fileId, kind, hint));
                }
                Status.Waiting(Waiting.Count);
                start = !_draining;
                _draining = true;
            }
            if (start) Bot.Dispatch(DrainAsync, "ImageScans");
            return waiting.Task;
        }

        /// <summary>Waits for a score from dispatched work, parked, so the harness counts it as waiting (as <see cref="Profiles.AwaitAsync{T}"/>).</summary>
        internal static Task<T> AwaitAsync<T>(Task<T> scoring) => Profiles.AwaitAsync(scoring);

        private static async Task DrainAsync()
        {
            while (true)
            {
                Item next;
                lock (Lock)
                {
                    next = null;
                    while (next == null && (Urgent.Count > 0 || Bulk.Count > 0))
                    {
                        var item = Urgent.Count > 0 ? Urgent.Dequeue() : Bulk.Dequeue();
                        InUrgent.Remove(item.UniqueId);
                        // Done already (it was in both lanes).
                        if (Waiting.ContainsKey(item.UniqueId)) next = item;
                    }
                    if (next == null)
                    {
                        _draining = false;
                        Status.Waiting(0);
                        return;
                    }
                }
                Status.Started($"{next.Kind} {next.UniqueId}");
                var score = await ScoreWithinLimitAsync(next);
                TaskCompletionSource<NsfwScore> waiting;
                lock (Lock)
                {
                    if (score != null) Remember(next.UniqueId, score);
                    Waiting.Remove(next.UniqueId, out waiting);
                    Status.Finished(Waiting.Count);
                }
                waiting?.TrySetResult(score);
            }
        }

        /// <summary>
        /// One image, given up after <see cref="ImageLimit"/> on the bot's clock: the download is cancelled (it honours the
        /// token while reading the file too), and the queue moves on. Null when it failed or ran over.
        /// </summary>
        private static async Task<NsfwScore> ScoreWithinLimitAsync(Item item)
        {
            var cancel = new CancellationTokenSource();
            // Dispatched work of its own, so the harness sees it running while the queue waits on the clock for it.
            var result = new TaskCompletionSource<NsfwScore>(TaskCreationOptions.RunContinuationsAsynchronously);
            Bot.Dispatch(async () =>
            {
                try
                {
                    result.TrySetResult(await ScoreOneAsync(item, cancel.Token));
                }
                catch (Exception e)
                {
                    result.TrySetException(e);
                }
                finally
                {
                    cancel.Dispose();
                }
            }, "ImageScans.Image");
            var work = result.Task;
            if (!await Clock.WaitForAsync(work, ImageLimit))
            {
                try { cancel.Cancel(); } catch (ObjectDisposedException) { } // it ended just now
                Interlocked.Increment(ref _timedOut);
                var waiting = WaitingCount();
                LogHelper.Info($"Image {item.Kind} {item.UniqueId}: given up after {ImageLimit.TotalSeconds:0} s, {waiting} waiting");
                ErrorReporter.Report($"NSFW image queue: {item.Kind} {item.UniqueId} gave no answer in {ImageLimit.TotalSeconds:0} s; " +
                                     $"skipped, {waiting} waiting");
                // Whatever it still does, it ends quietly: its result is no longer wanted.
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }
            try
            {
                return await work;
            }
            catch (Exception e) when (e is OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Scoring the image {item.UniqueId} ({item.Kind})");
                return null;
            }
        }

        private static async Task<NsfwScore> ScoreOneAsync(Item item, CancellationToken cancel)
        {
            var (uniqueId, fileId, kind, hint) = item;
            var model = Model;
            if (model == null) return null;
            if (PostgresStorage.Ready)
            {
                try
                {
                    if (await Repositories.MediaScores.GetAsync(uniqueId, model.Version) is { } stored) return stored;
                }
                catch (Exception e)
                {
                    // Scored again instead; only costs the time.
                    ErrorReporter.Report(e, $"Reading the score of the image {uniqueId}");
                }
            }
            var started = Clock.UtcNow;
            var image = await DownloadAsync(fileId, cancel);
            cancel.ThrowIfCancellationRequested();
            var downloaded = Clock.UtcNow;
            if (image == null)
            {
                LogHelper.Info($"Image {kind} {uniqueId}: not downloaded ({(downloaded - started).TotalMilliseconds:0} ms), {WaitingCount()} waiting");
                return null;
            }
            var frames = await Decoder.FramesAsync(image, hint, cancel);
            cancel.ThrowIfCancellationRequested();
            if (frames == null || frames.Count == 0)
            {
                LogHelper.Info($"Image {kind} {uniqueId}: {image.Length / 1024} KB, nothing to read in it ({MediaFiles.Sniff(image)}), {WaitingCount()} waiting");
                return null;
            }
            var decoded = Clock.UtcNow;
            var score = frames.Count == 1 ? model.Score(frames[0]) : NsfwScore.WorstFrame(frames.Select(model.Score).ToList());
            Interlocked.Increment(ref _scored);
            LogHelper.Info($"Image {kind} {uniqueId}: {image.Length / 1024} KB in {(downloaded - started).TotalMilliseconds:0} ms, " +
                           (frames.Count > 1 ? $"{frames.Count} frames in {(decoded - downloaded).TotalMilliseconds:0} ms, " : "") +
                           $"scored in {(Clock.UtcNow - decoded).TotalMilliseconds:0} ms ({score}), {WaitingCount()} waiting");
            if (PostgresStorage.Ready)
                HistoryWriter.Enqueue(() => Repositories.MediaScores.SaveAsync(new MediaScoreRow(uniqueId, kind, model.Version, score,
                    new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero))), $"Saving the score of the image {uniqueId}");
            return score;
        }

        /// <summary>The file's bytes; null when Telegram refuses it (gone, too large) or it is over <see cref="MaxBytes"/>.</summary>
        private static async Task<byte[]> DownloadAsync(string fileId, CancellationToken cancel)
        {
            await using var content = new LimitedStream(MaxBytes);
            try
            {
                await Bot.Files.DownloadAsync(fileId, content, cancel);
            }
            catch (LimitedStream.TooLargeException)
            {
                return null;
            }
            catch (Exception e)
            {
                var api = Bot.AsApiError(e);
                // 400: the file is gone or too big for the Bot API. An answer, not a failure.
                if (api?.ErrorCode == 400) return null;
                throw;
            }
            return content.ToArray();
        }

        private static int WaitingCount()
        {
            lock (Lock) return Waiting.Count;
        }

        private static void Remember(string uniqueId, NsfwScore score)
        {
            if (!Known.TryAdd(uniqueId, score)) return;
            KnownOrder.Enqueue(uniqueId);
            while (KnownOrder.Count > Remembered) Known.Remove(KnownOrder.Dequeue());
        }

        /// <summary>Forgets every score and turns the check off (a scenario sets <see cref="Model"/> again).</summary>
        internal static void ResetForTests()
        {
            Model = null;
            Decoder = new MediaDecoder();
            lock (Lock)
            {
                Known.Clear();
                KnownOrder.Clear();
                foreach (var w in Waiting.Values) w.TrySetResult(null);
                Waiting.Clear();
                Urgent.Clear();
                Bulk.Clear();
                InUrgent.Clear();
                _draining = false;
                _scored = 0;
                _timedOut = 0;
                ImageLimit = TimeSpan.FromSeconds(60);
            }
        }

        /// <summary>A memory stream that refuses to grow past a limit, so a large file is not read whole.</summary>
        private sealed class LimitedStream : MemoryStream
        {
            internal sealed class TooLargeException : IOException
            {
            }

            private readonly long _limit;

            internal LimitedStream(long limit) => _limit = limit;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Length + count > _limit) throw new TooLargeException();
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (Length + buffer.Length > _limit) throw new TooLargeException();
                base.Write(buffer);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.Span);
                return ValueTask.CompletedTask;
            }
        }
    }
}
