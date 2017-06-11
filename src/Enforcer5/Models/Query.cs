using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Models
{
    class Queries
    {
        public string Trigger { get; set; }
        public string DefaultResponse { get; set; } = "";
        public string Title { get; set; }
        public string Description { get; set; } = "";
        public Bot.InlineQuery Method { get; set; }
    }
}
