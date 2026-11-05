using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection.Images
{
    /// <summary>
    /// One queue for every image the NSFW check looks at (Daniel, 2026-11-05): photos, stickers and thumbnails posted in a
    /// group, members' profile photos and the posts of their personal channel. Built like <see cref="Profiles"/>:
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
        internal const long MaxBytes = 10 * 1024 * 1024;
        internal const int Remembered = 50_000;

        private static readonly object Lock = new();
        private static readonly Dictionary<string, NsfwScore> Known = new(StringComparer.Ordinal);
        private static readonly Queue<string> KnownOrder = new();
        private static readonly Dictionary<string, TaskCompletionSource<NsfwScore>> Waiting = new(StringComparer.Ordinal);
        private static readonly Queue<(string UniqueId, string FileId, string Kind)> Queue = new();
        private static bool _draining;
        private static int _scored;

        /// <summary>The models; null while they are off. Replaced in tests.</summary>
        internal static INsfwModel Model { get; set; }

        internal static bool Enabled => Model != null;

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
        /// scored; null when it could not be (off, queue full, too large, Telegram refused).
        /// </summary>
        /// <param name="kind">photo, sticker, thumbnail, profile_photo or channel_post: kept with the score.</param>
        internal static Task<NsfwScore> ScoreAsync(string uniqueId, string fileId, string kind)
        {
            if (Model == null || string.IsNullOrEmpty(uniqueId) || string.IsNullOrEmpty(fileId)) return Task.FromResult<NsfwScore>(null);
            bool start;
            TaskCompletionSource<NsfwScore> waiting;
            lock (Lock)
            {
                if (Known.TryGetValue(uniqueId, out var known)) return Task.FromResult(known);
                if (Waiting.TryGetValue(uniqueId, out waiting)) return waiting.Task;
                if (Queue.Count >= MaxWaiting) return Task.FromResult<NsfwScore>(null);
                waiting = new TaskCompletionSource<NsfwScore>(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiting[uniqueId] = waiting;
                Queue.Enqueue((uniqueId, fileId, kind));
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
                (string UniqueId, string FileId, string Kind) next;
                lock (Lock)
                {
                    if (Queue.Count == 0)
                    {
                        _draining = false;
                        return;
                    }
                    next = Queue.Dequeue();
                }
                NsfwScore score = null;
                try
                {
                    score = await ScoreOneAsync(next.UniqueId, next.FileId, next.Kind);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Scoring the image {next.UniqueId} ({next.Kind})");
                }
                TaskCompletionSource<NsfwScore> waiting;
                lock (Lock)
                {
                    if (score != null) Remember(next.UniqueId, score);
                    Waiting.Remove(next.UniqueId, out waiting);
                }
                waiting?.TrySetResult(score);
            }
        }

        private static async Task<NsfwScore> ScoreOneAsync(string uniqueId, string fileId, string kind)
        {
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
            var image = await DownloadAsync(fileId);
            var downloaded = Clock.UtcNow;
            if (image == null)
            {
                LogHelper.Info($"Image {kind} {uniqueId}: not downloaded ({(downloaded - started).TotalMilliseconds:0} ms), {WaitingCount()} waiting");
                return null;
            }
            var score = model.Score(image);
            Interlocked.Increment(ref _scored);
            LogHelper.Info($"Image {kind} {uniqueId}: {image.Length / 1024} KB in {(downloaded - started).TotalMilliseconds:0} ms, " +
                           $"scored in {(Clock.UtcNow - downloaded).TotalMilliseconds:0} ms ({score}), {WaitingCount()} waiting");
            if (PostgresStorage.Ready)
                HistoryWriter.Enqueue(() => Repositories.MediaScores.SaveAsync(new MediaScoreRow(uniqueId, kind, model.Version, score,
                    new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero))), $"Saving the score of the image {uniqueId}");
            return score;
        }

        /// <summary>The file's bytes; null when Telegram refuses it (gone, too large) or it is over <see cref="MaxBytes"/>.</summary>
        private static async Task<byte[]> DownloadAsync(string fileId)
        {
            await using var content = new LimitedStream(MaxBytes);
            try
            {
                await Bot.Files.DownloadAsync(fileId, content);
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
            lock (Lock)
            {
                Known.Clear();
                KnownOrder.Clear();
                foreach (var w in Waiting.Values) w.TrySetResult(null);
                Waiting.Clear();
                Queue.Clear();
                _draining = false;
                _scored = 0;
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
