using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Models
{
    public class TempbanUser
    {
        public string userId { get; set; } = "";
        public string name { get; set; } = "";
        public string groupName { get; set; } = "";
        public string unbanTime { get; set; }        
    }
}
