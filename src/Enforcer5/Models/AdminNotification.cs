using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Models
{
    public class AdminNotification
    {
        public string hash { get; set; }
        public int chatMsgId { get; set; }
        public long chatId { get; set; }
        public long adminChatId { get; set; }
        public int adminMsgId { get; set; }
        public int reportId { get; set; }
    }
}
