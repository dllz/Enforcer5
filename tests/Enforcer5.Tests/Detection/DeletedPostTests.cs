using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Detection
{
    [TestClass]
    public class DeletedPostTests
    {
        [TestMethod]
        public void Facts_SayWhatTelegramKnowsAboutTheFile_NeverMoreThanThat()
        {
            Assert.AreEqual("0:42 · Song – Band · song.mp3 · 3.1 MB", DeletedPost.FactsOf(new Message
            {
                Audio = new Audio { FileId = "a", FileUniqueId = "a", Duration = 42, Title = "Song", Performer = "Band", FileName = "song.mp3", FileSize = 3_250_000 }
            }));
            Assert.AreEqual("1:01:05 · 900 KB", DeletedPost.FactsOf(new Message
            {
                Video = new Video { FileId = "v", FileUniqueId = "v", Duration = 3665, FileSize = 921_600 }
            }));
            Assert.AreEqual("report.pdf · 1 KB", DeletedPost.FactsOf(new Message
            {
                Document = new Document { FileId = "d", FileUniqueId = "d", FileName = "report.pdf", FileSize = 10 }
            }));
            Assert.IsNull(DeletedPost.FactsOf(new Message { Text = "hi" }));
        }

        [TestMethod]
        public void From_CutsLongText_AndKeepsTheFileToShow()
        {
            var post = DeletedPost.From(new Message { Id = 7, Text = new string('x', 800) });
            Assert.AreEqual(DeletedPost.MaxText, post.Text.Length);
            Assert.IsTrue(post.Text.EndsWith("…"));
            Assert.AreEqual("text", post.Kind);
            Assert.IsNull(post.FileId);

            var sticker = DeletedPost.From(new Message
            {
                Id = 8, Sticker = new Sticker { FileId = "file", FileUniqueId = "u", Emoji = "😀", Width = 1, Height = 1 }
            });
            Assert.AreEqual(("sticker", "file", "😀", (string)null), (sticker.Kind, sticker.FileId, sticker.Facts, sticker.Text));
        }
    }
}
