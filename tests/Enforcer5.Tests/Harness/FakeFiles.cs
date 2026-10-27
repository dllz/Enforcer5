using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Telegram.Bot.Exceptions;

namespace Enforcer5.Tests.Harness
{
    /// <summary>Files members sent, downloaded by file id as the bot would from Telegram.</summary>
    internal sealed class FakeFiles : ITelegramFileDownloader
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new();

        internal void Add(string fileId, byte[] content) => _files[fileId] = content;

        public Task DownloadAsync(string fileId, Stream destination, CancellationToken cancellationToken = default)
        {
            if (!_files.TryGetValue(fileId, out var content))
                throw new ApiRequestException("Bad Request: invalid file_id", 400);
            return destination.WriteAsync(content, 0, content.Length, cancellationToken);
        }
    }
}
