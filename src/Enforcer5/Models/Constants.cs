using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Models
{
    public class Constants
    {
        public static long[] Devs = {125311351};
        public const int EnforcerDb = 0;
        public static long[] GlobalAdmins = {125311351};
        private const string aPIKey = "";
        public static long SupportId = -1001360717102;
        public static long TranslatorsId = -1001108140050;
        public static string supportUsername = "blackwolfsupport";
        public static string supportUsernameWithAt = "@blackwolfsupport";
        public static string announcementGroup = "BlackWolfAnnouncements";

        public static string APIKey
        {
            get
            {
                return aPIKey;
            }
        }
    }
}
