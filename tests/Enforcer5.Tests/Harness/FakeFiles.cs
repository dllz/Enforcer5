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

        private readonly ConcurrentDictionary<string, byte> _hanging = new();

        internal void Add(string fileId, byte[] content) => _files[fileId] = content;

        /// <summary>Every file id the bot downloaded, in order.</summary>
        internal readonly ConcurrentQueue<string> Downloaded = new();

        /// <summary>A download of <paramref name="fileId"/> never ends: it only stops when it is cancelled.</summary>
        internal void Hang(string fileId) => _hanging[fileId] = 0;

        // Waits on the bot's clock (so the harness can move time on) until cancelled.
        private static async Task HangAsync(CancellationToken cancel)
        {
            await Enforcer5.Helpers.Clock.WaitAsync(System.TimeSpan.FromDays(365), cancel);
            throw new System.OperationCanceledException(cancel);
        }

        public Task DownloadAsync(string fileId, Stream destination, CancellationToken cancellationToken = default)
        {
            Downloaded.Enqueue(fileId);
            if (_hanging.ContainsKey(fileId)) return HangAsync(cancellationToken);
            if (!_files.TryGetValue(fileId, out var content))
                throw new ApiRequestException("Bad Request: invalid file_id", 400);
            return destination.WriteAsync(content, 0, content.Length, cancellationToken);
        }
    }
}
