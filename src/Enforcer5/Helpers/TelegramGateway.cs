using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Every Telegram call the bot makes goes through here: Telegram.Bot's methods are all
    /// extensions over <see cref="ITelegramBotClient.SendRequest{TResponse}"/>, so wrapping the
    /// client covers the ~250 direct <c>Bot.Api</c> calls as well as the send helpers, with no change
    /// to any of them. <see cref="FloodControl"/> decides when each request may go.
    /// <list type="bullet">
    /// <item>Messages wait for their slot (at most <see cref="FloodControl.MessageMaxWait"/>) and a
    /// short 429 is waited out and retried once. Anything longer fails with a 429 at once.</item>
    /// <item>Moderation never waits for a 429 to pass and is never retried here: the caller gets the
    /// 429 straight away, and the enforcement queue decides when to try again.</item>
    /// <item>Uploads from a stream are never retried, since the stream cannot be read twice.</item>
    /// </list>
    /// The test harness wraps its fake Telegram in this too, so scenarios run the real pacing.
    /// </summary>
    internal sealed class TelegramGateway : ITelegramBotClient
    {
        private readonly ITelegramBotClient _inner;

        internal TelegramGateway(ITelegramBotClient inner, FloodControl flood)
        {
            _inner = inner;
            Flood = flood;
        }

        internal FloodControl Flood { get; }

        /// <summary>The client underneath, for the test harness.</summary>
        internal ITelegramBotClient Inner => _inner;

        public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            // Many calls are not awaited (fire and forget). The dispatched work that made one counts
            // as running until the call is done, not only while it waits for its slot: otherwise the
            // test harness saw it as finished the moment its wait ended and moved time on before the
            // request went out.
            var unit = Bot.CurrentWork;
            unit?.WaitStarted();
            try
            {
                return await SendPacedAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                unit?.WaitEnded();
            }
        }

        private async Task<TResponse> SendPacedAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
        {
            var method = request.MethodName;
            var lane = FloodControl.LaneOf(method);
            if (lane == Lane.Other)
                return await _inner.SendRequest(request, cancellationToken).ConfigureAwait(false);

            var chat = request is IChatTargetable targeted ? FloodControl.ChatKey(targeted.ChatId) : "";
            // Inline-message edits carry no chat; a 429 on one must not pause the whole lane.
            if (chat.Length == 0 && lane == Lane.Message) chat = "inline";
            var canRetry = !IsUpload(request);

            for (var attempt = 0; ; attempt++)
            {
                await WaitTurnAsync(lane, chat).ConfigureAwait(false);
                try
                {
                    return await _inner.SendRequest(request, cancellationToken).ConfigureAwait(false);
                }
                catch (ApiRequestException e) when (e.ErrorCode == 429 && e is not LocalRateLimitException)
                {
                    var seconds = e.Parameters?.RetryAfter ?? 5;
                    Flood.OnFlood(lane, method, chat == "inline" ? "inline" : chat, seconds);
                    if (attempt > 0 || !canRetry || !Flood.ShouldRetry(lane, seconds)) throw;
                    // The next turn waits out the pause, then the request goes once more.
                }
            }
        }

        private async Task WaitTurnAsync(Lane lane, string chat)
        {
            while (true)
            {
                var turn = Flood.TakeTurn(lane, chat);
                if (turn.Refused) throw new LocalRateLimitException(turn.RetryAfterSeconds);
                if (turn.Wait <= TimeSpan.Zero) return;
                await Clock.Delay(turn.Wait).ConfigureAwait(false);
                if (!turn.HasReservation) continue;
                // A reserved slot has come: only the bot-wide window is left to check.
                TimeSpan wait;
                while ((wait = Flood.TakeReservedBotSlot()) > TimeSpan.Zero)
                    await Clock.Delay(wait).ConfigureAwait(false);
                return;
            }
        }

        private static bool IsUpload<TResponse>(IRequest<TResponse> request)
        {
            for (var type = request.GetType(); type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FileRequestBase<>)) return true;
            }
            return false;
        }

        // The rest of ITelegramBotClient, passed through.
        public bool LocalBotServer => _inner.LocalBotServer;
        public long BotId => _inner.BotId;
        public TimeSpan Timeout { get => _inner.Timeout; set => _inner.Timeout = value; }
        public IExceptionParser ExceptionsParser { get => _inner.ExceptionsParser; set => _inner.ExceptionsParser = value; }
        public event AsyncEventHandler<ApiRequestEventArgs> OnMakingApiRequest
        {
            add => _inner.OnMakingApiRequest += value;
            remove => _inner.OnMakingApiRequest -= value;
        }
        public event AsyncEventHandler<ApiResponseEventArgs> OnApiResponseReceived
        {
            add => _inner.OnApiResponseReceived += value;
            remove => _inner.OnApiResponseReceived -= value;
        }
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => _inner.TestApi(cancellationToken);
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
            _inner.DownloadFile(filePath, destination, cancellationToken);
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
            _inner.DownloadFile(file, destination, cancellationToken);
    }
}
