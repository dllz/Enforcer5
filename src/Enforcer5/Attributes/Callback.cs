using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Attributes
{
    public class Callback : Attribute
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

        public bool CallbackQuery { get; set; } = false;

    }
   
}
