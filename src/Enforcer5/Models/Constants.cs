using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Models
{
    public class Constants
    {
        public static int[] Devs = {125311351};
        public const int EnforcerDb = 0;
        public static int[] GlobalAdmins = {125311351};
        public static int[] TranslaterAdmins = {9375804, 106665913};
        private const string aPIKey = "279558316:AAGYmfdcT4pWqDJmDlpSz0hm-76lg6hp3Ak";

        public static string APIKey
        {
            get
            {
                return aPIKey;
            }
        }
    }
}
