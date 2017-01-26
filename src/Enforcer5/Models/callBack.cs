using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Models
{
    class CallBacks
    {
        public string Trigger { get; set; }
        public bool GroupAdminOnly { get; set; }
        public bool GlobalAdminOnly { get; set; }
        public bool DevOnly { get; set; }
        public bool Blockable { get; set; }
        public Bot.ChatCallbackMethod Method { get; set; }
        public bool InGroupOnly { get; set; }
        public bool RequiresReply { get; set; }
        public bool CallbackQuery { get; set; }
    }
}
