using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Detection.Ml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Detection
{
    /// <summary>The model service's client (build step 15): Infinity's requests and answers, and cosines. No service.</summary>
    [TestClass]
    public class MlClientTests
    {
        private sealed class Recording : HttpMessageHandler
        {
            internal readonly List<(string Url, string Auth, JsonDocument Body)> Requests = new();
            internal string Answer = "";
            internal HttpStatusCode Status = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            {
                Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                    JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel))));
                return new HttpResponseMessage(Status) { Content = new StringContent(Answer) };
            }
        }

        [TestMethod]
        public async Task TheClient_SendsTextsWithThePrefix_AndImagesAsDataUris_WithTheKey()
        {
            var http = new Recording { Answer = @"{""data"":[{""index"":1,""embedding"":[0,2]},{""index"":0,""embedding"":[3,4]}]}" };
            var client = new InfinityClient("http://ml:7997/", "secret", "harrier", "Query: ", "clip", http);

            var vectors = await client.EmbedTextsAsync(new[] { "a", new string('x', 600) }, CancellationToken.None);

            var (url, auth, body) = http.Requests.Single();
            Assert.AreEqual("http://ml:7997/embeddings", url);
            Assert.AreEqual("Bearer secret", auth);
            Assert.AreEqual("harrier", body.RootElement.GetProperty("model").GetString());
            var input = body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.AreEqual("Query: a", input[0]);
            Assert.AreEqual("Query: ".Length + InfinityClient.MaxTextLength, input[1].Length, "cut as in the evals");
            // In input order, unit length.
            CollectionAssert.AreEqual(new[] { 0.6f, 0.8f }, vectors[0]);
            CollectionAssert.AreEqual(new[] { 0f, 1f }, vectors[1]);

            http.Answer = @"{""data"":[{""index"":0,""embedding"":[1,0]}]}";
            await client.EmbedImagesAsync(new[] { new MlImage(new byte[] { 1, 2, 3 }, "image/webp") }, CancellationToken.None);
            var image = http.Requests.Last().Body.RootElement;
            Assert.AreEqual("clip", image.GetProperty("model").GetString());
            Assert.AreEqual("image", image.GetProperty("modality").GetString());
            Assert.AreEqual("data:image/webp;base64,AQID", image.GetProperty("input")[0].GetString());
        }

        [TestMethod]
        public async Task TheClient_Throws_WhenTheServiceFails_OrAnswersTooFew()
        {
            var http = new Recording { Status = HttpStatusCode.ServiceUnavailable, Answer = "down" };
            var client = new InfinityClient("http://ml", null, "t", "", "i", http);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.EmbedTextsAsync(new[] { "a" }, CancellationToken.None));
            Assert.IsNull(http.Requests.Single().Auth, "no key, no header");

            http.Status = HttpStatusCode.OK;
            http.Answer = @"{""data"":[{""index"":0,""embedding"":[1]}]}";
            await Assert.ThrowsExactlyAsync<FormatException>(() => client.EmbedTextsAsync(new[] { "a", "b" }, CancellationToken.None));
        }

        [TestMethod]
        public void Cosine_OfUnitVectors_AndZeroForAnotherModel()
        {
            var a = Vectors.Normalized(Enumerable.Range(1, 37).Select(i => (float)i).ToArray());
            var b = Vectors.Normalized(Enumerable.Range(1, 37).Select(i => (float)(38 - i)).ToArray());
            double expected = a.Zip(b, (x, y) => (double)x * y).Sum();
            Assert.AreEqual(expected, Vectors.Cosine(a, b), 1e-5);
            Assert.AreEqual(1, Vectors.Cosine(a, a), 1e-5);
            Assert.AreEqual(0, Vectors.Cosine(a, new[] { 1f }));
            CollectionAssert.AreEqual(new[] { 0f, 0f }, Vectors.Normalized(new[] { 0f, 0f }));
        }
    }
}
