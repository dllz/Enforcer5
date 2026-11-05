using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Detection.Images;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection.Ml
{
    /// <summary>
    /// The queue to the model service (design section 9, build step 15): text and image embeddings for
    /// <see cref="SimilarToBanned"/>. Off unless <c>MlServiceUrl</c> is set. Built like <see cref="ImageScans"/>:
    /// <list type="bullet">
    /// <item>never on the message path: callers get a task, answered null when the vector cannot be had (off, the queue
    /// full, the service down, the file gone); only the vectors are then missing;</item>
    /// <item>one request at a time: the texts waiting go together (up to <see cref="TextBatch"/>), images one by one,
    /// downloaded from Telegram (at most <see cref="MaxImageBytes"/>) and kept only in memory;</item>
    /// <item>the same text or file is embedded once: answers are remembered (<see cref="Remembered"/>), image vectors also in
    /// <c>media_vectors</c> for both editions;</item>
    /// <item>at most <see cref="MaxWaiting"/> waiting, the oldest dropped; after a failure the service is left alone for
    /// <see cref="PauseAfterFailure"/>.</item>
    /// </list>
    /// </summary>
    internal static class Embeddings
    {
        internal const int MaxWaiting = 5000;
        internal const int TextBatch = 32;
        internal const int Remembered = 5000;
        internal const long MaxImageBytes = 2 * 1024 * 1024;
        internal static TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(1);
        internal static TimeSpan RequestLimit = TimeSpan.FromSeconds(15);

        /// <summary>The model service; null while it is off. Replaced in tests.</summary>
        internal static IMlClient Client { get; set; }

        internal static bool Enabled => Client != null;

        /// <summary>The queue's state for the watchdog and /queues.</summary>
        internal static readonly QueueStatus Status = QueueStatus.Register("embeddings");

        private abstract record Item(string Key)
        {
            internal readonly TaskCompletionSource<float[]> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed record TextItem(string Key, string Text) : Item(Key);

        private sealed record ImageItem(string Key, string FileId, string Kind, string MimeType) : Item(Key);

        private static readonly object Lock = new();
        private static readonly LinkedList<Item> Queue = new();
        private static readonly Dictionary<string, Item> Waiting = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float[]> Known = new(StringComparer.Ordinal);
        private static readonly Queue<string> KnownOrder = new();
        private static bool _draining;
        private static DateTime _pausedUntil;
        private static int _requests, _failures, _dropped;

        /// <summary>Requests sent to the service (tests, /queues).</summary>
        internal static int Requests => _requests;

        internal static int Failures => _failures;

        /// <summary>Reads the configuration; leaves it off when <c>MlServiceUrl</c> is not set.</summary>
        internal static void Start()
        {
            try
            {
                var url = RegHelper.GetRegValue("MlServiceUrl");
                if (string.IsNullOrWhiteSpace(url)) return;
                string Setting(string key, string fallback) => RegHelper.GetRegValue(key) is { Length: > 0 } v ? v : fallback;
                var client = new InfinityClient(url, RegHelper.GetRegValue("MlServiceKey"),
                    Setting("MlTextModel", "microsoft/harrier-oss-v1-270m"),
                    Setting("MlTextPrefix", "Instruct: Retrieve semantically similar text\nQuery: ").Replace("\\n", "\n"),
                    Setting("MlImageModel", "openai/clip-vit-base-patch32"));
                Client = client;
                LogHelper.Info($"Model service at {url}: text {client.TextModel}, images {client.ImageModel}.");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Starting the model service client");
            }
        }

        /// <summary>The vector of <paramref name="text"/>; null when it cannot be had.</summary>
        internal static Task<float[]> TextAsync(string text)
        {
            if (Client == null || string.IsNullOrWhiteSpace(text)) return Task.FromResult<float[]>(null);
            if (text.Length > InfinityClient.MaxTextLength) text = text[..InfinityClient.MaxTextLength]; // all the model is sent
            var key = "t:" + Client.TextModel + ":" + text;
            return Enqueue(key, () => new TextItem(key, text));
        }

        /// <summary>
        /// The vector of the image <paramref name="mediaUniqueId"/> (the file's id as labels and banned media know it),
        /// downloaded by <paramref name="fileId"/> (its small size or preview); null when it cannot be had.
        /// </summary>
        internal static Task<float[]> ImageAsync(string mediaUniqueId, string fileId, string kind, string mimeType)
        {
            if (Client == null || string.IsNullOrEmpty(mediaUniqueId) || string.IsNullOrEmpty(fileId)) return Task.FromResult<float[]>(null);
            var key = "i:" + Client.ImageModel + ":" + mediaUniqueId;
            return Enqueue(key, () => new ImageItem(key, fileId, kind, mimeType ?? "image/jpeg"));
        }

        /// <summary>Waits for a vector from dispatched work, parked, so the harness counts it as waiting.</summary>
        internal static Task<T> AwaitAsync<T>(Task<T> work) => Profiles.AwaitAsync(work);

        private static Task<float[]> Enqueue(string key, Func<Item> create)
        {
            bool start;
            Item item;
            Item dropped = null;
            lock (Lock)
            {
                if (Known.TryGetValue(key, out var known)) return Task.FromResult(known);
                if (Waiting.TryGetValue(key, out var waiting)) return waiting.Answer.Task;
                if (Clock.UtcNow < _pausedUntil) return Task.FromResult<float[]>(null);
                item = create();
                Waiting[key] = item;
                Queue.AddLast(item);
                if (Queue.Count > MaxWaiting)
                {
                    dropped = Queue.First.Value;
                    Queue.RemoveFirst();
                    Waiting.Remove(dropped.Key);
                    _dropped++;
                }
                Status.Waiting(Queue.Count);
                start = !_draining;
                _draining = true;
            }
            dropped?.Answer.TrySetResult(null);
            if (start) Bot.Dispatch(DrainAsync, "Embeddings");
            return item.Answer.Task;
        }

        private static async Task DrainAsync()
        {
            while (true)
            {
                List<Item> batch;
                lock (Lock)
                {
                    if (Queue.Count == 0)
                    {
                        _draining = false;
                        Status.Waiting(0);
                        return;
                    }
                    batch = new List<Item>();
                    if (Queue.First.Value is ImageItem)
                    {
                        batch.Add(Queue.First.Value);
                        Queue.RemoveFirst();
                    }
                    else
                    {
                        for (var node = Queue.First; node != null && batch.Count < TextBatch;)
                        {
                            var next = node.Next;
                            if (node.Value is TextItem)
                            {
                                batch.Add(node.Value);
                                Queue.Remove(node);
                            }
                            node = next;
                        }
                    }
                }
                Status.Started(batch[0] is ImageItem image ? $"image {image.Kind}" : $"{batch.Count} texts");
                float[][] vectors = null;
                if (Clock.UtcNow >= PausedUntil)
                {
                    try
                    {
                        vectors = batch[0] is ImageItem first
                            ? new[] { await ImageVectorAsync(first) }
                            : await RequestAsync(c => Client.EmbedTextsAsync(batch.Cast<TextItem>().Select(t => t.Text).ToList(), c));
                    }
                    catch (Exception e) when (Client != null)
                    {
                        Interlocked.Increment(ref _failures);
                        lock (Lock) _pausedUntil = Clock.UtcNow + PauseAfterFailure;
                        ErrorReporter.Report(e, $"Embedding {(batch[0] is ImageItem ? "an image" : $"{batch.Count} texts")}; " +
                                                $"the model service is left alone for {PauseAfterFailure.TotalMinutes:0} min");
                    }
                    catch (Exception)
                    {
                        // Turned off meanwhile (tests): nothing to say.
                    }
                }
                lock (Lock)
                {
                    for (var i = 0; i < batch.Count; i++)
                    {
                        Waiting.Remove(batch[i].Key);
                        if (vectors?[i] is { } v) Remember(batch[i].Key, v);
                    }
                    Status.Finished(Queue.Count);
                }
                for (var i = 0; i < batch.Count; i++) batch[i].Answer.TrySetResult(vectors?[i]);
            }
        }

        private static DateTime PausedUntil
        {
            get { lock (Lock) return _pausedUntil; }
        }

        /// <summary>One request, given up after <see cref="RequestLimit"/>.</summary>
        private static async Task<float[][]> RequestAsync(Func<CancellationToken, Task<float[][]>> request)
        {
            Interlocked.Increment(ref _requests);
            using var cancel = new CancellationTokenSource(RequestLimit);
            return await request(cancel.Token);
        }

        /// <summary>An image's vector: stored by the other edition or earlier, else downloaded and embedded, then stored.</summary>
        private static async Task<float[]> ImageVectorAsync(ImageItem item)
        {
            var client = Client;
            var uniqueId = item.Key[(item.Key.LastIndexOf(':') + 1)..];
            var stored = await StorageAsync();
            if (stored)
            {
                try
                {
                    if (await Repositories.Embeddings.GetMediaAsync(uniqueId, client.ImageModel) is { Length: > 0 } vector) return vector;
                }
                catch (Exception e)
                {
                    // Embedded again instead; only costs the time.
                    ErrorReporter.Report(e, $"Reading the vector of the image {uniqueId}");
                }
            }
            byte[] bytes;
            using (var cancel = new CancellationTokenSource(RequestLimit))
            {
                try
                {
                    bytes = await ImageScans.DownloadAsync(item.FileId, cancel.Token, MaxImageBytes);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            if (bytes == null) return null; // gone, or too large: an answer, not a failure
            var result = (await RequestAsync(c => client.EmbedImagesAsync(new[] { new MlImage(bytes, item.MimeType) }, c)))[0];
            if (stored)
                HistoryWriter.Enqueue(() => Repositories.Embeddings.SaveMediaAsync(uniqueId, client.ImageModel, item.Kind, result,
                    new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero)), $"Saving the vector of the image {uniqueId}");
            return result;
        }

        private static Task<bool> _storage;

        /// <summary>Whether the vector tables are there (PostgreSQL ready, pgvector enabled); asked once.</summary>
        internal static Task<bool> StorageAsync()
        {
            if (!PostgresStorage.Ready) return Task.FromResult(false);
            lock (Lock) return _storage ??= CheckStorageAsync();
        }

        private static async Task<bool> CheckStorageAsync()
        {
            try
            {
                var available = await Repositories.Embeddings.AvailableAsync();
                if (!available) LogHelper.Info("No embedding tables (pgvector is not enabled): vectors are not kept.");
                return available;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Looking for the embedding tables");
                lock (Lock) _storage = null; // asked again next time
                return false;
            }
        }

        private static void Remember(string key, float[] vector)
        {
            if (!Known.TryAdd(key, vector)) return;
            KnownOrder.Enqueue(key);
            while (KnownOrder.Count > Remembered) Known.Remove(KnownOrder.Dequeue());
        }

        internal static void ResetForTests()
        {
            Client = null;
            lock (Lock)
            {
                foreach (var item in Waiting.Values) item.Answer.TrySetResult(null);
                Queue.Clear();
                Waiting.Clear();
                Known.Clear();
                KnownOrder.Clear();
                _draining = false;
                _pausedUntil = default;
                _requests = _failures = _dropped = 0;
                _storage = null;
                PauseAfterFailure = TimeSpan.FromMinutes(1);
            }
        }
    }
}
