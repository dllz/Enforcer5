using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Enforcement;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Seeded random raids, many at a time: several groups, regulars and real newcomers talking,
    /// raiders joining fast and spamming in different ways, auto and human mode, 429 storms, the bot
    /// losing its rights and the process crashing at random moments. After each, the same rules must
    /// hold (see <see cref="Check"/>). A failing seed is printed and replays exactly with
    /// <c>ENFORCER_CHAOS_SEED</c>.
    /// <para>
    /// <c>ENFORCER_CHAOS_RUNS</c> sets how many (default 3, so it stays cheap in an ordinary test run).
    /// Run 500 before pushing a change to rate limiting, enforcement or raids (see CLAUDE.md). Needs
    /// the test database, like every harness scenario.
    /// </para>
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Chaos")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class ChaosScenarios
    {
        private sealed class GroupPlan
        {
            public HarnessChat Chat;
            public HarnessUser Admin;
            public List<HarnessUser> Regulars = new();
            public List<HarnessUser> Newcomers = new();
            public List<HarnessUser> Raiders = new();
            public HashSet<long> Spammed = new();
            public Dictionary<long, int> SpammedAt = new();
            public RaidMode Mode;
            public int RaidStart;      // step the raid starts, -1 for none
            public int RaidSize;
            public string Style;       // picture, text, link, flood
            public int PressAfter;     // human mode: steps until the admin locks
            public int? AlertSeenAt;
        }

        [TestMethod]
        public async Task SeededRaids_KeepEveryRule()
        {
            var seedSetting = Environment.GetEnvironmentVariable("ENFORCER_CHAOS_SEED");
            var runs = int.TryParse(Environment.GetEnvironmentVariable("ENFORCER_CHAOS_RUNS"), out var r) ? r : 3;
            var from = int.TryParse(Environment.GetEnvironmentVariable("ENFORCER_CHAOS_FROM"), out var f) ? f : 1000;
            var seeds = seedSetting != null
                ? new[] { int.Parse(seedSetting) }
                : Enumerable.Range(0, runs).Select(i => from + i).ToArray();
            // Every seed runs; the failures are listed together at the end.
            var failures = new List<string>();
            foreach (var seed in seeds)
            {
                try
                {
                    await RunAsync(seed);
                    Progress($"{seed} ok");
                }
                catch (AssertInconclusiveException)
                {
                    throw; // no test database: skipped, as every harness scenario is
                }
                catch (Exception e)
                {
                    failures.Add($"Chaos seed {seed} failed (replay with ENFORCER_CHAOS_SEED={seed}): {e.Message}" +
                        (e is AssertFailedException ? "" : $"\n{e.GetType().Name}\n{e.StackTrace}"));
                    Console.Error.WriteLine(failures[^1]);
                    Progress(failures[^1]);
                }
            }
            if (failures.Count > 0)
                Assert.Fail($"{failures.Count} of {seeds.Length} seeds failed:\n" + string.Join("\n", failures.Take(20)));
        }

        // ENFORCER_CHAOS_PROGRESS names a file each seed's result is added to as it finishes, to
        // follow a long run; the test's own output only appears at the end.
        private static void Progress(string line)
        {
            var path = Environment.GetEnvironmentVariable("ENFORCER_CHAOS_PROGRESS");
            if (!string.IsNullOrEmpty(path)) System.IO.File.AppendAllText(path, line + Environment.NewLine);
        }

        private static async Task RunAsync(int seed)
        {
            var rnd = new Random(seed);
            await using var first = await HarnessChat.StartAsync(chatId: -1003000000000 - seed * 10L, title: $"Chaos {seed}");
            var groups = new List<GroupPlan>();
            var groupCount = rnd.Next(1, 4);
            for (var g = 0; g < groupCount; g++)
            {
                var chat = g == 0 ? first : first.Group(first.ChatId - g, $"Chaos {seed}.{g}");
                var plan = new GroupPlan
                {
                    Chat = chat,
                    Admin = chat.Add($"Admin{g}", MemberState.Administrator),
                    Mode = rnd.Next(2) == 0 ? RaidMode.Auto : RaidMode.Human,
                    RaidStart = rnd.Next(4) == 0 ? -1 : rnd.Next(2, 20),
                    RaidSize = rnd.Next(12, 40),
                    Style = new[] { "picture", "text", "link", "flood" }[rnd.Next(4)],
                    PressAfter = rnd.Next(1, 15)
                };
                RaidGuard.SetMode(chat.ChatId, plan.Mode);
                for (var i = 0; i < rnd.Next(4, 10); i++)
                {
                    var regular = chat.Add($"Regular{g}.{i}");
                    chat.Hashes[$"{chat.ChatId}:users:{regular.Id}", "msgs"] = rnd.Next(200, 3000).ToString();
                    plan.Regulars.Add(regular);
                }
                if (plan.Style == "flood")
                {
                    chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no";
                    chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = "4";
                    chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = "kick";
                }
                groups.Add(plan);
            }

            // Faults, at random steps.
            var stormAt = rnd.Next(0, 25);
            var stormKind = rnd.Next(4); // 0 none, 1 messages, 2 moderation, 3 everything
            var stormLength = TimeSpan.FromSeconds(rnd.Next(10, 240));
            var rightsLostAt = rnd.Next(3) == 0 ? rnd.Next(0, 25) : -1;
            var rightsBackAfter = rnd.Next(2, 20);
            var crashAt = rnd.Next(3) == 0 ? rnd.Next(5, 30) : -1;
            var rightsGroup = groups[rnd.Next(groups.Count)];
            const int steps = 40; // two seconds each

            for (var step = 0; step < steps; step++)
            {
                if (step == stormAt && stormKind > 0)
                {
                    Func<TelegramCall, bool> what = stormKind switch
                    {
                        1 => c => FakeTelegram.IsMessage(c.Method),
                        2 => c => FakeTelegram.IsModeration(c.Method),
                        _ => c => FakeTelegram.IsMessage(c.Method) || FakeTelegram.IsModeration(c.Method)
                    };
                    first.Telegram.StartStorm(what, stormLength, rnd.Next(5, 120));
                }
                if (step == rightsLostAt) first.Telegram.SetBotCanRestrict(rightsGroup.Chat.ChatId, false);
                if (rightsLostAt >= 0 && step == rightsLostAt + rightsBackAfter) first.Telegram.SetBotCanRestrict(rightsGroup.Chat.ChatId, true);
                if (step == crashAt) await first.CrashAndRestartAsync();

                foreach (var plan in groups)
                {
                    var chat = plan.Chat;
                    // Regulars chatting, sometimes all the same sticker.
                    if (rnd.Next(3) == 0)
                    {
                        var who = plan.Regulars[rnd.Next(plan.Regulars.Count)];
                        chat.Deliver(rnd.Next(4) == 0 ? chat.Photo(who, uniqueId: "party-sticker") : chat.Text(who, $"chat line {rnd.Next()}"));
                    }
                    // A real newcomer now and then, saying something of their own.
                    if (rnd.Next(8) == 0)
                    {
                        var newcomer = chat.Add($"Newcomer{plan.Newcomers.Count}", MemberState.Left);
                        plan.Newcomers.Add(newcomer);
                        chat.Deliver(chat.Join(newcomer));
                        var words = new[] { "hello", "anyone", "knows", "where", "the", "rules", "are", "thanks", "joined", "from", "forum", "question", "about", "release", "date", "price" };
                        chat.Deliver(chat.Text(newcomer, string.Join(" ", Enumerable.Range(0, 7).Select(_ => words[rnd.Next(words.Length)]))));
                    }
                    // The raid.
                    if (plan.RaidStart >= 0 && step >= plan.RaidStart && plan.Raiders.Count < plan.RaidSize)
                    {
                        var batch = Math.Min(plan.RaidSize - plan.Raiders.Count, rnd.Next(2, 8));
                        for (var i = 0; i < batch; i++)
                        {
                            var raider = chat.Add($"Raider {plan.Raiders.Count}", MemberState.Left);
                            plan.Raiders.Add(raider);
                            if (rnd.Next(2) == 0) chat.Deliver(chat.MemberJoined(raider, "https://t.me/+leaked", "old"));
                            else chat.Deliver(chat.Join(raider));
                            if (rnd.Next(5) == 0) continue; // silent
                            plan.Spammed.Add(raider.Id);
                            plan.SpammedAt[raider.Id] = step;
                            switch (plan.Style)
                            {
                                case "picture": chat.Deliver(chat.Photo(raider, uniqueId: $"scam{seed}")); break;
                                case "text": chat.Deliver(chat.Text(raider, "Earn 500 dollars a day, message me now")); break;
                                case "link": chat.Deliver(chat.Text(raider, "https://t.me/+cheapfollowers")); break;
                                default:
                                    for (var m = 0; m < 6; m++) chat.Deliver(chat.Text(raider, $"buy now {m} {rnd.Next()}"));
                                    break;
                            }
                        }
                    }
                    // Human mode: the admin answers the alert after a while.
                    if (plan.Mode == RaidMode.Human)
                    {
                        var alert = chat.Telegram.SentMessages.LastOrDefault(m => m.Chat.Id == chat.ChatId && m.Text?.Contains("Possible raid") == true);
                        if (alert != null)
                        {
                            plan.AlertSeenAt ??= step;
                            if (step == plan.AlertSeenAt + plan.PressAfter) chat.DeliverPress(plan.Admin, alert, $"raid:{chat.ChatId}:lock");
                        }
                    }
                }
                await first.AdvanceAsync(TimeSpan.FromSeconds(2));
            }

            // Everything back to normal; time for the raids to end and the queue to drain.
            foreach (var plan in groups) first.Telegram.SetBotCanRestrict(plan.Chat.ChatId, true);
            await first.AdvanceAsync(TimeSpan.FromMinutes(20));
            Check(seed, first, groups, crashAt);
        }

        /// <summary>The rules every run must keep.</summary>
        private static void Check(int seed, HarnessChat first, List<GroupPlan> groups, int crashAt)
        {
            var telegram = first.Telegram;
            Assert.AreEqual(0, telegram.LimitRefusals.Count, "Telegram never had to refuse us for going over its limits");
            Assert.AreEqual(0, EnforcementQueue.Count, "no enforcement job left behind");
            var errors = first.Errors.Where(e => !e.Contains("Too Many Requests")).ToList();
            Assert.AreEqual(0, errors.Count, "errors:\n" + string.Join("\n", errors));

            foreach (var plan in groups)
            {
                var chatId = plan.Chat.ChatId;
                Assert.IsTrue(plan.Regulars.All(r => telegram.GetMember(chatId, r.Id) == MemberState.Member), "no regular was touched");
                Assert.IsTrue(plan.Newcomers.All(n => telegram.GetMember(chatId, n.Id) is MemberState.Member or MemberState.Restricted),
                    "a real newcomer may be held in quarantine, never banned");
                Assert.IsNull(RaidGuard.IncidentIn(chatId), "every raid ended once it stopped");
                Assert.IsFalse(telegram.ChatPermissions(chatId)?.CanSendMessages == false, "and the group is open again");

                // A kick is a ban lifted at once (the unban may wait out a 429 or its turn); what
                // counts is bans that stayed. Banning someone a flood kicked a moment ago is fine.
                var calls = telegram.Accepted.Where(c => c.ChatId == chatId).ToList();
                var bans = calls.Where(c => c.Method == "banChatMember" &&
                                            !calls.Any(u => u.Method == "unbanChatMember" && u.UserId == c.UserId && u.At >= c.At &&
                                                            u.At - c.At <= TimeSpan.FromMinutes(2)))
                    .GroupBy(c => c.UserId);
                // A job Telegram took just before a crash, but not yet crossed off the journal, is
                // carried out again after it (at least once, never lost): only then may a ban repeat.
                var twice = crashAt >= 0 ? new List<long?>() : bans.Where(b => b.Count() > 1).Select(b => b.Key).ToList();
                Assert.AreEqual(0, twice.Count, $"nobody banned twice ({plan.Style}, {plan.Mode}, raiders {string.Join(",", twice.Select(u => plan.Raiders.FindIndex(x => x.Id == u)))}): " + string.Join(" | ", twice.Select(u =>
                    string.Join(",", telegram.Calls.Where(c => c.ChatId == chatId && c.UserId == u)
                        .Select(c => $"{c.Method}{(c.Refused ? "(429)" : "")}{(c.Request is Telegram.Bot.Requests.BanChatMemberRequest b ? $"[revoke={b.RevokeMessages},until={b.UntilDate:HH:mm}]" : "")}@{(c.At - telegram.Calls[0].At).TotalSeconds:0}s")))));

                var raidDetected = plan.Raiders.Count >= 15 || plan.Style == "flood" && plan.Spammed.Count >= 3;
                if (!raidDetected || plan.Mode == RaidMode.Human && plan.AlertSeenAt == null) continue;
                // A raid that was caught: everyone who spammed is out or muted.
                // A restart forgets the last minute of signs: spam posted before a crash that had not
                // yet added up to a sign is not seen again. Known limit; see RaidGuard.
                var missed = plan.Raiders.Where(x => plan.Spammed.Contains(x.Id) &&
                                                     !(crashAt >= 0 && plan.SpammedAt[x.Id] <= crashAt) &&
                                                     telegram.GetMember(chatId, x.Id) is not (MemberState.Kicked or MemberState.Restricted or MemberState.Left))
                    .ToList();
                Assert.AreEqual(0, missed.Count, $"{missed.Count} of {plan.Spammed.Count} spamming raiders untouched ({plan.Style}, {plan.Mode}, " +
                    $"raid from step {plan.RaidStart}, alert seen at step {plan.AlertSeenAt}, pressed after {plan.PressAfter}): " +
                    string.Join(", ", missed.Select(m => $"#{plan.Raiders.IndexOf(m)} {telegram.GetMember(chatId, m.Id)} " +
                        string.Join("/", telegram.Calls.Where(c => c.UserId == m.Id).Select(c => c.Method + (c.Refused ? "(429)" : ""))))));
            }
        }
    }
}
