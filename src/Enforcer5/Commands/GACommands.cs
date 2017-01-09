using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Handlers;
using Enforcer5.Helpers;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Commands
{
    public class GACommands
    {
        [Command(Trigger = "uploadlang", GlobalAdminOnly = true)]
        public static async void UploadLang(Update update, string[] args)
        {
            try
            {
                var id = update.Message.Chat.Id;
                if (update.Message.ReplyToMessage?.Type != MessageType.DocumentMessage)
                {
                    await Bot.Send("Please reply to the file with /uploadlang", id);
                    return;
                }
                var fileid = update.Message.ReplyToMessage.Document?.FileId;
                //if (fileid != null)
                //    LanguageHelper.UploadFile(fileid, id,
                //        update.Message.ReplyToMessage.Document.FileName,
                //        update.Message.MessageId);
            }
            catch (Exception e)
            {
                await Bot.Api.SendTextMessageAsync(update.Message.Chat.Id, e.Message, parseMode: ParseMode.Default);
            }
        }

        [Command(Trigger = "validatelangs", GlobalAdminOnly = true)]
        public static async void ValidateLangs(Update update, string[] args)
        {
            //var langs = Directory.GetFiles(Bot.LanguageDirectory)
            //                                            .Select(x => XDocument.Load(x)
            //                                                        .Descendants("language")
            //                                                        .First()
            //                                                        .Attribute("name")
            //                                                        .Value
            //                                            ).ToList();
            //langs.Insert(0, "All");

            //var buttons =
            //    langs.Select(x => new[] { new InlineKeyboardButton(x, $"validate|{update.Message.Chat.Id}|{x}") }).ToArray();
            //var menu = new InlineKeyboardMarkup(buttons.ToArray());
            //Bot.Api.SendTextMessage(update.Message.Chat.Id, "Validate which language?",
            //    replyToMessageId: update.Message.MessageId, replyMarkup: menu);


            var langs = Program.LangaugeList;


            List<InlineKeyboardButton> buttons = langs.Select(x => x.Base).Distinct().OrderBy(x => x).Select(x => new InlineKeyboardButton(x, $"validate|{update.Message.From.Id}|{x}|null|base")).ToList();
            //buttons.Insert(0, new InlineKeyboardButton("All", $"validate|{update.Message.From.Id}|All|null|base"));

            var baseMenu = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons.Count - 1 == i)
                {
                    baseMenu.Add(new[] { buttons[i] });
                }
                else
                    baseMenu.Add(new[] { buttons[i], buttons[i + 1] });
                i++;
            }

            var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
            try
            {
                await Bot.Api.SendTextMessageAsync(update.Message.Chat.Id, "Validate which language?",
                    replyToMessageId: update.Message.MessageId, replyMarkup: menu);
            }
            catch (AggregateException e)
            {
                foreach (var ex in e.InnerExceptions)
                {
                    var x = ex as ApiRequestException;

                    await Bot.Send(x.Message, update.Message.Chat.Id);
                }
            }
            catch (ApiRequestException ex)
            {
                await Bot.Send(ex.Message, update.Message.Chat.Id);
            }
        }
    }
}
