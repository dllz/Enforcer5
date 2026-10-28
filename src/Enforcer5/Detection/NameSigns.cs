using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The name signs (design section 3, build step 10), about new members: a name that copies an admin or a bot of
    /// the group (<c>name_impersonation</c>, reported), and a name with admin, support, official and the like
    /// (<c>name_keyword</c>, scored).
    /// <para>
    /// Names are compared with lookalike letters made Latin and everything but letters left out
    /// (<see cref="MessageFingerprint.Normalize"/>: emoji, digits, separators and invisible characters), with the
    /// group's admins and its bots (bots among the admins, bots seen joining, this bot): the same, one letter apart
    /// (two for names over <see cref="ShortName"/> letters), or the admin's name with nothing but staff words added
    /// ("Daniel | Support", "daniel_admin"). Display names and usernames both count, either way round.
    /// </para>
    /// <para>
    /// Checked when someone joins (the admin list is looked up then) and on every message against the list at hand,
    /// so a name changed after joining is seen on the member's next message. A name that matched is remembered for
    /// a day, so it is recorded once, not on every message; names that match nothing cost no memory.
    /// </para>
    /// </summary>
    internal static class NameSigns
    {
        /// <summary>Names with fewer letters are only compared for being the same.</summary>
        internal const int CloseFrom = 5;

        /// <summary>Up to this many letters a name may be one letter off; longer ones two.</summary>
        internal const int ShortName = 12;

        /// <summary>Fewer letters than this is no name to copy ("Al", "J").</summary>
        internal const int MinLetters = 3;

        internal static readonly TimeSpan RememberFor = TimeSpan.FromHours(24);
        internal static readonly TimeSpan WarmEvery = TimeSpan.FromMinutes(10);
        private const int BotsPerChat = 20;
        private const int RememberedLimit = 50_000;

        /// <summary>Whom a name copies, and how.</summary>
        /// <param name="How">same, close (letters apart) or keyword (their name with staff words added).</param>
        /// <param name="Role">admin or bot.</param>
        internal sealed record Likeness(User Of, string Role, string How);

        // ------------------------------------------------------------------ entry points

        /// <summary>A group message (or edit), on the moderation path. Never waits.</summary>
        internal static void OnMessage(MessageContext ctx)
        {
            try
            {
                var message = ctx.Message;
                if (ctx.Watched || message.SenderChat != null || message.From == null || message.From.IsBot) return;
                if (message.NewChatMembers != null || message.LeftChatMember != null) return; // the join itself is checked on its own
                var chatId = ctx.ChatId;
                var person = message.From;
                if (AdminDirectory.IsKnownAdmin(chatId, person.Id)) return;
                var admins = AdminDirectory.Known(chatId);
                if (admins == null && Warm(chatId))
                {
                    // No list at hand: one lookup for the group (at most every few minutes), then this name.
                    Bot.Dispatch(async () =>
                    {
                        await AdminDirectory.GetAsync(chatId);
                        await CheckAsync(chatId, person, message, AdminDirectory.Known(chatId));
                    }, "NameSigns.Warm");
                    return;
                }
                var found = Judge(chatId, person, admins);
                if (found.Likeness == null && !found.Keyword) return;
                if (!Claim(chatId, person, found)) return;
                Bot.Dispatch(() => HitAsync(chatId, person, message, found), "NameSigns");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Name signs in {ctx.ChatId}");
            }
        }

        /// <summary>Someone joined (the service message or the member update; the same join seen both ways counts once).</summary>
        internal static void OnJoin(long chatId, User person)
        {
            if (person == null || Bot.Me != null && person.Id == Bot.Me.Id) return;
            if (person.IsBot)
            {
                BotSeen(chatId, person);
                return;
            }
            Bot.Dispatch(async () => await CheckAsync(chatId, person, null, await AdminDirectory.GetAsync(chatId)), "NameSigns.Join");
        }

        private static async Task CheckAsync(long chatId, User person, Message message, IReadOnlyList<ChatMember> admins)
        {
            try
            {
                if (admins != null && admins.Any(a => a.User.Id == person.Id)) return;
                var found = Judge(chatId, person, admins);
                if (found.Likeness == null && !found.Keyword) return;
                if (!Claim(chatId, person, found)) return;
                await HitAsync(chatId, person, message, found);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Name signs of {person.Id} in {chatId}");
            }
        }

        private static async Task HitAsync(long chatId, User person, Message message, Found found)
        {
            try
            {
                if (found.Likeness is { } like)
                    await DetectionEngine.HitAsync(chatId, person.Id, message,
                        new SignHit(SignCatalog.NameImpersonation.Name, JsonSerializer.Serialize(new { of = like.Of.Id, role = like.Role, how = like.How }))
                        {
                            Detail = Describe(like.Of)
                        });
                if (found.Keyword)
                    await DetectionEngine.HitAsync(chatId, person.Id, message, new SignHit(SignCatalog.NameKeyword.Name));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Name signs of {person.Id} in {chatId}");
            }
        }

        /// <summary>Whom the report names: their name and username as Telegram has them, never the member's own.</summary>
        internal static string Describe(User of)
        {
            var name = $"{of.FirstName} {of.LastName}".Trim();
            return of.Username != null ? $"{name} (@{of.Username})" : name;
        }

        // ------------------------------------------------------------------ judging

        internal sealed record Found(Likeness Likeness, bool Keyword)
        {
            /// <summary>What the remembered verdict is about: the name as compared, and what it matched.</summary>
            internal string Key(User person) =>
                $"{Name(person)}|{MessageFingerprint.Normalize(person.Username)}|{Likeness?.Of.Id}|{Keyword}";
        }

        private static Found Judge(long chatId, User person, IReadOnlyList<ChatMember> admins)
        {
            var targets = new List<(User User, string Role)>();
            if (admins != null)
                targets.AddRange(admins.Where(a => a.User.Id != person.Id).Select(a => (a.User, a.User.IsBot ? "bot" : "admin")));
            targets.AddRange(BotsOf(chatId).Where(b => b.Id != person.Id && targets.All(t => t.User.Id != b.Id)).Select(b => (b, "bot")));
            return new Found(Impersonates(person, targets), HasKeyword(person));
        }

        /// <summary>A display name or username with admin, support, official and the like (the staff words preset).</summary>
        internal static bool HasKeyword(User person) =>
            FilterPresets.HasStaffWord($"{person.FirstName} {person.LastName}") ||
            person.Username != null && FilterPresets.HasStaffWord(person.Username);

        /// <summary>The first of <paramref name="targets"/> whose name <paramref name="person"/>'s copies; null for none. Pure, for tests.</summary>
        internal static Likeness Impersonates(User person, IEnumerable<(User User, string Role)> targets)
        {
            var mine = Names(person);
            if (mine.Count == 0) return null;
            var staffWord = HasKeyword(person);
            Likeness close = null, keyword = null;
            foreach (var (target, role) in targets)
            {
                if (target == null || target.Id == person.Id) continue;
                foreach (var theirs in Names(target))
                foreach (var name in mine)
                {
                    if (name == theirs) return new Likeness(target, role, "same");
                    if (close == null && Close(name, theirs)) close = new Likeness(target, role, "close");
                    if (keyword == null && staffWord && WithStaffWords(name, theirs)) keyword = new Likeness(target, role, "keyword");
                }
            }
            return close ?? keyword;
        }

        /// <summary>The display name and the username, each with only its letters, folded; names too short to copy left out.</summary>
        private static List<string> Names(User user)
        {
            var names = new List<string>(2);
            var display = Name(user);
            if (display.Length >= MinLetters) names.Add(display);
            var username = MessageFingerprint.Normalize(user.Username);
            if (username.Length >= MinLetters && username != display) names.Add(username);
            return names;
        }

        private static string Name(User user) => MessageFingerprint.Normalize($"{user.FirstName} {user.LastName}");

        /// <summary>One letter apart (two for long names), counting two letters swapped as one; never for names under <see cref="CloseFrom"/>.</summary>
        internal static bool Close(string a, string b)
        {
            if (b.Length < CloseFrom || a.Length < CloseFrom) return false;
            var max = b.Length > ShortName ? 2 : 1;
            return Math.Abs(a.Length - b.Length) <= max && Distance(a, b, max) <= max;
        }

        // What may be added to an admin's name and still be them claiming to be staff ("Daniel | Official Support Team").
        private static readonly Regex StaffOnly = new Regex(
            "^(admin|admins|administrator|administration|support|official|moderator|moderators|mod|mods|helpdesk|help|customer|care|service|team|staff|owner|real|the|bot|desk|center|centre)+$",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

        /// <summary>
        /// <paramref name="theirs"/> with nothing but staff words around it: "danielsupport", "officialdaniel". A longer
        /// name that only starts the same ("maximilian" for "max") is not.
        /// </summary>
        internal static bool WithStaffWords(string mine, string theirs)
        {
            if (theirs.Length < MinLetters || mine.Length <= theirs.Length) return false;
            var at = mine.IndexOf(theirs, StringComparison.Ordinal);
            if (at < 0) return false;
            var rest = mine.Remove(at, theirs.Length);
            return rest.Length > 0 && StaffOnly.IsMatch(rest);
        }

        /// <summary>Edit distance with swaps of neighbours (optimal string alignment), stopping above <paramref name="max"/>.</summary>
        internal static int Distance(string a, string b, int max)
        {
            var rows = new int[3][];
            for (var r = 0; r < 3; r++) rows[r] = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) rows[1][j] = j;
            int[] before = rows[0], previous = rows[1], current = rows[2];
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var best = current[0];
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        value = Math.Min(value, before[j - 2] + 1);
                    current[j] = value;
                    best = Math.Min(best, value);
                }
                if (best > max) return max + 1;
                (before, previous, current) = (previous, current, before);
            }
            return previous[b.Length];
        }

        // ------------------------------------------------------------------ the group's bots

        private static readonly ConcurrentDictionary<long, List<User>> SeenBots = new();

        /// <summary>A bot joined the group: its name is one not to copy. Kept in memory, the last 20 a group.</summary>
        internal static void BotSeen(long chatId, User bot)
        {
            var list = SeenBots.GetOrAdd(chatId, _ => new List<User>());
            lock (list)
            {
                list.RemoveAll(b => b.Id == bot.Id);
                list.Add(bot);
                if (list.Count > BotsPerChat) list.RemoveRange(0, list.Count - BotsPerChat);
            }
        }

        private static List<User> BotsOf(long chatId)
        {
            var bots = new List<User>();
            if (Bot.Me != null) bots.Add(Bot.Me);
            if (SeenBots.TryGetValue(chatId, out var seen))
                lock (seen) bots.AddRange(seen);
            return bots;
        }

        // ------------------------------------------------------------------ remembering

        private static readonly ConcurrentDictionary<(long Chat, long User), (string Key, DateTime At)> Remembered = new();
        private static readonly ConcurrentDictionary<long, DateTime> Warmed = new();

        /// <summary>True the first time in a day this name is found so; later messages with the same name are left alone.</summary>
        private static bool Claim(long chatId, User person, Found found)
        {
            var now = Clock.UtcNow;
            var key = found.Key(person);
            var claimed = false;
            Remembered.AddOrUpdate((chatId, person.Id),
                _ => { claimed = true; return (key, now); },
                (_, old) =>
                {
                    if (old.Key == key && now - old.At < RememberFor) { claimed = false; return old; }
                    claimed = true;
                    return (key, now);
                });
            if (claimed && Remembered.Count > RememberedLimit) Prune(now);
            return claimed;
        }

        private static void Prune(DateTime now)
        {
            foreach (var old in Remembered.Where(p => now - p.Value.At >= RememberFor).Select(p => p.Key).ToList())
                Remembered.TryRemove(old, out _);
            if (Remembered.Count > RememberedLimit) Remembered.Clear();
        }

        /// <summary>True when the group's admin list may be looked up now for a message: once every <see cref="WarmEvery"/>.</summary>
        private static bool Warm(long chatId)
        {
            var now = Clock.UtcNow;
            var due = true;
            Warmed.AddOrUpdate(chatId, now, (_, last) =>
            {
                if (now - last < WarmEvery) { due = false; return last; }
                return now;
            });
            return due;
        }

        internal static void ResetForTests()
        {
            SeenBots.Clear();
            Remembered.Clear();
            Warmed.Clear();
        }
    }
}
