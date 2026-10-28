using System;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        /// <summary>
        /// The filters menu (Detection step 5): each preset with what it does, the two choices for all
        /// filters, and how many own rules the group has (they are managed with /filter).
        /// </summary>
        internal static InlineKeyboardMarkup GenFiltersMenu(long chatId, XDocument lang)
        {
            var filters = DetectionSettings.For(chatId).Filters;
            var presets = new Menu(2);
            foreach (var preset in FilterPresets.All)
            {
                presets.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"filterPreset_{preset.Name}"), $"filterpreset:{chatId}:{preset.Name}"));
                presets.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"filterAction_{filters.PresetAction(preset.Name) ?? "off"}"),
                    $"filterpreset:{chatId}:{preset.Name}"));
            }
            presets.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "filterEstablishedButton"), $"filtertoggle:{chatId}:established"));
            presets.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, filters.IncludeEstablished ? "on" : "off"), $"filtertoggle:{chatId}:established"));
            presets.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "filterGroupBotsButton"), $"filtertoggle:{chatId}:bots"));
            presets.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, filters.ExemptGroupBots ? "on" : "off"), $"filtertoggle:{chatId}:bots"));
            var back = new Menu(1);
            back.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"openGroupMenu:{chatId}"));
            return Key.CreateMarkupFromMenus(presets, back);
        }
    }

    public static partial class CallBacks
    {
        /// <summary>A preset's actions in menu order; one press moves to the next, after ban it is off again.</summary>
        internal static readonly string[] PresetActions = { null, "delete", "warn", "mute", "kick", "tempban", "ban" };

        [Callback(Trigger = "filtersmenu", GroupAdminOnly = true)]
        public static void FiltersMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var own = DetectionSettings.For(chatId).Filters.OwnRules.Count();
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Methods.GetLocaleString(lang, "filtersMenu", own),
                replyMarkup: Commands.GenFiltersMenu(chatId, lang));
        }

        [Callback(Trigger = "filterpreset", GroupAdminOnly = true)]
        public static void FilterPreset(CallbackQuery call, string[] args)
        {
            if (args.Length < 3 || FilterPresets.Get(args[2]) == null) return;
            var chatId = long.Parse(args[1]);
            var preset = args[2];
            ChangeDetection(call, chatId, Methods.GetGroupLanguage(chatId).Doc,
                p => p with { Filters = p.Filters.WithPreset(preset, Next(p.Filters.PresetAction(preset), PresetActions)) },
                Commands.GenFiltersMenu);
        }

        [Callback(Trigger = "filtertoggle", GroupAdminOnly = true)]
        public static void FilterToggle(CallbackQuery call, string[] args)
        {
            if (args.Length < 3) return;
            var chatId = long.Parse(args[1]);
            Func<DetectionPolicy, DetectionPolicy> change = args[2] switch
            {
                "established" => p => p with { Filters = p.Filters with { IncludeEstablished = !p.Filters.IncludeEstablished } },
                "bots" => p => p with { Filters = p.Filters with { ExemptGroupBots = !p.Filters.ExemptGroupBots } },
                _ => null
            };
            if (change == null) return;
            ChangeDetection(call, chatId, Methods.GetGroupLanguage(chatId).Doc, change, Commands.GenFiltersMenu);
        }
    }
}
