using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Models
{
    public class Constants
    {
        public static long[] Devs = {125311351, 295152997, 129046388};
        public const int EnforcerDb = 0;
        public static long[] GlobalAdmins = {125311351};
        private const string aPIKey = "";
        public static long SupportId = -1001060486754;
        public static long TranslatorsId = -1001108140050;

        public static string APIKey
        {
            get
            {
                return aPIKey;
            }
        }
    }
}
