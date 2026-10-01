using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>Fetches a file that a user sent to the bot.</summary>
    internal interface ITelegramFileDownloader
    {
        /// <summary>Writes the file with <paramref name="fileId"/> to <paramref name="destination"/>.</summary>
        Task DownloadAsync(string fileId, Stream destination, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Downloads files in whichever way the configured Bot API server supports.
    ///
    /// The library's own download fetches {server}/file/bot{token}/{path}. Our self-hosted Bot API
    /// server does not serve that and answers 404; its files are published on the same host on
    /// port 80 as /file/{token}/{path} instead, which is what blackwolf has used since 2022. So:
    /// <list type="number">
    /// <item>A server in --local mode returns an absolute path. If that file is readable on this
    /// host, it is copied from disk.</item>
    /// <item>With a file URL configured (see <see cref="ResolveFileBaseUrl"/>), it is fetched from
    /// {fileUrl}/{token}/{path}.</item>
    /// <item>Otherwise the library downloads it, which is right for the official Bot API.</item>
    /// </list>
    /// </summary>
    internal sealed class TelegramFileDownloader : ITelegramFileDownloader
    {
        private readonly ITelegramBotClient _client;
        private readonly HttpClient _http;
        private readonly string _fileBaseUrl;
        private readonly string _token;

        public TelegramFileDownloader(ITelegramBotClient client, HttpClient http, string fileBaseUrl, string token)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _fileBaseUrl = string.IsNullOrWhiteSpace(fileBaseUrl) ? null : fileBaseUrl.TrimEnd('/');
            _token = token ?? throw new ArgumentNullException(nameof(token));
        }

        /// <summary>
        /// Where files are fetched from: an explicit TelegramFileUrl wins; otherwise, with a
        /// self-hosted server, the same host without port plus /file (the blackwolf convention);
        /// otherwise null, meaning the library's own download against the official API.
        /// </summary>
        internal static string ResolveFileBaseUrl(string telegramFileUrl, string telegramServerUrl)
        {
            if (!string.IsNullOrWhiteSpace(telegramFileUrl)) return telegramFileUrl.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(telegramServerUrl)) return null;
            if (!Uri.TryCreate(telegramServerUrl.Trim(), UriKind.Absolute, out var server))
                throw new InvalidOperationException($"TelegramServerUrl '{telegramServerUrl}' is not an absolute URL.");
            return $"{server.Scheme}://{server.Host}/file";
        }

        /// <summary>
        /// The download URL. The path is appended as the server returns it, which is exactly what
        /// blackwolf does; for an absolute path that gives a double slash, which nginx merges.
        /// </summary>
        internal static string BuildUrl(string fileBaseUrl, string token, string filePath) =>
            $"{fileBaseUrl}/{token}/{filePath}";

        /// <summary>The text with the bot token replaced, for anything shown to a user or logged.</summary>
        internal static string Redact(string text, string token) =>
            string.IsNullOrEmpty(token) || text == null ? text : text.Replace(token, "<token>");

        public async Task DownloadAsync(string fileId, Stream destination, CancellationToken cancellationToken = default)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            var file = await _client.GetFile(fileId, cancellationToken);
            var path = file.FilePath;
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("Telegram returned no file path; the file may be too large to download.");

            if (await TryCopyLocalAsync(path, destination, cancellationToken)) return;

            if (_fileBaseUrl == null)
            {
                await _client.DownloadFile(path, destination, cancellationToken);
                return;
            }

            await DownloadHttpAsync(BuildUrl(_fileBaseUrl, _token, path), destination, cancellationToken);
        }

        private static async Task<bool> TryCopyLocalAsync(string path, Stream destination, CancellationToken cancellationToken)
        {
            if (!Path.IsPathRooted(path) || !System.IO.File.Exists(path)) return false;

            FileStream source;
            try
            {
                source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                // The Bot API server's directory is usually not readable by this service; use HTTP.
                return false;
            }
            using (source)
            {
                await source.CopyToAsync(destination, cancellationToken);
            }
            return true;
        }

        private async Task DownloadHttpAsync(string url, Stream destination, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new IOException($"Could not download {Redact(url, _token)}: {Redact(e.Message, _token)}");
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new IOException($"Downloading {Redact(url, _token)} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                await response.Content.CopyToAsync(destination, cancellationToken);
            }
        }
    }
}
