using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Attributes
{
    public class Query : Attribute
    {
        public string Trigger { get; set; }
        public string DefaultResponse { get; set; } = "";
        public string Title { get; set; }
        public string Description { get; set; } = "";
    }
}
