using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Recovery
{
    /// <summary>
    /// Data recovered after a data loss, offered to each group's admins before anything is written (Daniel,
    /// 2026-10-29: opt in per group, opt out per change). A dev imports a <see cref="RecoveryFile"/> with /recovery
    /// and releases it; whichever edition is in the group then sends the offer where Detection reports go (the admin
    /// chat, or each admin in private; with none reachable, a note in the group links to it). It apologises, lists
    /// what would change, a page at a time, with the log channel posts each change was read from, and a tick per
    /// change, shared by all admins. Nothing changes until an admin taps Apply (and confirms), and then only what is
    /// ticked; Apply none, or nobody answering for <see cref="PostgresRecoveryRepository.Open"/>, changes nothing.
    /// <para>
    /// Each value is checked when it is applied: one that still holds what the recovery started from is set; a count
    /// (warns) that changed meanwhile gets the difference added; anything else that changed is skipped and listed.
    /// Restored warns go into the warn history. Members who end up at or over the group's warn limit are not banned:
    /// the admins are told, with a Ban button for each.
    /// </para>
    /// </summary>
    internal static class RecoveryOffers
    {
        internal const string Trigger = "rcv";
        internal const string StartPrefix = "recovery_";
        internal const int PageSize = 8;
        internal const string Reason = "Restored after data loss";
        internal static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

        /// <summary>The most Ban buttons under the result, and the most skipped changes named in it.</summary>
        internal const int MaxListed = 20;

        private static readonly ConcurrentDictionary<long, SemaphoreSlim> Gates = new();
        private static SemaphoreSlim Gate(long offerId) => Gates.GetOrAdd(offerId, _ => new SemaphoreSlim(1, 1));

        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Clock.WaitAsync(Every, ct);
                    if (ct.IsCancellationRequested) return;
                    await CheckOnceAsync();
                }
            }, stop, "RecoveryOffers");

        /// <summary>Sends the released offers of groups this edition is in, and closes the ones that expired.</summary>
        internal static async Task CheckOnceAsync()
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
                foreach (var (offerId, chatId) in await Repositories.Recovery.DueAsync(50))
                {
                    // A group of the other edition only, or one the bot left: not ours to send.
                    if (await ChatDetails.GetAsync(chatId) == null) continue;
                    if (!await Repositories.Recovery.ClaimAsync(offerId, BanHistory.Edition, now, now + PostgresRecoveryRepository.Open)) continue;
                    await SendAsync(offerId, chatId);
                }
                foreach (var offerId in await Repositories.Recovery.ExpireAsync(BanHistory.Edition, now))
                    if (await Repositories.Recovery.GetAsync(offerId) is { } expired)
                        await EditAllAsync(expired);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Sending and closing data recovery offers");
            }
        }

        private static async Task SendAsync(long offerId, long chatId)
        {
            var offer = await Repositories.Recovery.GetAsync(offerId);
            if (offer == null) return;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var (text, markup) = await PageAsync(lang, offer, 0);
            var copies = await DetectionReports.SendAsync(chatId, text, markup);
            await Repositories.Recovery.AddCopiesAsync(offerId, copies);
            if (copies.Count > 0) return;
            // No admin chat and no admin who started the bot: the group is told, and an admin opens it in private.
            try
            {
                var link = $"https://t.me/{Bot.Me.Username}?start={StartPrefix}{chatId}";
                var menu = new Menu(1) { Buttons = { new InlineButton(Methods.GetLocaleString(lang, "recoveryReview")) { Url = link } } };
                await Bot.Api.SendMessage(chatId, Methods.GetLocaleString(lang, "recoveryGroupNotice"), replyMarkup: Key.CreateMarkupFromMenu(menu));
            }
            catch (Exception e)
            {
                // 403 or 400: the bot can no longer post in the group. The offer stays open for /start.
                if (Bot.AsApiError(e)?.ErrorCode is not (400 or 403))
                    ErrorReporter.Report(e, $"Telling {chatId} about its data recovery offer");
            }
        }

        /// <summary>
        /// /start recovery_{chat} in private, from the note in the group: the offer, to an admin of the group.
        /// </summary>
        internal static async Task StartAsync(Message message, string payload)
        {
            var user = message.From.Id;
            var lang = Methods.GetGroupLanguage(message, false).Doc;
            if (!long.TryParse(payload, out var chatId) || !PostgresStorage.Ready ||
                await Repositories.Recovery.OpenForAsync(chatId) is not { } offer)
            {
                await Bot.Api.SendMessage(user, Methods.GetLocaleString(lang, "recoveryGone"));
                return;
            }
            lang = Methods.GetGroupLanguage(chatId).Doc;
            if (!await Methods.IsGroupAdminAsync(user, chatId))
            {
                await Bot.Api.SendMessage(user, Methods.GetLocaleString(lang, "recoveryNotAdmin"));
                return;
            }
            var (text, markup) = await PageAsync(lang, offer, 0);
            var sent = await Bot.Api.SendMessage(user, text, Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: markup,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true });
            await Repositories.Recovery.AddCopiesAsync(offer.OfferId, new[] { new ReportCopy(user, sent.MessageId) });
        }

        /// <summary>The group's newest offer, a message per page as its admins see it (without buttons), for a dev; null when none.</summary>
        internal static async Task<IReadOnlyList<string>> PreviewAsync(long chatId)
        {
            var offer = await Repositories.Recovery.LatestForAsync(chatId);
            if (offer == null) return null;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var pages = Math.Max(1, (offer.Items.Count + PageSize - 1) / PageSize);
            var parts = new List<string> { $"[{offer.Batch.FormatHTML()}, {offer.Status}, {offer.Items.Count} changes, {pages} pages]" };
            for (var page = 0; page < pages; page++)
                parts.Add((await PageAsync(lang, offer, page)).Item1);
            return parts;
        }

        // ------------------------------------------------------------------ buttons

        /// <summary>
        /// A button under an offer: <c>rcv:{chat}:{offer}:{op}:{n}</c> from an admin of the group. Ops: <c>pg</c> page n,
        /// <c>tg</c> tick or untick item n, <c>ap</c> ask to apply, <c>ok</c> apply, <c>no</c> apply none, <c>ban</c> ban
        /// the member of item n.
        /// </summary>
        internal static async Task PressAsync(CallbackQuery call, long chatId, long offerId, string op, int n)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (!PostgresStorage.Ready)
            {
                await Answer(call, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            var gate = Gate(offerId);
            await gate.WaitAsync();
            try
            {
                var offer = await Repositories.Recovery.GetAsync(offerId);
                if (offer == null || offer.ChatId != chatId)
                {
                    await Answer(call, Methods.GetLocaleString(lang, "recoveryGone"));
                    return;
                }
                var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
                var open = offer.Status == "sent" && offer.ExpiresAt > now;
                switch (op)
                {
                    case "pg" when open:
                        await Answer(call, null);
                        await ShowAsync(call, await PageAsync(lang, offer, n));
                        return;
                    case "tg" when open:
                        var item = offer.Items.FirstOrDefault(i => i.Position == n);
                        if (item == null || !await Repositories.Recovery.SelectAsync(offerId, n, !item.Selected, now))
                        {
                            await Answer(call, Methods.GetLocaleString(lang, "recoveryGone"));
                            return;
                        }
                        await Answer(call, null);
                        offer = await Repositories.Recovery.GetAsync(offerId);
                        await ShowAsync(call, await PageAsync(lang, offer, (n - 1) / PageSize));
                        return;
                    case "ap" when open:
                        await Answer(call, null);
                        await ShowAsync(call, Confirm(lang, offer, await DetectionReports.TitleAsync(chatId)));
                        return;
                    case "ok" or "no" when open:
                        await DecideAsync(call, lang, offer, op == "ok" ? "applied" : "declined");
                        return;
                    case "ban":
                        await BanAsync(call, lang, offer, n);
                        return;
                    default:
                        // Decided or expired since this copy was drawn: show what happened.
                        await Answer(call, Methods.GetLocaleString(lang, "recoveryGone"));
                        await ShowAsync(call, await ViewAsync(lang, offer));
                        return;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task DecideAsync(CallbackQuery call, XDocument lang, RecoveryOffer offer, string decision)
        {
            var actor = call.From.Id;
            var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
            var standing = await Repositories.Recovery.DecideAsync(offer.OfferId, decision, actor, now);
            if (standing is not { } decided)
            {
                await Answer(call, Methods.GetLocaleString(lang, "recoveryGone"));
                return;
            }
            var mine = decided.Decision == decision && decided.By == actor;
            if (mine)
            {
                var outcomes = decision == "applied"
                    ? await ApplyAsync(offer, actor)
                    : offer.Items.Select(i => new RecoveryOutcome(i.Position, "declined", null)).ToList();
                await Repositories.Recovery.SetOutcomesAsync(offer.OfferId, outcomes);
                Log(offer, actor, call.From.FirstName, decision == "applied"
                    ? $"recovery: applied {outcomes.Count(o => o.Outcome is "applied" or "partly")} of {offer.Items.Count} recovered changes"
                    : "recovery: applied none of the recovered changes");
            }
            await Answer(call, mine ? Methods.GetLocaleString(lang, "reportDone") : DecisionLine(lang, decided.Decision, decided.By, html: false));
            await EditAllAsync(await Repositories.Recovery.GetAsync(offer.OfferId), call.Message);
        }

        /// <summary>Applies the ticked items, each value checked against what it is now.</summary>
        internal static async Task<List<RecoveryOutcome>> ApplyAsync(RecoveryOffer offer, long actor)
        {
            var outcomes = new List<RecoveryOutcome>();
            var max = Methods.MaxWarns(offer.ChatId);
            foreach (var item in offer.Items)
            {
                if (!item.Selected)
                {
                    outcomes.Add(new RecoveryOutcome(item.Position, "unticked", null));
                    continue;
                }
                try
                {
                    outcomes.Add(await ApplyItemAsync(offer.ChatId, item, actor, max));
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Applying recovered item {item.Position} of offer {offer.OfferId} in {offer.ChatId}");
                    outcomes.Add(new RecoveryOutcome(item.Position, "skipped", JsonConvert.SerializeObject(new ItemResult { Failed = true })));
                }
            }
            return outcomes;
        }

        /// <summary>What applying an item did, stored with its outcome.</summary>
        internal sealed class ItemResult
        {
            [JsonProperty("skipped", NullValueHandling = NullValueHandling.Ignore)] public List<string> Skipped { get; set; }
            [JsonProperty("warns", NullValueHandling = NullValueHandling.Ignore)] public long? Warns { get; set; }
            [JsonProperty("max", NullValueHandling = NullValueHandling.Ignore)] public int? Max { get; set; }
            [JsonProperty("banned_by", NullValueHandling = NullValueHandling.Ignore)] public long? BannedBy { get; set; }
            [JsonProperty("failed", NullValueHandling = NullValueHandling.Ignore)] public bool? Failed { get; set; }
        }

        private static async Task<RecoveryOutcome> ApplyItemAsync(long chatId, RecoveryItem item, long actor, int max)
        {
            var result = new ItemResult();
            var applied = 0;
            var counts = new Dictionary<string, (long Was, long Now)>();
            foreach (var change in item.Changes)
            {
                var current = await Repositories.RecoveryValues.ReadAsync(change);
                if (RecoveryFile.IsCount(change))
                {
                    long Count(string v) => long.TryParse(v, out var x) ? x : 0;
                    var was = Count(current);
                    // Unchanged since: the recovered count. Changed (warned or forgiven since): the difference on top.
                    var now = was == Count(change.Before) ? Count(change.After) : Math.Max(0, was + Count(change.After) - Count(change.Before));
                    if (now != was) await Repositories.RecoveryValues.WriteAsync(change, now.ToString(CultureInfo.InvariantCulture));
                    counts[RecoveryFile.Suffix(change.Key)] = (was, now);
                    applied++;
                }
                else if (current == change.Before)
                {
                    await Repositories.RecoveryValues.WriteAsync(change, change.After);
                    applied++;
                }
                else if (current == change.After) applied++; // already so
                else (result.Skipped ??= new List<string>()).Add(RecoveryFile.Suffix(change.Key));
            }

            if (item.Kind == "warns" && item.MemberId is { } member)
            {
                long? total = counts.TryGetValue("totalWarns", out var t) ? t.Now : null;
                if (counts.TryGetValue("warns", out var w))
                {
                    History(chatId, member, WarnKind.Warn, w, total, actor);
                    result.Warns = w.Now;
                    if (w.Now >= max) result.Max = max;
                }
                if (counts.TryGetValue("prewarns", out var p)) History(chatId, member, WarnKind.Prewarn, p, null, actor);
            }
            var outcome = applied == item.Changes.Count ? "applied" : applied > 0 ? "partly" : "skipped";
            return new RecoveryOutcome(item.Position, outcome, JsonConvert.SerializeObject(result));
        }

        private static void History(long chatId, long member, WarnKind kind, (long Was, long Now) count, long? total, long actor)
        {
            if (count.Now > count.Was)
                WarnHistory.Added(chatId, member, kind, count.Now - count.Was, count.Now, total, actor, Reason);
            else if (count.Now < count.Was)
                WarnHistory.Removed(chatId, member, kind, count.Was - count.Now, count.Now, actor, Reason);
        }

        private static async Task BanAsync(CallbackQuery call, XDocument lang, RecoveryOffer offer, int position)
        {
            var item = offer.Items.FirstOrDefault(i => i.Position == position);
            var result = item?.Result != null ? JsonConvert.DeserializeObject<ItemResult>(item.Result) : null;
            if (offer.Status != "applied" || item?.MemberId is not { } member || result?.Max == null)
            {
                await Answer(call, Methods.GetLocaleString(lang, "recoveryGone"));
                return;
            }
            if (result.BannedBy == null)
            {
                if (!Methods.BanUser(offer.ChatId, member, lang, Reason, call.From.Id))
                {
                    await Answer(call, Methods.GetLocaleString(lang, "failed"));
                    return;
                }
                Methods.SaveBan(member, "maxWarn");
                result.BannedBy = call.From.Id;
                await Repositories.Recovery.SetOutcomesAsync(offer.OfferId,
                    new[] { new RecoveryOutcome(position, item.Outcome, JsonConvert.SerializeObject(result)) });
                Log(offer, call.From.Id, call.From.FirstName, "recovery: ban at the warn limit", member);
            }
            await Answer(call, Methods.GetLocaleString(lang, "reportDone"));
            await EditAllAsync(await Repositories.Recovery.GetAsync(offer.OfferId), call.Message);
        }

        private static void Log(RecoveryOffer offer, long actor, string actorName, string what, long? member = null)
        {
            Service.LogCommand(offer.ChatId, actor, Methods.FormatHTML(actorName), Service.GetCachedGroupName(offer.ChatId), what,
                member is { } m ? Service.ArgTargetName(m) : "");
        }

        // ------------------------------------------------------------------ messages

        /// <summary>Edits every copy (and <paramref name="pressed"/>, a copy sent from /start before it was stored) to what the offer is now.</summary>
        private static async Task EditAllAsync(RecoveryOffer offer, Message pressed = null)
        {
            if (offer == null) return;
            var lang = Methods.GetGroupLanguage(offer.ChatId).Doc;
            var (text, markup) = await ViewAsync(lang, offer);
            var copies = offer.Copies.ToList();
            if (pressed != null && !copies.Any(c => c.ChatId == pressed.Chat.Id && c.MessageId == pressed.MessageId))
                copies.Add(new ReportCopy(pressed.Chat.Id, pressed.MessageId));
            await DetectionReports.EditAsync(copies, text, markup);
        }

        private static async Task ShowAsync(CallbackQuery call, (string Text, InlineKeyboardMarkup Markup) view)
        {
            if (call.Message == null) return;
            await DetectionReports.EditAsync(new[] { new ReportCopy(call.Message.Chat.Id, call.Message.MessageId) }, view.Text, view.Markup);
        }

        /// <summary>The offer as it stands: a page to tick while open, else what was decided.</summary>
        private static async Task<(string, InlineKeyboardMarkup)> ViewAsync(XDocument lang, RecoveryOffer offer) =>
            offer.Status == "sent" && offer.ExpiresAt > new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero)
                ? await PageAsync(lang, offer, 0)
                : Result(lang, offer, await DetectionReports.TitleAsync(offer.ChatId));

        internal static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Page <paramref name="page"/> of an open offer: the apology, the items with their ticks, and the buttons.</summary>
        internal static async Task<(string, InlineKeyboardMarkup)> PageAsync(XDocument lang, RecoveryOffer offer, int page)
        {
            var pages = Math.Max(1, (offer.Items.Count + PageSize - 1) / PageSize);
            page = Math.Clamp(page, 0, pages - 1);
            var shown = offer.Items.Skip(page * PageSize).Take(PageSize).ToList();
            var title = await DetectionReports.TitleAsync(offer.ChatId);
            var lines = new List<string>
            {
                // Not GetLocaleString: it would escape the markup. Every argument is escaped here.
                Methods.GetLocaleStringNoFormat(lang, "recoveryOffer", title.FormatHTML(), Date(offer.PeriodFrom), Date(offer.PeriodTo),
                    Date(offer.ExpiresAt)),
                ""
            };
            lines.AddRange(shown.Select(i => $"{(i.Selected ? "✅" : "❌")} {i.Position}. {ItemText(lang, offer.ChatId, i)}"));
            lines.Add("");
            lines.Add(Methods.GetLocaleString(lang, "recoveryPage", page + 1, pages, offer.Items.Count(i => i.Selected), offer.Items.Count));

            var data = $"{Trigger}:{offer.ChatId}:{offer.OfferId}";
            var ticks = new Menu(4);
            foreach (var i in shown)
                ticks.Buttons.Add(new InlineButton($"{(i.Selected ? "✅" : "❌")} {i.Position}", $"{data}:tg:{i.Position}"));
            var nav = new Menu(2);
            if (page > 0) nav.Buttons.Add(new InlineButton("◀️", $"{data}:pg:{page - 1}"));
            if (page < pages - 1) nav.Buttons.Add(new InlineButton("▶️", $"{data}:pg:{page + 1}"));
            var decide = new Menu(1)
            {
                Buttons =
                {
                    new InlineButton(Methods.GetLocaleString(lang, "recoveryApply", offer.Items.Count(i => i.Selected)), $"{data}:ap:0")
                        { Style = KeyboardButtonStyle.Success },
                    new InlineButton(Methods.GetLocaleString(lang, "recoveryNone"), $"{data}:no:0")
                }
            };
            var menus = new[] { ticks, nav, decide }.Where(m => m.Buttons.Count > 0).ToArray();
            return (string.Join("\n", lines), Key.CreateMarkupFromMenus(menus));
        }

        private static (string, InlineKeyboardMarkup) Confirm(XDocument lang, RecoveryOffer offer, string title)
        {
            var data = $"{Trigger}:{offer.ChatId}:{offer.OfferId}";
            var menu = new Menu(2)
            {
                Buttons =
                {
                    new InlineButton(Methods.GetLocaleString(lang, "recoveryYes"), $"{data}:ok:0") { Style = KeyboardButtonStyle.Success },
                    new InlineButton(Methods.GetLocaleString(lang, "recoveryBack"), $"{data}:pg:0")
                }
            };
            return (Methods.GetLocaleString(lang, "recoveryConfirm", offer.Items.Count(i => i.Selected), offer.Items.Count, title),
                Key.CreateMarkupFromMenu(menu));
        }

        /// <summary>A decided or expired offer: who decided, what was applied, what was skipped, and who is at the warn limit.</summary>
        internal static (string, InlineKeyboardMarkup) Result(XDocument lang, RecoveryOffer offer, string title)
        {
            var lines = new List<string> { Methods.GetLocaleStringNoFormat(lang, "recoveryTitle", title.FormatHTML()), "" };
            InlineKeyboardMarkup markup = null;
            switch (offer.Status)
            {
                case "declined":
                    lines.Add(DecisionLine(lang, "declined", offer.DecidedBy));
                    break;
                case "expired":
                    lines.Add(Methods.GetLocaleString(lang, "recoveryExpired"));
                    break;
                case "applied":
                    var results = offer.Items.ToDictionary(i => i.Position,
                        i => i.Result != null ? JsonConvert.DeserializeObject<ItemResult>(i.Result) : new ItemResult());
                    lines.Add(DecisionLine(lang, "applied", offer.DecidedBy));
                    lines.Add(Methods.GetLocaleString(lang, "recoveryAppliedCount",
                        offer.Items.Count(i => i.Outcome is "applied" or "partly"), offer.Items.Count,
                        offer.Items.Count(i => i.Outcome == "unticked")));
                    var skipped = offer.Items.Where(i => i.Outcome is "skipped" or "partly").ToList();
                    if (skipped.Count > 0)
                    {
                        lines.Add("");
                        lines.Add(Methods.GetLocaleString(lang, "recoverySkipped"));
                        lines.AddRange(skipped.Take(MaxListed).Select(i => $"• {i.Position}. {ItemText(lang, offer.ChatId, i, evidence: false)}"));
                        if (skipped.Count > MaxListed) lines.Add($"… +{skipped.Count - MaxListed}");
                    }
                    var over = offer.Items.Where(i => i.MemberId != null && results[i.Position].Max != null).ToList();
                    if (over.Count > 0)
                    {
                        lines.Add("");
                        lines.Add(Methods.GetLocaleString(lang, "recoveryOverMax", over.Max(i => results[i.Position].Max.Value)));
                        var menu = new Menu(1);
                        foreach (var i in over.Take(MaxListed))
                        {
                            var r = results[i.Position];
                            var name = DetectionReports.StoredName(i.MemberId.Value);
                            if (r.BannedBy is { } by)
                                lines.Add("• " + Methods.GetLocaleStringNoFormat(lang, "recoveryBanned",
                                    DetectionReports.Mention(i.MemberId.Value, name), r.Warns, DetectionReports.Mention(by, DetectionReports.StoredName(by))));
                            else
                            {
                                lines.Add($"• {DetectionReports.Mention(i.MemberId.Value, name)}: {r.Warns}/{r.Max}");
                                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "recoveryBan", name),
                                    $"{Trigger}:{offer.ChatId}:{offer.OfferId}:ban:{i.Position}") { Style = KeyboardButtonStyle.Danger });
                            }
                        }
                        if (menu.Buttons.Count > 0) markup = Key.CreateMarkupFromMenu(menu);
                    }
                    break;
            }
            return (string.Join("\n", lines), markup);
        }

        private static string DecisionLine(XDocument lang, string decision, long? by, bool html = true)
        {
            var name = by is { } id ? DetectionReports.StoredName(id) : "?";
            return Methods.GetLocaleStringNoFormat(lang, $"recoveryDecided_{decision}",
                html ? (by is { } admin ? DetectionReports.Mention(admin, name) : "?") : name);
        }

        /// <summary>One item as admins read it (HTML): what changes, and the log channel posts it was read from.</summary>
        internal static string ItemText(XDocument lang, long chatId, RecoveryItem item, bool evidence = true)
        {
            string Value(string suffix) => item.Changes.FirstOrDefault(c => RecoveryFile.Suffix(c.Key) == suffix) is { } c
                ? $"{c.Before ?? "0"} → {c.After ?? "0"}" : null;
            var change = item.Changes[0];
            var on = change.After == "true"; // a set member, blocklist entry or report block added, not taken out
            var member = item.MemberId is { } m ? DetectionReports.Mention(m, DetectionReports.StoredName(m)) : null;
            string text = item.Kind switch
            {
                "warns" => member + ": " + string.Join(", ", new[]
                {
                    Value("warns") is { } w ? Methods.GetLocaleString(lang, "recoveryWarns", w) : null,
                    Value("totalWarns") is { } t ? Methods.GetLocaleString(lang, "recoveryTotalWarns", t) : null,
                    Value("prewarns") is { } p ? Methods.GetLocaleString(lang, "recoveryPrewarns", p) : null
                }.Where(s => s != null)),
                "reportblock" => member + ": " + Methods.GetLocaleString(lang, on ? "recoveryReportBlock" : "recoveryReportUnblock"),
                "inline" => Methods.GetLocaleString(lang, on ? "recoveryInlineBlock" : "recoveryInlineUnblock", change.Field),
                "stickerset" => Methods.GetLocaleString(lang, on ? "recoveryStickerBlock" : "recoveryStickerUnblock", change.Field),
                "extra" => Methods.GetLocaleString(lang,
                    change.After == null ? "recoveryExtraDelete" : change.Before == null ? "recoveryExtraRestore" : "recoveryExtraChange", change.Field),
                "warnreasons" => Methods.GetLocaleString(lang, "recoveryWarnReasons"),
                _ => item.Kind.FormatHTML()
            };
            if (!evidence || item.Evidence.Count == 0) return text;
            const int Links = 5;
            var links = item.Evidence.OrderBy(e => e.At).Take(Links)
                .Select(e => $"<a href=\"{Methods.GetChatMessageLink(e.Log, e.Post.ToString())}\">{e.At.UtcDateTime.ToString("dd.MM", CultureInfo.InvariantCulture)}</a>");
            var more = item.Evidence.Count > Links ? $" +{item.Evidence.Count - Links}" : "";
            return $"{text} ({string.Join(", ", links)}{more})";
        }

        private static Task Answer(CallbackQuery call, string text) => Bot.Api.AnswerCallbackQuery(call.Id, text);

        internal static void ResetForTests() => Gates.Clear();
    }
}
