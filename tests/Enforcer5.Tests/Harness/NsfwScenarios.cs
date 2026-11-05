using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Enforcer5.Detection.Images;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The NSFW check (Daniel, 2026-11-05; <see cref="NsfwSigns"/>): images and stickers deleted and reported with a working
    /// Show button, new members' profile photos and personal channel muted and reported, each a toggle in the Detection menu.
    /// The models are a stand-in (<see cref="FakeModel"/>) that reads what the harness wrote into the file. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class NsfwScenarios
    {
        private static NsfwScore Parts(double nsfw, params (string Part, double Score)[] parts) =>
            new(nsfw, parts.ToDictionary(p => p.Part, p => p.Score));

        /// <summary>Scores a "file" by its text: "genitals" flagged with an exposed part, "nude" flagged without one, anything else safe.</summary>
        private sealed class FakeModel : INsfwModel
        {
            public string Version => "fake-1";

            public NsfwScore Score(byte[] image)
            {
                var text = Encoding.UTF8.GetString(image);
                return text.Contains("genitals") ? Parts(0.95, ("MALE_GENITALIA_EXPOSED", 0.92))
                    : text.Contains("nude") ? Parts(0.97, ("BUTTOCKS_EXPOSED", 0.2))
                    : text.Contains("lots of skin") ? Parts(0.5, ("FEMALE_BREAST_EXPOSED", 0.99)) // NudeNet alone never flags
                    : Parts(0.01, ("FEMALE_BREAST_EXPOSED", 0.05));
            }
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static async Task<HarnessChat> StartAsync(bool images = true, bool profile = true)
        {
            var chat = await HarnessChat.StartAsync();
            ImageScans.Model = new FakeModel();
            await chat.ChangeSettingsAsync(p => p with
            {
                Engine = p.Engine with
                {
                    Signs = new System.Collections.Generic.Dictionary<string, SignPolicy>(p.Engine.Signs)
                    {
                        [SignCatalog.ImageNsfw.Name] = p.Engine.For(SignCatalog.ImageNsfw.Name) with { Enabled = images },
                        [SignCatalog.ProfileNsfw.Name] = p.Engine.For(SignCatalog.ProfileNsfw.Name) with { Enabled = profile }
                    }
                }
            });
            return chat;
        }

        private static Message[] Reports(HarnessChat chat, HarnessUser admin) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true).ToArray();

        private static Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton[] Buttons(Message message) =>
            message.ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray() ?? Array.Empty<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton>();

        private static bool Deleted(HarnessChat chat, HarnessUser user, Message message) =>
            !chat.Telegram.MessagesFrom(chat.ChatId, user.Id).Contains(message.MessageId);

        private static PhotoSize File(HarnessChat chat, string name, string content, int side = 640)
        {
            chat.Files.Add("f-" + name, Encoding.UTF8.GetBytes(content));
            return new PhotoSize { FileId = "f-" + name, FileUniqueId = "u-" + name, Width = side, Height = side };
        }

        private static Message Sticker(HarnessChat chat, HarnessUser from, string name, string content)
        {
            var file = File(chat, name, content, 512);
            var message = chat.Text(from, "x");
            message.Text = null;
            message.Entities = null;
            message.Sticker = new Sticker { FileId = file.FileId, FileUniqueId = file.FileUniqueId, Width = 512, Height = 512, Emoji = "🍑" };
            return message;
        }

        private static Message Photo(HarnessChat chat, HarnessUser from, string name, string content)
        {
            var message = chat.Text(from, "x");
            message.Text = null;
            message.Entities = null;
            message.Photo = new[] { new PhotoSize { FileId = "thumb", FileUniqueId = "thumb-" + name, Width = 90, Height = 90 }, File(chat, name, content, 1280) };
            return message;
        }

        private static async Task<(long User, string Sign, string Mode, string Action, bool Deleted, bool Reported)[]> Detections(HarnessChat chat)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string, string, string, bool, bool)>(@"
SELECT a.telegram_user_id, d.sign, d.mode, d.action, d.deleted, d.reported FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE d.sign LIKE '%nsfw' ORDER BY d.detection_id")).ToArray();
        }

        [TestMethod]
        public async Task ANudeSticker_IsDeletedAndReported_AndShowSendsTheStickerToTheAdmin()
        {
            await using var chat = await StartAsync(profile: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            chat.Hashes[$"{chat.ChatId}:users:{member.Id}", "msgs"] = "5000"; // established: images apply to everyone

            var fine = await chat.PostAsync(Sticker(chat, member, "cat", "a cat"));
            var bad = await chat.PostAsync(Sticker(chat, member, "peach", "genitals"));

            Assert.IsFalse(Deleted(chat, member, fine));
            Assert.IsTrue(Deleted(chat, member, bad));
            var report = Reports(chat, admin).Single();
            StringAssert.Contains(report.Text, "NSFW image");
            StringAssert.Contains(report.Text, "95% NSFW: exposed genitals 92%");
            StringAssert.Contains(report.Text, "Deleted post");
            var show = Buttons(report).Single(b => b.CallbackData?.EndsWith(":show") == true);
            Assert.AreEqual("👁 Show deleted post", show.Text);
            await chat.PressDataAsync(admin, report, show.CallbackData);
            Assert.AreEqual(new FakeTelegram.ResentFile(admin.Id, "sendSticker", "f-peach", true), chat.Telegram.ResentFiles.Single());
            Assert.IsFalse(chat.Telegram.Accepted.Any(c => c.ChatId == chat.ChatId && c.Method == "sendSticker"), "never in the group");
            CollectionAssert.AreEqual(new[] { (member.Id, "image_nsfw", "live", "delete", true, true) }, await Detections(chat));

            // Scored once: the same sticker again is known.
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));
            var again = await chat.PostAsync(Sticker(chat, member, "peach", "genitals"));
            Assert.IsTrue(Deleted(chat, member, again));
            Assert.AreEqual(2, ImageScans.Scored);
            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM media_scores WHERE model = 'fake-1'"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task APhoto_IsScoredAtItsLargeSize_AdminsAreNotChecked_AndNothingHappensWhileTheCheckIsOff()
        {
            await using var chat = await StartAsync(images: false, profile: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            var off = await chat.PostAsync(Photo(chat, member, "a", "nude"));
            Assert.IsFalse(Deleted(chat, member, off), "off by default");
            Assert.AreEqual(0, ImageScans.Scored, "not even scored");

            await chat.ChangeSettingsAsync(p => p with
            {
                Engine = p.Engine with
                {
                    Signs = new System.Collections.Generic.Dictionary<string, SignPolicy>(p.Engine.Signs)
                        { [SignCatalog.ImageNsfw.Name] = p.Engine.For(SignCatalog.ImageNsfw.Name) with { Enabled = true } }
                }
            });
            var fromAdmin = await chat.PostAsync(Photo(chat, admin, "b", "nude"));
            var bad = await chat.PostAsync(Photo(chat, member, "c", "nude"));
            var skin = await chat.PostAsync(Photo(chat, member, "d", "lots of skin"));

            Assert.IsFalse(Deleted(chat, admin, fromAdmin));
            Assert.IsTrue(Deleted(chat, member, bad));
            Assert.IsFalse(Deleted(chat, member, skin), "NudeNet alone never flags");
            StringAssert.Contains(Reports(chat, admin).Single().Text, "97% NSFW: no exposed body parts seen");
            Assert.IsFalse(chat.Telegram.Calls.Any(c => c.Method == "getFile" || c.Text == "thumb"), "the small size is never fetched");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ANewMemberWithANudeProfilePhoto_IsMutedAndReported_WithTheImage_OnceADay()
        {
            await using var chat = await StartAsync(images: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var joiner = chat.Add("Joiner");
            chat.Telegram.SetProfilePhotos(joiner.Id,
                new[] { File(chat, "p1", "a face") }, new[] { File(chat, "p2", "genitals") }, new[] { File(chat, "p3", "a dog") });
            var plain = chat.Add("Plain");
            chat.Telegram.SetProfilePhotos(plain.Id, new[] { File(chat, "q1", "a face") });

            await chat.JoinAsync(joiner);
            await chat.JoinAsync(plain);
            await chat.SendAsync(joiner, "hello"); // the first post: already looked at today

            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, joiner.Id), "muted");
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, plain.Id));
            var report = Reports(chat, admin).Single();
            StringAssert.Contains(report.Text, "NSFW profile");
            StringAssert.Contains(report.Text, "profile photo · 95% NSFW: exposed genitals 92%");
            StringAssert.Contains(report.Text, "Image on their profile");
            var show = Buttons(report).Single(b => b.CallbackData?.EndsWith(":show") == true);
            Assert.AreEqual("👁 Show image", show.Text);
            await chat.PressDataAsync(admin, report, show.CallbackData);
            var sent = chat.Telegram.Accepted.Single(c => c.Method == "sendPhoto");
            Assert.AreEqual(admin.Id, sent.ChatId);
            var request = (Telegram.Bot.Requests.SendPhotoRequest)sent.Request;
            Assert.AreEqual("f-p2", ((InputFileId)request.Photo).Id);
            Assert.IsTrue(request.ProtectContent);

            Assert.AreEqual(1, chat.Telegram.Calls.Count(c => c.Method == "getUserProfilePhotos" && c.UserId == joiner.Id), "once a day");
            CollectionAssert.AreEqual(new[] { (joiner.Id, "profile_nsfw", "live", "mute", false, true) }, await Detections(chat));
            await using var connection = await chat.Database.OpenConnectionAsync();
            var scans = (await connection.QueryAsync<(long User, string Source, int Photos, int Scored, bool Flagged, string Media)>(@"
SELECT a.telegram_user_id, s.source, s.photos, s.scored, s.flagged, s.flagged_media FROM profile_scans s
JOIN telegram_accounts a ON a.account_id = s.account_id ORDER BY s.scan_id")).ToArray();
            CollectionAssert.AreEqual(new[] { (joiner.Id, "join", 3, 3, true, "u-p2"), (plain.Id, "join", 1, 1, false, (string)null) }, scans);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task APersonalChannelWithNudity_IsFound_AndTheGroupChoosesTheAction()
        {
            await using var chat = await StartAsync(images: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.ChangeSettingsAsync(p => p with
            {
                Engine = p.Engine with
                {
                    Signs = new System.Collections.Generic.Dictionary<string, SignPolicy>(p.Engine.Signs)
                        { [SignCatalog.ProfileNsfw.Name] = p.Engine.For(SignCatalog.ProfileNsfw.Name) with { Action = "ban" } }
                }
            });
            chat.Telegram.AddChannel(-1007770, "My videos");
            var joiner = chat.Add("Joiner");
            chat.Telegram.SetProfile(joiner.Id, null, personalChannel: -1007770);
            var post = new Message { Id = 5, Chat = new Chat { Id = -1007770, Type = Telegram.Bot.Types.Enums.ChatType.Channel } };
            post.Video = new Video { FileId = "v", FileUniqueId = "v", Thumbnail = File(chat, "vt", "nude", 320) };
            chat.Telegram.SetPersonalPosts(joiner.Id, post, new Message { Id = 4, Text = "hi" });

            await chat.JoinAsync(joiner);

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, joiner.Id), "banned");
            StringAssert.Contains(Reports(chat, admin).Single().Text, "personal channel · 97% NSFW: no exposed body parts seen");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheMenu_TurnsEachCheckOnAndOff_AndCyclesTheProfileAction()
        {
            await using var chat = await StartAsync(images: false, profile: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var lang = Methods.GetGroupLanguage(chat.ChatId).Doc;
            var menu = Commands.GenNsfwMenu(chat.ChatId, lang).InlineKeyboard.SelectMany(r => r).ToArray();
            Assert.AreEqual(4 + 1, menu.Length, "two toggles and Back; no action while profiles are off");

            var message = await chat.SendAsync(admin, "/menu"); // any message of the bot's to press on
            await chat.PressDataAsync(admin, message, $"nsfwtoggle:{chat.ChatId}:images");
            await chat.PressDataAsync(admin, message, $"nsfwtoggle:{chat.ChatId}:profile");
            await chat.PressDataAsync(admin, message, $"nsfwtoggle:{chat.ChatId}:action");

            var engine = DetectionSettings.For(chat.ChatId).Engine;
            Assert.IsTrue(engine.For(SignCatalog.ImageNsfw.Name).Enabled);
            Assert.IsTrue(engine.For(SignCatalog.ProfileNsfw.Name).Enabled);
            Assert.AreEqual("ban", engine.For(SignCatalog.ProfileNsfw.Name).Action, "mute, then ban");
            var detection = Commands.GenDetectionMenu(chat.ChatId, lang).InlineKeyboard.SelectMany(r => r);
            Assert.IsTrue(detection.Any(b => b.CallbackData == $"nsfwmenu:{chat.ChatId}"));
        }

        [TestMethod]
        public async Task NsfwScan_LooksAtTheMembersLabelledBad_AndKeepsEveryLook_WithoutActing()
        {
            await using var chat = await StartAsync(images: false, profile: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var dev = chat.Add("Dev", id: Models.Constants.Devs[0]);
            var spammer = chat.Add("Spammer");
            chat.Telegram.SetProfilePhotos(spammer.Id, new[] { File(chat, "s1", "nude") });
            var regular = chat.Add("Regular");
            chat.Telegram.SetProfilePhotos(regular.Id, new[] { File(chat, "r1", "a cat") });
            await chat.SendAsync(spammer, "buy now");
            await chat.SendAsync(regular, "hello");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/ban " + spammer.Id);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await Enforcer5.Helpers.HistoryWriter.FlushAsync();

            await chat.SendAsync(dev, "/nsfwscan 10 10");
            await chat.CompleteAsync();

            var summary = chat.Telegram.SentMessages.Last(m => m.Chat.Id == dev.Id).Text;
            StringAssert.Contains(summary, "Labelled bad: 1 flagged of 1");
            StringAssert.Contains(summary, "Never labelled: 0 flagged of");
            Assert.AreEqual(0, (await Detections(chat)).Length, "nothing done to anyone");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ADownloadThatNeverEnds_IsGivenUp_AndTheQueueMovesOn()
        {
            // 2026-11-05: one download that never ended stopped the queue, and the check in every group, until a restart.
            await using var chat = await StartAsync(profile: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var stuck = Sticker(chat, member, "stuck", "whatever");
            chat.Files.Hang("f-stuck");

            chat.Deliver(stuck);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(1));
            var bad = Sticker(chat, member, "peach", "genitals");
            chat.Deliver(bad);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(29));
            Assert.IsFalse(Deleted(chat, member, bad), "still behind the stuck one");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(31));
            await chat.CompleteAsync();

            Assert.IsTrue(Deleted(chat, member, bad), "the queue moved on");
            Assert.IsFalse(Deleted(chat, member, stuck));
            Assert.AreEqual(1, ImageScans.TimedOut);
            Assert.AreEqual(1, chat.Errors.Count(e => e.Contains("gave no answer in 60 s")), string.Join("\n", chat.Errors));
            Assert.AreEqual(1, chat.Errors.Count);
        }

        [TestMethod]
        public async Task AQueueThatStopsMoving_IsReportedOnce_AndAgainWhenItMoves()
        {
            await using var chat = await StartAsync(profile: false);
            var member = chat.Add("Member");
            ImageScans.ImageLimit = TimeSpan.FromMinutes(10);
            chat.Files.Hang("f-stuck");
            chat.Deliver(Sticker(chat, member, "stuck", "whatever"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(1)); // the stuck one first: two deliveries race otherwise
            chat.Deliver(Sticker(chat, member, "next", "a cat"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(59));
            QueueStatus.Report();
            Assert.IsFalse(chat.Errors.Any(e => e.Contains("Queue stuck")), "not yet");

            await chat.AdvanceAsync(TimeSpan.FromMinutes(2));
            QueueStatus.Report();
            QueueStatus.Report();
            var stuck = chat.Errors.Single(e => e.Contains("Queue stuck: images"));
            StringAssert.Contains(stuck, "images: 2 waiting");
            StringAssert.Contains(stuck, "on sticker u-stuck for 3 min");
            var queues = QueueStatus.Queues.Single(q => q.Name == "images").Read();
            Assert.IsTrue(queues.Stuck);

            await chat.AdvanceAsync(TimeSpan.FromMinutes(8)); // given up at 10 min
            QueueStatus.Report();
            Assert.IsTrue(chat.Errors.Any(e => e.Contains("Queue moving again: images")));
        }

        [TestMethod]
        public async Task AScan_SkipsAMemberThatTakesTooLong_RefusesASecondScan_AndAlwaysEnds()
        {
            await using var chat = await StartAsync(images: false, profile: false);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var dev = chat.Add("Dev", id: Models.Constants.Devs[0]);
            var slow = chat.Add("Slow");
            chat.Telegram.SetProfilePhotos(slow.Id, new[] { File(chat, "h1", "x") });
            chat.Files.Hang("f-h1");
            var spammer = chat.Add("Spammer");
            chat.Telegram.SetProfilePhotos(spammer.Id, new[] { File(chat, "s1", "nude") });
            await chat.SendAsync(slow, "hi");
            await chat.SendAsync(spammer, "buy now");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/ban " + slow.Id);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/ban " + spammer.Id);
            await Enforcer5.Helpers.HistoryWriter.FlushAsync();
            ImageScans.ImageLimit = TimeSpan.FromMinutes(10);
            NsfwSigns.MemberLimit = TimeSpan.FromMinutes(2);

            Assert.IsTrue(NsfwSigns.TryStartEval(chat.ChatId, dev.Id, 10, 0));
            Assert.IsFalse(NsfwSigns.TryStartEval(chat.ChatId, dev.Id, 10, 0), "one at a time");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(15));
            await chat.CompleteAsync();

            var summary = chat.Telegram.SentMessages.Last(m => m.Chat.Id == dev.Id && m.Text?.Contains("NSFW profile scan") == true).Text;
            StringAssert.Contains(summary, "Labelled bad: 1 flagged of 1 (1 with images, 1 skipped) of 2");
            Assert.IsFalse(NsfwSigns.Scanning);
            Assert.IsTrue(NsfwSigns.TryStartEval(chat.ChatId, dev.Id, 0, 0), "free again");
            await chat.CompleteAsync();
        }

        [TestMethod]
        public async Task AGroupsImage_GoesAheadOfAScan()
        {
            await using var chat = await StartAsync(profile: false);
            var model = new OrderModel(new FakeModel());
            ImageScans.Model = model;
            ImageScans.ImageLimit = TimeSpan.FromSeconds(5);
            chat.Files.Hang("f-h");
            File(chat, "b1", "a cat");
            File(chat, "b2", "a dog");
            File(chat, "g", "genitals");

            var first = ImageScans.ScoreAsync("u-h", "f-h", "profile_photo", bulk: true);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(1)); // the queue is on the stuck one
            var scan1 = ImageScans.ScoreAsync("u-b1", "f-b1", "profile_photo", bulk: true);
            var scan2 = ImageScans.ScoreAsync("u-b2", "f-b2", "profile_photo", bulk: true);
            var group = ImageScans.ScoreAsync("u-g", "f-g", "sticker");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));

            CollectionAssert.AreEqual(new[] { "genitals", "a cat", "a dog" }, model.Order);
            Assert.IsNull(await first);
            Assert.IsNotNull(await scan1);
            Assert.IsNotNull(await scan2);
            Assert.AreEqual(0.95, (await group).Nsfw);
        }

        [TestMethod]
        public async Task NsfwTest_ScoresTheRepliedImage_AndDoesNothingElse()
        {
            await using var chat = await StartAsync(images: false, profile: false);
            var dev = chat.Add("Dev", id: Models.Constants.Devs[0]);
            var member = chat.Add("Member");
            var bad = await chat.PostAsync(Sticker(chat, member, "peach", "genitals"));
            var fine = await chat.PostAsync(Photo(chat, member, "cat", "a cat"));

            await chat.SendAsync(dev, "/nsfwtest", bad);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
            await chat.SendAsync(dev, "/nsfwtest", fine);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));
            await chat.SendAsync(dev, "/nsfwtest");

            var replies = chat.Replies.Where(r => r.Contains("Flagged") || r.Contains("Passes") || r.Contains("Reply to")).ToList();
            Assert.AreEqual(3, replies.Count, string.Join("\n---\n", chat.Replies));
            StringAssert.Contains(replies[0], "🔞 <b>Flagged</b>\nViT: 95% NSFW (flags at 90%)\nNudeNet: male genitalia exposed 92% (named)");
            StringAssert.Contains(replies[0], "the sticker, <code>u-peach</code>");
            StringAssert.Contains(replies[1], "✅ <b>Passes</b>\nViT: 1% NSFW");
            StringAssert.Contains(replies[1], "<code>u-cat</code>", "the large size of the photo");
            StringAssert.Contains(replies[2], "Reply to a photo");
            Assert.IsFalse(Deleted(chat, member, bad), "nothing is done");
            Assert.AreEqual(0, (await Detections(chat)).Length);
            Assert.AreEqual(0, Reports(chat, dev).Length);
            AssertNoErrors(chat);
        }

        /// <summary>
        /// Reads a "video" as the harness writes it: "frames:a|b|c" are three frames, each scored by <see cref="FakeModel"/>;
        /// anything else is not a video. Without <see cref="ReadsVideo"/> it stands for a server without ffmpeg.
        /// </summary>
        private sealed class FakeDecoder : IMediaDecoder
        {
            private readonly IMediaDecoder _real = new MediaDecoder();

            public bool ReadsVideo { get; init; } = true;

            public Task<System.Collections.Generic.IReadOnlyList<byte[]>> FramesAsync(byte[] file, MediaHint hint, System.Threading.CancellationToken cancel)
            {
                var text = Encoding.UTF8.GetString(file);
                if (!text.StartsWith("frames:")) return _real.FramesAsync(file, hint, cancel);
                if (!ReadsVideo) return Task.FromResult<System.Collections.Generic.IReadOnlyList<byte[]>>(null);
                return Task.FromResult<System.Collections.Generic.IReadOnlyList<byte[]>>(
                    text["frames:".Length..].Split('|').Select(Encoding.UTF8.GetBytes).ToList());
            }
        }

        private static Message Video(HarnessChat chat, HarnessUser from, string name, string preview, string frames)
        {
            var message = chat.Text(from, "x");
            message.Text = null;
            message.Entities = null;
            chat.Files.Add("f-" + name, Encoding.UTF8.GetBytes("frames:" + frames));
            message.Video = new Video
            {
                FileId = "f-" + name, FileUniqueId = "u-" + name, Width = 640, Height = 360, Duration = 5, FileSize = 100_000,
                Thumbnail = preview == null ? null : File(chat, name + "-preview", preview, 320)
            };
            return message;
        }

        private static Message Document(HarnessChat chat, HarnessUser from, string name, string fileName, string mime, byte[] content)
        {
            var message = chat.Text(from, "x");
            message.Text = null;
            message.Entities = null;
            chat.Files.Add("f-" + name, content);
            message.Document = new Document { FileId = "f-" + name, FileUniqueId = "u-" + name, FileName = fileName, MimeType = mime, FileSize = content.Length };
            return message;
        }

        [TestMethod]
        public async Task AVideoWithACleanPreview_IsReadAsFrames_AndItsWorstFrameFlagsIt()
        {
            // Daniel, 2026-11-06: what moves was let through by a clean preview.
            await using var chat = await StartAsync(profile: false);
            ImageScans.Decoder = new FakeDecoder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            var fine = await chat.PostAsync(Video(chat, member, "fine", "a cat", "a cat|a dog|a cat"));
            var bad = await chat.PostAsync(Video(chat, member, "bad", "a cat", "a cat|a cat|genitals|a cat"));

            Assert.IsFalse(Deleted(chat, member, fine));
            Assert.IsTrue(Deleted(chat, member, bad), "the third frame");
            StringAssert.Contains(Reports(chat, admin).Single().Text, "95% NSFW: exposed genitals 92%");
            CollectionAssert.AreEqual(new[] { (member.Id, "image_nsfw", "live", "delete", true, true) }, await Detections(chat));
            CollectionAssert.AreEqual(new[] { "f-fine-preview", "f-fine", "f-bad-preview", "f-bad" }, chat.Files.Downloaded.ToArray(), "the preview first, then the video");
            await using var connection = await chat.Database.OpenConnectionAsync();
            var kinds = (await connection.QueryAsync<(string, string)>("SELECT media_unique_id, kind FROM media_scores ORDER BY media_unique_id")).ToArray();
            CollectionAssert.AreEqual(new[] { ("u-bad", "video"), ("u-bad-preview", "thumbnail"), ("u-fine", "video"), ("u-fine-preview", "thumbnail") }, kinds);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AVideoWhosePreviewFlags_IsNeverDownloaded_AndWithoutFfmpegTheVideoIsJudgedByItsPreview()
        {
            await using var chat = await StartAsync(profile: false);
            ImageScans.Decoder = new FakeDecoder { ReadsVideo = false };
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            var nudePreview = await chat.PostAsync(Video(chat, member, "a", "nude", "a cat"));
            var cleanPreview = await chat.PostAsync(Video(chat, member, "b", "a cat", "genitals"));

            Assert.IsTrue(Deleted(chat, member, nudePreview));
            Assert.IsFalse(Deleted(chat, member, cleanPreview), "no ffmpeg: as before, only the preview is seen");
            CollectionAssert.AreEqual(new[] { "f-a-preview", "f-b-preview" }, chat.Files.Downloaded.ToArray(), "no video downloaded");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWebmSentAsAFile_AndAnImageRenamedBin_AreRead_AZipNeverIs()
        {
            await using var chat = await StartAsync(profile: false);
            ImageScans.Decoder = new FakeDecoder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var png = new System.Collections.Generic.List<byte>(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="));
            png.AddRange(Encoding.UTF8.GetBytes("genitals")); // the fake model reads the bytes; Skia reads the image

            var webm = await chat.PostAsync(Document(chat, member, "webm", "clip.webm", "video/webm", Encoding.UTF8.GetBytes("frames:a cat|nude")));
            var bin = await chat.PostAsync(Document(chat, member, "bin", "photo.bin", "application/octet-stream", png.ToArray()));
            var zip = await chat.PostAsync(Document(chat, member, "zip", "pics.zip", "application/zip", Encoding.UTF8.GetBytes("genitals")));
            var text = await chat.PostAsync(Document(chat, member, "txt", "notes", "application/octet-stream", Encoding.UTF8.GetBytes("genitals, just a word")));

            Assert.IsTrue(Deleted(chat, member, webm));
            Assert.IsTrue(Deleted(chat, member, bin), "known by its first bytes");
            Assert.IsFalse(Deleted(chat, member, zip));
            Assert.IsFalse(Deleted(chat, member, text), "downloaded to find out, and not an image");
            Assert.IsFalse(chat.Files.Downloaded.Contains("f-zip"), "a zip is never downloaded");
            Assert.AreEqual(2, (await Detections(chat)).Length, "one report a member a day, both recorded");
            AssertNoErrors(chat);
        }

        /// <summary>Keeps the order images were scored in.</summary>
        private sealed class OrderModel : INsfwModel
        {
            private readonly INsfwModel _inner;
            internal readonly System.Collections.Generic.List<string> Order = new();

            internal OrderModel(INsfwModel inner) => _inner = inner;

            public string Version => _inner.Version;

            public NsfwScore Score(byte[] image)
            {
                lock (Order) Order.Add(Encoding.UTF8.GetString(image));
                return _inner.Score(image);
            }
        }
    }
}
