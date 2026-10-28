using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The filters (design section 5, build step 5): the presets a group switched on and its own regex
    /// rules, on every group message (text and captions, the sender's name and username) and on the
    /// names of everyone who joins. They replace the old Arabic and right-to-left checks, which are the
    /// <c>arabic</c> and <c>rtl</c> presets now, with their notices.
    /// <para>
    /// Matching runs inline from the context; whatever matched is handled in dispatched work, which
    /// first leaves out admins, members on the watch or auth list, and (when the group says so)
    /// established members and the text of messages via bots in the group. Every rule that matched is
    /// recorded (<see cref="DetectionEngine.Checked"/>, a rule on trial as <c>trial</c>); of the live
    /// ones, the strongest action is done, once.
    /// </para>
    /// </summary>
    internal static class FilterCheck
    {
        internal sealed record Hit(FilterRule Rule, FilterTarget Target);

        /// <summary>A group message, from the moderation path of the update handler.</summary>
        internal static void OnMessage(MessageContext ctx)
        {
            if (ctx.Watched) return;
            var message = ctx.Message;
            // A post made as a channel: the member behind it cannot be acted on.
            if (message.SenderChat != null || message.From == null) return;
            var filters = ctx.Policy?.Filters;
            var hits = Find(filters, message.From, TextOf(message));
            if (hits.Count == 0) return;
            Handle(ctx, filters, message.From, message, hits);
        }

        /// <summary>Everyone in a join message, each by their own name.</summary>
        internal static void OnJoin(MessageContext ctx)
        {
            var filters = ctx.Policy?.Filters;
            foreach (var joiner in ctx.Message.NewChatMembers ?? Array.Empty<User>())
            {
                if (joiner.Id == Bot.Me.Id) continue;
                var hits = Find(filters, joiner, null);
                if (hits.Count > 0) Handle(ctx, filters, joiner, null, hits);
            }
        }

        /// <summary>What a message's text target is: its text or caption, the addresses of its links, and the name it was forwarded from.</summary>
        internal static string TextOf(Message message)
        {
            var parts = new List<string> { message.Text ?? message.Caption };
            foreach (var entity in (message.Entities ?? Array.Empty<MessageEntity>()).Concat(message.CaptionEntities ?? Array.Empty<MessageEntity>()))
                if (entity.Url != null) parts.Add(entity.Url);
            switch (message.ForwardOrigin)
            {
                case MessageOriginUser user:
                    parts.Add($"{user.SenderUser.FirstName} {user.SenderUser.LastName} {user.SenderUser.Username}");
                    break;
                case MessageOriginHiddenUser hidden:
                    parts.Add(hidden.SenderUserName);
                    break;
                case MessageOriginChat chat:
                    parts.Add(chat.SenderChat.Title);
                    break;
                case MessageOriginChannel channel:
                    parts.Add(channel.Chat.Title);
                    break;
            }
            return string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        /// <summary>The rules that match; pure, for tests.</summary>
        internal static List<Hit> Find(FilterSettings filters, User person, string text)
        {
            var hits = new List<Hit>();
            if (filters == null || filters.Rules.Count == 0) return hits;
            var name = $"{person.FirstName} {person.LastName}".Trim();
            foreach (var rule in filters.Rules)
            {
                if (!rule.Enabled || rule.Kind == FilterRule.PresetKind && rule.Action == null) continue;
                if (text != null && rule.Matches(FilterTarget.Text, text)) hits.Add(new Hit(rule, FilterTarget.Text));
                else if (rule.Matches(FilterTarget.Name, name)) hits.Add(new Hit(rule, FilterTarget.Name));
                else if (rule.Matches(FilterTarget.Username, person.Username)) hits.Add(new Hit(rule, FilterTarget.Username));
            }
            return hits;
        }

        /// <summary>
        /// What a profile lookup found (step 11): a new member's bio (<see cref="FilterTarget.Bio"/>), or a channel they
        /// pointed at (<see cref="FilterTarget.Channel"/>: its title, description and username), against the group's own
        /// rules on it. Called from dispatched work.
        /// </summary>
        internal static void OnProfile(MessageContext ctx, User person, Message message, FilterTarget target, string value)
        {
            var filters = (ctx.Policy ?? DetectionSettings.For(ctx.ChatId)).Filters;
            var hits = FindIn(filters, target, value);
            if (hits.Count > 0) Handle(ctx, filters, person, message, hits);
        }

        /// <summary>The rules that match <paramref name="value"/> on one target; pure, for tests.</summary>
        internal static List<Hit> FindIn(FilterSettings filters, FilterTarget target, string value)
        {
            var hits = new List<Hit>();
            if (filters == null || string.IsNullOrEmpty(value)) return hits;
            foreach (var rule in filters.Rules)
                if (rule.Enabled && !(rule.Kind == FilterRule.PresetKind && rule.Action == null) && rule.Matches(target, value))
                    hits.Add(new Hit(rule, target));
            return hits;
        }

        private static void Handle(MessageContext ctx, FilterSettings filters, User person, Message message, List<Hit> found)
        {
            var chatId = ctx.ChatId;
            var viaBot = message?.ViaBot;
            var update = ctx.Update;
            Bot.DispatchAction(() =>
            {
                try
                {
                    var userId = person.Id;
                    if (Methods.IsGroupAdmin(userId, chatId)) return;
                    var trust = MemberTrust.OfAsync(chatId, userId).GetAwaiter().GetResult();
                    if (trust is TrustLevel.Approved or TrustLevel.Admin) return;
                    if (trust == TrustLevel.Established && !filters.IncludeEstablished) return;
                    var hits = found;
                    if (viaBot != null && filters.ExemptGroupBots && hits.Any(h => h.Target == FilterTarget.Text) &&
                        GroupBots.IsMemberAsync(chatId, viaBot.Id).GetAwaiter().GetResult())
                        hits = hits.Where(h => h.Target != FilterTarget.Text).ToList();
                    if (hits.Count == 0) return;

                    foreach (var hit in hits)
                    {
                        var action = hit.Rule.Action ?? "none";
                        DetectionEngine.Checked(chatId, userId, message, SignCatalog.Get(hit.Rule.Sign), action,
                            deleted: action == "delete" && !hit.Rule.OnTrial, reported: action == "report" && !hit.Rule.OnTrial,
                            evidence: new { rule = hit.Rule.Kind == FilterRule.PresetKind ? hit.Rule.Preset : hit.Rule.Id.ToString(), target = hit.Target.ToString().ToLowerInvariant() },
                            weight: action is "score" or "report" ? 2 : null, trial: hit.Rule.OnTrial);
                    }
                    var live = hits.Where(h => !h.Rule.OnTrial).ToList();
                    if (live.Count == 0) return;
                    var strongest = live.Select(h => h.Rule.Action ?? "none").Aggregate(DetectionEngine.Stronger);
                    var rule = live.First(h => (h.Rule.Action ?? "none") == strongest).Rule;
                    Act(chatId, person, message, update, ctx.Lang ?? Methods.GetGroupLanguage(chatId).Doc, rule, strongest);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Filters in {chatId}");
                }
            }, "Filters");
        }

        private sealed record Notices(string Kicked, string Banned, string Tempbanned, string Reason, string SavedAs);

        // The old checks' own notices and reasons, which the two presets keep.
        private static Notices NoticesFor(FilterRule rule) => rule.Kind == FilterRule.PresetKind
            ? rule.Preset switch
            {
                FilterPresets.Arabic => new Notices("kickedForNoEnglishScript", "bannedForNoEnglishScript", "tempbanForNoEnglishScript", "Non-Latin script", "arab"),
                FilterPresets.Rtl => new Notices("kickedForRtl", "bannedForRtl", "tempbannedForRtl", "Right-to-left text", "rtl"),
                _ => null
            }
            : null;

        /// <summary>How a rule is named to people: the preset's name, or #id for an admin's rule.</summary>
        internal static string Label(XDocument lang, FilterRule rule) => rule.Kind == FilterRule.PresetKind
            ? Methods.GetLocaleString(lang, $"filterPreset_{rule.Preset}")
            : $"#{rule.Id}";

        private static void Act(long chatId, User person, Message message, Update update, XDocument lang, FilterRule rule, string action)
        {
            var userId = person.Id;
            var name = person.Username != null ? $"{person.FirstName} (@{person.Username})" : person.FirstName;
            var own = NoticesFor(rule);
            var label = Label(lang, rule);
            var reason = own?.Reason ?? $"Filter {(rule.Kind == FilterRule.PresetKind ? rule.Preset : "#" + rule.Id)}";
            var quiet = Raids.RaidGuard.Quiet(chatId);
            void Tell(string reply)
            {
                Service.LogBotAction(chatId, reply, userId);
                if (!quiet) Bot.Send(reply, update);
            }

            switch (action)
            {
                case "delete":
                    if (message != null)
                        EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, message.MessageId)).GetAwaiter().GetResult();
                    break;
                case "warn":
                    Commands.Warn(userId, chatId, update, targetnick: userId.ToString(), reason: reason);
                    break;
                case "mute":
                    if (Methods.MuteUser(chatId, userId, lang, reason: reason))
                        Tell(Methods.GetLocaleString(lang, "mutedByFilter", $"{name}, {userId}", label));
                    break;
                case "tempmute":
                    var muteMinutes = Methods.GetGroupTempMuteTime(chatId);
                    if (Methods.TempMuteUser(chatId, userId, Clock.UtcNow.AddMinutes(muteMinutes), lang, reason: reason))
                        Tell(Methods.GetLocaleString(lang, "tempmutedByFilter", $"{name}, {userId}", label,
                            TimeSpan.FromMinutes(muteMinutes).ToString(@"dd\:hh\:mm")));
                    break;
                case "kick":
                    Methods.KickUser(chatId, userId, lang, reason);
                    Tell(own != null
                        ? Methods.GetLocaleString(lang, own.Kicked, $"{name}, {userId}")
                        : Methods.GetLocaleString(lang, "kickedByFilter", $"{name}, {userId}", label));
                    break;
                case "ban":
                    if (Methods.BanUser(chatId, userId, lang, reason))
                    {
                        Methods.SaveBan(userId, own?.SavedAs ?? "filter");
                        Methods.AddBanList(chatId, userId, person.FirstName, own != null
                            ? Methods.GetLocaleString(lang, own.Banned, ".")
                            : Methods.GetLocaleString(lang, "bannedByFilter", ".", label));
                        Tell(own != null
                            ? Methods.GetLocaleString(lang, own.Banned, $"{name}, {userId}")
                            : Methods.GetLocaleString(lang, "bannedByFilter", $"{name}, {userId}", label));
                    }
                    break;
                case "tempban":
                    var minutes = Methods.GetGroupTempbanTime(chatId);
                    var timeText = TimeSpan.FromMinutes(minutes).ToString(@"dd\:hh\:mm");
                    var text = own != null
                        ? Methods.GetLocaleString(lang, own.Tempbanned, $"{name}, {userId}", timeText)
                        : Methods.GetLocaleString(lang, "tempbannedByFilter", $"{name}, {userId}", label, timeText);
                    Service.LogBotAction(chatId, text, userId);
                    Commands.Tempban(userId, chatId, minutes, userId.ToString(), message: text, reason: reason);
                    break;
                case "report":
                    DetectionReports.Report(chatId, userId, message?.MessageId, rule.Sign, person);
                    break;
                // none and score: recorded above.
            }
        }
    }
}
