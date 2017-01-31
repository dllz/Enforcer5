using Enforcer5.Helpers;

namespace Enforcer5.Models
{
    class Commands
    {
        public string Trigger { get; set; }
        public bool GroupAdminOnly { get; set; }
        public bool GlobalAdminOnly { get; set; }
        public bool DevOnly { get; set; }
        public bool Blockable { get; set; }
        public Bot.ChatCommandMethod Method { get; set; }
        public bool InGroupOnly { get; set; }
        public bool RequiresReply { get; set; }
        public bool UploadAdmin { get; set; }
    }
}
