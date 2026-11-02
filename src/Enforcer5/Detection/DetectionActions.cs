using System;
using Enforcer5.Actions;
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

        /// <summary>The member, when the message showed who they are; for the report.</summary>
        internal Telegram.Bot.Types.User Member { get; init; }

        /// <summary>Their score after the sign, for the report.</summary>
        internal int Score { get; init; }

        /// <summary>What the report says about the sign (what an edit changed), or null.</summary>
        internal string Detail { get; init; }
    }

    /// <summary>Carries out the engine's decisions. Replaceable in tests.</summary>
    internal interface IDetectionActions
    {
        void Execute(DetectionAction action);
    }

    /// <summary>
    /// Through the same paths as everything else: the enforcement queue (<c>Methods.*</c>), so raid
    /// mode, flood control and the journal apply. Runs dispatched; never blocks the caller.
    /// Reports go to the admins (<see cref="DetectionReports"/>); every action taken gets a line in the
    /// group's log channel.
    /// </summary>
    internal sealed class EnforcerDetectionActions : IDetectionActions
    {
        public void Execute(DetectionAction action)
        {
            if (action.Report)
                DetectionReports.Report(action.ChatId, action.UserId, action.MessageId, action.Sign, action.Member, action.Score, action.Detail);
            var deleteOnly = action.Action is "none" or "score" or "report";
            if (deleteOnly && !action.Delete) return;
            // A delete alone is logged here; an action logs its own line once it went through.
            var line = LogLine(action);
            if (deleteOnly && line != null) Service.LogBotAction(action.ChatId, line, action.UserId);
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
                            Commands.Warn(userId, chatId, targetnick: userId.ToString(), reason: action.Reason, source: ActionSource.Detection,
                                logLine: line);
                            break;
                        case "mute":
                            Methods.MuteUser(chatId, userId, lang, reason: action.Reason, source: ActionSource.Detection, logLine: line);
                            break;
                        case "tempmute":
                            Methods.TempMuteUser(chatId, userId, Clock.UtcNow.AddMinutes(Methods.GetGroupTempMuteTime(chatId)), lang,
                                reason: action.Reason, source: ActionSource.Detection, logLine: line);
                            break;
                        case "kick":
                            Methods.KickUser(chatId, userId, lang, action.Reason, source: ActionSource.Detection, logLine: line);
                            break;
                        case "tempban":
                            Commands.Tempban(userId, chatId, Methods.GetGroupTempbanTime(chatId), userId.ToString(), reason: action.Reason,
                                source: ActionSource.Detection, logLine: line);
                            break;
                        case "ban":
                            Methods.BanUser(chatId, userId, lang, action.Reason, source: ActionSource.Detection, logLine: line);
                            break;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Detection action {action.Action} on {action.UserId} in {action.ChatId}");
                }
            }, "DetectionAction");
        }

        /// <summary>The group's log channel line for what Detection did (design section 6); null when it cannot be built.</summary>
        private static string LogLine(DetectionAction action)
        {
            try
            {
                var lang = Methods.GetGroupLanguage(action.ChatId).Doc;
                var what = action.Action is "none" or "score" or "report" ? "delete" : action.Action;
                var name = action.Member != null ? action.Member.FirstName : action.UserId.ToString();
                var label = DetectionReports.SignLabel(lang, action.Sign);
                return Methods.GetLocaleString(lang, "detectionLogLine", Methods.GetLocaleString(lang, $"filterAction_{what}"),
                    $"{name}, {action.UserId}", label);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Logging the Detection action on {action.UserId} in {action.ChatId}");
                return null;
            }
        }
    }
}
