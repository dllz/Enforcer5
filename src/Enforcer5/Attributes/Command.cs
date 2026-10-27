using System;

namespace Enforcer5.Attributes
{
    public class Command : Attribute
    {
        /// <summary>
        /// The string to trigger the command
        /// </summary>
        public string Trigger { get; set; }

        /// <summary>
        /// Is this command limited to bot admins only
        /// </summary>
        public bool GlobalAdminOnly { get; set; } = false;

        /// <summary>
        /// Is this command limited to group admins only
        /// </summary>
        public bool GroupAdminOnly { get; set; } = false;

        public bool RequiresReply { get; set; } = false;

        /// <summary>
        /// Developer only command
        /// </summary>
        public bool DevOnly { get; set; } = false;

        /// <summary>
        /// Marks the command as something to block (for example, in support chat)
        /// </summary>
        public bool Blockable { get; set; } = false;

        public bool InGroupOnly { get; set; } = false;

        public bool UploadAdmin { get; set; } = false;

        /// <summary>
        /// Which command menu lists it, when the flags above do not say it (see Helpers/CommandMenus):
        /// by default group admin commands go in the admin menu, group-only ones in the group menu,
        /// staff ones in the staff's own menus, and the rest in both the private and the group menu.
        /// </summary>
        public CommandMenu Menu { get; set; } = CommandMenu.Auto;

        /// <summary>English description for the staff menus (dev, global admin); the public menus take theirs from the language files.</summary>
        public string Description { get; set; }
    }

    /// <summary>Where a command is listed in the menus Telegram shows; see <see cref="Command.Menu"/>.</summary>
    public enum CommandMenu
    {
        Auto,
        /// <summary>In no menu: shorthands, duplicates and tools.</summary>
        Hidden,
        /// <summary>Only in private chats with the bot.</summary>
        Private,
        /// <summary>Only in groups.</summary>
        Groups,
        /// <summary>In the group admins' menu.</summary>
        GroupAdmins,
        /// <summary>In the devs' own menu.</summary>
        Devs,
    }
}
