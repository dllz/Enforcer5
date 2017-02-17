using System.Collections.Generic;
using System;
namespace Enforcer5.Models
{
    public class InlineButton
    {
        /// <summary>
        /// The button text
        /// </summary>
        public string Text { get; set; }
        /// <summary>
        /// What trigger to associate this button with.  Make sure you create a CallbackCommand with the trigger set
        /// </summary>
        public string Trigger { get; set; }
        /// Have this button link to a chat or website. (Optional)
        /// </summary>
        public string Url { get; set; }

        public InlineButton(string text, string trigger = "", string url = "")
        {
            Url = url;
            Text = text;
            Trigger = trigger;
        }
    }

    public class Menu
    {
        /// <summary>
        /// The buttons you want in your menu
        /// </summary>
        public List<InlineButton> Buttons { get; set; }
        /// <summary>
        /// How many columns.  Defaults to 1.
        /// </summary>
        public int Columns { get; set; }

        public Menu(int col = 1, List<InlineButton> buttons = null)
        {
            Buttons = buttons ?? new List<InlineButton>();
            Columns = Math.Max(col, 1);
        }
    }
}