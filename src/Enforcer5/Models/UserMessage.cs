using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Enforcer5.Models
{
    class UserMessage
    {
        public DateTime Time { get; set; }
        public string Command { get; set; }
        public bool Replied { get; set; }

        public UserMessage(string command)
        {
            Time = DateTime.Now;
            Command = command;
        }

        public UserMessage(string command, DateTime date)
        {
            Time = date;
            Command = command;
        }
    }
}
