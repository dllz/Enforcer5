using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Enforcer5.Tests
{
    [TestClass]
    public class TelegramFileDownloaderTests
    {
        private const string Token = "123456:secret-token";

        /// <summary>Answers every request with a fixed response and records the URL.</summary>
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public string RequestedUrl { get; private set; }

            public FakeHandler(HttpStatusCode status, string body = "") { _status = status; _body = body; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestedUrl = request.RequestUri.ToString();
                return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
            }
        }

        private static Mock<ITelegramBotClient> ClientReturning(string filePath)
        {
            var client = new Mock<ITelegramBotClient>();
            client.Setup(c => c.SendRequest(It.IsAny<GetFileRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TGFile { FileId = "id", FileUniqueId = "u", FilePath = filePath });
            return client;
        }

        private static string Read(MemoryStream stream) => Encoding.UTF8.GetString(stream.ToArray());

        [DataTestMethod]
        // The production layout: blackwolf's working URL is the server host on port 80 plus /file.
        [DataRow(null, "http://192.168.0.51:8081", "http://192.168.0.51/file")]
        [DataRow(null, "http://192.168.0.51:8081/bot", "http://192.168.0.51/file")]
        [DataRow("", "https://bots.example.org:8081/", "https://bots.example.org/file")]
        // An explicit setting wins.
        [DataRow("http://files.example.org/tg/", "http://192.168.0.51:8081", "http://files.example.org/tg")]
        // Official API: the library downloads.
        [DataRow(null, null, null)]
        [DataRow(" ", "", null)]
        public void ResolveFileBaseUrl_FollowsTheConfiguration(string fileUrl, string serverUrl, string expected)
        {
            Assert.AreEqual(expected, TelegramFileDownloader.ResolveFileBaseUrl(fileUrl, serverUrl));
        }

        [TestMethod]
        public void ResolveFileBaseUrl_RejectsAnInvalidServerUrl()
        {
            Assert.ThrowsException<InvalidOperationException>(() => TelegramFileDownloader.ResolveFileBaseUrl(null, "not a url"));
        }

        [TestMethod]
        public void BuildUrl_MatchesBlackwolf()
        {
            // Werewolf Control/Helpers/LanguageHelper.cs builds {host}/file/{token}/{file.FilePath}.
            Assert.AreEqual("http://192.168.0.51/file/123456:secret-token/documents/file_1.xml",
                TelegramFileDownloader.BuildUrl("http://192.168.0.51/file", Token, "documents/file_1.xml"));
        }

        [TestMethod]
        public async Task ConfiguredFileUrl_IsUsedInsteadOfTheLibraryDownload()
        {
            var handler = new FakeHandler(HttpStatusCode.OK, "<strings/>");
            var client = ClientReturning("documents/file_1.xml");
            var downloader = new TelegramFileDownloader(client.Object, new HttpClient(handler), "http://192.168.0.51/file", Token);

            var destination = new MemoryStream();
            await downloader.DownloadAsync("id", destination);

            Assert.AreEqual("<strings/>", Read(destination));
            Assert.AreEqual("http://192.168.0.51/file/123456:secret-token/documents/file_1.xml", handler.RequestedUrl);
            client.Verify(c => c.DownloadFile(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task FailedDownload_NamesTheUrlWithoutTheToken()
        {
            var handler = new FakeHandler(HttpStatusCode.NotFound);
            var downloader = new TelegramFileDownloader(ClientReturning("documents/file_1.xml").Object,
                new HttpClient(handler), "http://192.168.0.51/file", Token);

            var error = await Assert.ThrowsExceptionAsync<IOException>(() => downloader.DownloadAsync("id", new MemoryStream()));

            StringAssert.Contains(error.Message, "http://192.168.0.51/file/<token>/documents/file_1.xml");
            StringAssert.Contains(error.Message, "404");
            Assert.IsFalse(error.Message.Contains("secret-token"), "the token never reaches the chat");
        }

        [TestMethod]
        public async Task WithoutAFileUrl_TheLibraryDownloads()
        {
            var client = ClientReturning("documents/file_1.xml");
            var handler = new FakeHandler(HttpStatusCode.OK);
            var downloader = new TelegramFileDownloader(client.Object, new HttpClient(handler), null, Token);

            await downloader.DownloadAsync("id", new MemoryStream());

            client.Verify(c => c.DownloadFile("documents/file_1.xml", It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.IsNull(handler.RequestedUrl);
        }

        [TestMethod]
        public async Task LocalModePath_ThatExistsHere_IsCopiedFromDisk()
        {
            var path = Path.Combine(Path.GetTempPath(), $"enforcer5-file-{Guid.NewGuid():N}.xml");
            System.IO.File.WriteAllText(path, "<strings>local</strings>");
            try
            {
                var handler = new FakeHandler(HttpStatusCode.OK, "from http");
                var downloader = new TelegramFileDownloader(ClientReturning(path).Object,
                    new HttpClient(handler), "http://192.168.0.51/file", Token);

                var destination = new MemoryStream();
                await downloader.DownloadAsync("id", destination);

                Assert.AreEqual("<strings>local</strings>", Read(destination));
                Assert.IsNull(handler.RequestedUrl, "no HTTP request when the file is on this host");
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [TestMethod]
        public async Task LocalModePath_ThatIsNotHere_FallsBackToHttp()
        {
            var path = Path.Combine(Path.GetTempPath(), $"enforcer5-missing-{Guid.NewGuid():N}", "file_1.xml");
            var handler = new FakeHandler(HttpStatusCode.OK, "from http");
            var downloader = new TelegramFileDownloader(ClientReturning(path).Object,
                new HttpClient(handler), "http://192.168.0.51/file", Token);

            var destination = new MemoryStream();
            await downloader.DownloadAsync("id", destination);

            Assert.AreEqual("from http", Read(destination));
            Assert.AreEqual(TelegramFileDownloader.BuildUrl("http://192.168.0.51/file", Token, path), handler.RequestedUrl);
        }

        [TestMethod]
        public async Task MissingFilePath_IsReported()
        {
            var downloader = new TelegramFileDownloader(ClientReturning(null).Object,
                new HttpClient(new FakeHandler(HttpStatusCode.OK)), "http://192.168.0.51/file", Token);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => downloader.DownloadAsync("id", new MemoryStream()));
        }
    }
}
