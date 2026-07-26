using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Models
{
    public class Constants
    {
        //538092996 = Suri
        //Bardia = 888143203
        public static long[] Devs = { 125311351, 1112734253 };
        public const int EnforcerDb = 0;
        public static long[] GlobalAdmins = { 1112734253, 125311351, 538092996, 159790001, 888143203};
        public static long SupportId = -1001360717102;
        public static long TranslatorsId = -1001108140050;
        public static string supportUsername = "blackwolfsupport";
        public static string supportUsernameWithAt = "@blackwolfsupport";
        public static string announcementGroup = "BlackWolfAnnouncements";
        public static int monthlyPremiumCost = 500;//in cents
        public static int threemonthlyPremiumCost = 1300;
        public static int sixmonthlyPremiumCost = 2500;
        public static string paymentCurrency = "USD";
        public static string premiumUsername = "enforcedbot";

        /// <summary>
        /// Telegram Payments provider token. Configured, never committed - the old hardcoded
        /// TEST token has been removed from source.
        /// </summary>
        public static string PaymentProviderToken => RegHelper.GetRegValue("PaymentProviderToken");
    }
}
