using System;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;

namespace Enforcer5.Detection
{
    /// <summary>What the engine decided to do about one sign, in live mode.</summary>
    internal sealed record DetectionAction(long ChatId, long UserId, long? MessageId, string Sign, string Action,
        bool Delete, bool Report)
    {
        /// <summary>The reason recorded in <c>sanctions</c> and warns.</summary>
        internal string Reason => $"detect:{Sign}";
    }

    /// <summary>Carries out the engine's decisions. Replaceable in tests.</summary>
    internal interface IDetectionActions
    {
        void Execute(DetectionAction action);
    }

    /// <summary>
    /// Through the same paths as everything else: the enforcement queue (<c>Methods.*</c>), so raid
    /// mode, flood control and the journal apply. Runs dispatched; never blocks the caller.
    /// Reports are recorded but not sent yet: they arrive with build step 7.
    /// </summary>
    internal sealed class EnforcerDetectionActions : IDetectionActions
    {
        public void Execute(DetectionAction action)
        {
            if (action.Action is "none" or "score" or "report" && !action.Delete) return;
            Bot.DispatchAction(() =>
            {
                try
                {
                    var chatId = action.ChatId;
                    var userId = action.UserId;
                    if (action.Delete && action.MessageId is { } messageId)
                        EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, (int)messageId)).GetAwaiter().GetResult();
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    switch (action.Action)
                    {
                        case "warn":
                            Commands.Warn(userId, chatId, targetnick: userId.ToString(), reason: action.Reason);
                            break;
                        case "mute":
                            Methods.MuteUser(chatId, userId, lang, reason: action.Reason);
                            break;
                        case "tempmute":
                            Methods.TempMuteUser(chatId, userId, Clock.UtcNow.AddMinutes(Methods.GetGroupTempMuteTime(chatId)), lang,
                                reason: action.Reason);
                            break;
                        case "kick":
                            Methods.KickUser(chatId, userId, lang, action.Reason);
                            break;
                        case "tempban":
                            Commands.Tempban(userId, chatId, Methods.GetGroupTempbanTime(chatId), userId.ToString(), reason: action.Reason);
                            break;
                        case "ban":
                            Methods.BanUser(chatId, userId, lang, action.Reason);
                            break;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Detection action {action.Action} on {action.UserId} in {action.ChatId}");
                }
            }, "DetectionAction");
        }
    }
}
