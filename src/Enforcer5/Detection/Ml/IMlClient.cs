using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Enforcer5.Detection.Ml
{
    /// <summary>An image to embed: its bytes as Telegram gave them (JPEG, WebP, PNG) and their type.</summary>
    internal sealed record MlImage(byte[] Bytes, string MimeType);

    /// <summary>
    /// The model service (design section 9, build step 15): text and image embeddings, each vector of unit length so a dot
    /// product is the cosine. One adapter per server; today <see cref="InfinityClient"/>.
    /// </summary>
    internal interface IMlClient
    {
        /// <summary>The text model's name, stored with each vector: vectors of another model are never compared.</summary>
        string TextModel { get; }

        /// <summary>The image model's name, stored with each vector.</summary>
        string ImageModel { get; }

        /// <summary>One vector per text, in order. Throws when the service fails.</summary>
        Task<float[][]> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancel);

        /// <summary>One vector per image, in order. Throws when the service fails.</summary>
        Task<float[][]> EmbedImagesAsync(IReadOnlyList<MlImage> images, CancellationToken cancel);
    }

    /// <summary>
    /// <c>michaelfeil/infinity</c> over REST (the test service on Apollo, Daniel 2026-11-05): <c>POST /embeddings</c> with a
    /// Bearer key, <c>{"model", "input"}</c> for texts and <c>{"model", "modality": "image", "input": [data URIs]}</c> for
    /// images. Texts get the model's instruction prefix (Harrier's "Retrieve semantically similar text", as in the evals).
    /// </summary>
    internal sealed class InfinityClient : IMlClient
    {
        /// <summary>Texts are cut to this many characters, as in the evals (/workspace/scratch/infinity/eval).</summary>
        internal const int MaxTextLength = 500;

        private readonly HttpClient _http;
        private readonly string _textPrefix;

        public string TextModel { get; }
        public string ImageModel { get; }

        internal InfinityClient(string baseUrl, string key, string textModel, string textPrefix, string imageModel,
            HttpMessageHandler handler = null)
        {
            _http = handler != null ? new HttpClient(handler) : new HttpClient();
            _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrEmpty(key)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            TextModel = textModel;
            ImageModel = imageModel;
            _textPrefix = textPrefix ?? "";
        }

        public Task<float[][]> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancel) =>
            PostAsync(new Dictionary<string, object>
            {
                ["model"] = TextModel,
                ["input"] = texts.Select(t => _textPrefix + (t.Length > MaxTextLength ? t[..MaxTextLength] : t)).ToArray()
            }, texts.Count, cancel);

        public Task<float[][]> EmbedImagesAsync(IReadOnlyList<MlImage> images, CancellationToken cancel) =>
            PostAsync(new Dictionary<string, object>
            {
                ["model"] = ImageModel,
                ["modality"] = "image",
                ["input"] = images.Select(i => $"data:{i.MimeType};base64,{Convert.ToBase64String(i.Bytes)}").ToArray()
            }, images.Count, cancel);

        private async Task<float[][]> PostAsync(object body, int count, CancellationToken cancel)
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("embeddings", content, cancel);
            var json = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Infinity answered {(int)response.StatusCode}: {Short(json)}", null, response.StatusCode);
            return Parse(json, count);
        }

        /// <summary>The vectors of an OpenAI-style answer, in input order and of unit length. Pure, for tests.</summary>
        internal static float[][] Parse(string json, int count)
        {
            using var document = JsonDocument.Parse(json);
            var vectors = new float[count][];
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var i) ? i.GetInt32() : Array.IndexOf(vectors, null);
                if (index < 0 || index >= count) throw new FormatException($"Embedding index {index} of {count}");
                var values = item.GetProperty("embedding");
                var vector = new float[values.GetArrayLength()];
                var n = 0;
                foreach (var v in values.EnumerateArray()) vector[n++] = v.GetSingle();
                vectors[index] = Vectors.Normalized(vector);
            }
            if (vectors.Any(v => v == null)) throw new FormatException($"Infinity answered fewer than {count} embeddings");
            return vectors;
        }

        private static string Short(string text) => text == null ? "" : text.Length > 200 ? text[..200] : text;
    }

    /// <summary>Vector arithmetic for unit vectors.</summary>
    internal static class Vectors
    {
        /// <summary>The vector scaled to unit length (a copy); all zeros stays zeros.</summary>
        internal static float[] Normalized(float[] v)
        {
            double sum = 0;
            foreach (var x in v) sum += x * x;
            if (sum <= 0 || double.IsNaN(sum)) return (float[])v.Clone();
            var scale = (float)(1 / Math.Sqrt(sum));
            var result = new float[v.Length];
            for (var i = 0; i < v.Length; i++) result[i] = v[i] * scale;
            return result;
        }

        /// <summary>The cosine of two unit vectors; 0 when their lengths differ (another model).</summary>
        internal static double Cosine(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0;
            var width = System.Numerics.Vector<float>.Count;
            var sum = System.Numerics.Vector<float>.Zero;
            var i = 0;
            for (; i <= a.Length - width; i += width)
                sum += new System.Numerics.Vector<float>(a, i) * new System.Numerics.Vector<float>(b, i);
            var dot = System.Numerics.Vector.Dot(sum, System.Numerics.Vector<float>.One);
            for (; i < a.Length; i++) dot += a[i] * b[i];
            return dot;
        }
    }
}
