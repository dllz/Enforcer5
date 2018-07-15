using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Payments;

namespace Enforcer5
{
    public partial class Commands
    {
       // [Command(Trigger = "getPremium", DevOnly = true)]
        public static void GetPremium(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, false).Doc;
            var userId = update.Message.From.Id;
            var startMe = new Menu(1)
            {
                Buttons = new List<InlineButton>
                {
                    new InlineButton(Methods.GetLocaleString(lang, "Purchase"), url:$"https://t.me/{Constants.premiumUsername}?start=getpremium")
                }
            };
            Bot.Send(Methods.GetLocaleString(lang, "clickToGetPremium"), update, Key.CreateMarkupFromMenu(startMe));
        }
    }

    public partial class CallBacks
    {
        //[Callback(Trigger = "getpremium", InGroupOnly = false)]
        public static void getPremiumInvoice(CallbackQuery call, string[] args)
        {
            var lang = Methods.GetGroupLanguage(call.Message, false).Doc;
            var userId = call.Message.From.Id;
            var labeledPrices = new List<LabeledPrice>
            {
                new LabeledPrice()
                {
                    Amount = Constants.monthlyPremiumCost,
                    Label = "1 Month"
                },
                new LabeledPrice()
                {
                    Amount = Constants.threemonthlyPremiumCost,
                    Label = "3 Month"
                },
                new LabeledPrice()
                {
                    Amount = Constants.sixmonthlyPremiumCost,
                    Label = "6 Month"
                }
            };
            Bot.SendInvoice(userId, Methods.GetLocaleString(lang, "purchaseTitle"), Methods.GetLocaleString(lang, "purchasePremium"), $"purchase:{userId}", labeledPrices.ToArray());
        }
    }
}



