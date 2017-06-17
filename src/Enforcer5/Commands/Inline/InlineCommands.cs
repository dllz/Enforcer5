using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.InputMessageContents;

namespace Enforcer5
{
    public partial class Queries
    {
        [Query(Trigger = "tempban", Title = "Tempban")]
        public static List<InlineQueryResultArticle> TempbInlineQueryResultArticle(User user, string[] args, XDocument lang)
        {
            var tempbanUsers = InlineMethods.GetTempbanUserDetails(user, args[1], lang);
            var result = new List<InlineQueryResultArticle>();
            foreach (var u in tempbanUsers)
            {
                result.Add(new InlineQueryResultArticle()
                {
                    Description = u.groupName,
                    Id = $"{u.unbanTime} + 1",
                    Title = $"{u.name} ({u.userId})",
                    InputMessageContent = new InputTextMessageContent()
                    {
                        DisableWebPagePreview = true,
                        MessageText = $"{u.name} ({u.userId})\n<b>{u.groupName}</b>\n<code>{u.unbanTime}</code>",
                        ParseMode = ParseMode.Html
                    }
                });
            }
            return result;
        }

        [Query(Trigger = "help", Title = "Help")]
        public static List<InlineQueryResultArticle> HelpQueryResultArticle(User user, string[] args, XDocument lang)
        {
            var tempbanUsers = InlineMethods.GetHelpArticles(args[1], lang);
            var result = new List<InlineQueryResultArticle>();
            foreach (var u in tempbanUsers)
            {
                var des =new string(u.details.Take(50).ToArray());
                result.Add(new InlineQueryResultArticle()
                {
                    Description = $"{des}...",
                    Id = u.name,
                    Title = $"{u.name}",
                    InputMessageContent = new InputTextMessageContent()
                    {
                        DisableWebPagePreview = true,
                        MessageText = $"<b>{u.name}</b>\n{u.details}",
                        ParseMode = ParseMode.Html
                    }
                });
            }
            return result;
        }
    }
}
