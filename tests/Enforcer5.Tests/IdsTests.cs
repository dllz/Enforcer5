using System.Collections.Generic;
using System.Linq;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests
{
    [TestClass]
    public class IdsTests
    {
        private static readonly string Id = Ids.Mark(-1001030085238);

        [TestMethod]
        public void Html_PutsAMarkedIdInCode()
        {
            Assert.AreEqual("<b>Ann</b> (<code>-1001030085238</code>)", Ids.ToHtml($"<b>Ann</b> ({Id})"));
        }

        [TestMethod]
        public void Html_LeavesItPlain_InALinkCodeOrPreOrATag()
        {
            Assert.AreEqual("<a href=\"x\">-1001030085238</a> <code>-1001030085238</code>",
                Ids.ToHtml($"<a href=\"x\">{Id}</a> {Id}"));
            Assert.AreEqual("<code>a -1001030085238</code>", Ids.ToHtml($"<code>a {Id}</code>"));
            Assert.AreEqual("<pre>-1001030085238</pre>", Ids.ToHtml($"<pre>{Id}</pre>"));
            Assert.AreEqual("<a href=\"tg://user?id=-1001030085238\">x</a>", Ids.ToHtml($"<a href=\"tg://user?id={Id}\">x</a>"));
        }

        [TestMethod]
        public void Html_DropsAHalfMark()
        {
            Assert.AreEqual("12 <code>34</code>", Ids.ToHtml($"{Ids.Open}12 {Ids.Open}34{Ids.Close}"));
            Assert.AreEqual("12", Ids.ToHtml($"12{Ids.Close}"));
            Assert.AreEqual("12<b>x</b>", Ids.ToHtml($"{Ids.Open}12<b>x</b>"));
        }

        [TestMethod]
        public void Html_StaysUnderTelegramsLimitOfFormattedParts()
        {
            var text = "<b>x</b> " + string.Join(" ", Enumerable.Range(1, 150).Select(i => Ids.Mark(i)));
            var html = Ids.ToHtml(text);
            Assert.AreEqual(Ids.MaxEntities - 1, html.Split("<code>").Length - 1);
            Assert.IsTrue(html.EndsWith(" 150"));
        }

        [TestMethod]
        public void Strip_LeavesThePlainId()
        {
            Assert.AreEqual("Ann (5)", Ids.Strip($"Ann ({Ids.Mark(5)})"));
            Assert.AreEqual("Ann", Ids.Strip("Ann"));
        }

        [TestMethod]
        public void MarksSurviveTheEscapingOfLanguageArguments()
        {
            Assert.AreEqual($"A&amp;B ({Ids.Mark(5)})", $"A&B ({Ids.Mark(5)})".FormatHTML());
        }

        [TestMethod]
        public void Request_Html_IsCode_Plain_IsStripped()
        {
            var html = new SendMessageRequest { ChatId = 1, Text = $"x {Id}", ParseMode = ParseMode.Html };
            var plain = new SendMessageRequest { ChatId = 1, Text = $"x {Id}" };
            Ids.Apply(html);
            Ids.Apply(plain);
            Assert.AreEqual("x <code>-1001030085238</code>", html.Text);
            Assert.AreEqual("x -1001030085238", plain.Text);
        }

        [TestMethod]
        public void Request_AlertsCaptionsButtonsAndInlineResults()
        {
            var alert = new AnswerCallbackQueryRequest { CallbackQueryId = "1", Text = $"by {Id}" };
            Ids.Apply(alert);
            Assert.AreEqual("by -1001030085238", alert.Text);

            var photo = new SendPhotoRequest { ChatId = 1, Photo = InputFile.FromFileId("f"), Caption = Id, ParseMode = ParseMode.Html,
                ReplyMarkup = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData($"Ban {Id}", "d")) };
            Ids.Apply(photo);
            Assert.AreEqual("<code>-1001030085238</code>", photo.Caption);
            Assert.AreEqual("Ban -1001030085238", ((InlineKeyboardMarkup)photo.ReplyMarkup).InlineKeyboard.First().First().Text);

            var inline = new AnswerInlineQueryRequest
            {
                InlineQueryId = "1",
                Results = new List<InlineQueryResult>
                {
                    new InlineQueryResultArticle("1", $"Ann ({Id})",
                        new InputTextMessageContent($"Ann ({Id})") { ParseMode = ParseMode.Html })
                }
            };
            Ids.Apply(inline);
            var article = (InlineQueryResultArticle)inline.Results.First();
            Assert.AreEqual("Ann (-1001030085238)", article.Title);
            Assert.AreEqual("Ann (<code>-1001030085238</code>)", ((InputTextMessageContent)article.InputMessageContent).MessageText);
        }

        [TestMethod]
        public void Request_EditText()
        {
            var edit = new EditMessageTextRequest { ChatId = 1, MessageId = 2, Text = Id, ParseMode = ParseMode.Html };
            Ids.Apply(edit);
            Assert.AreEqual("<code>-1001030085238</code>", edit.Text);
        }
    }
}
