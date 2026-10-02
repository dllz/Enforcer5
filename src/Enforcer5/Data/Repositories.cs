using Enforcer5.Helpers;

namespace Enforcer5.Data
{
    /// <summary>
    /// The one place that decides which store backs each repository. Commands are bound by
    /// reflection as static methods, so there is no constructor to inject through; they and the
    /// update path take their repositories from here instead. Moving a repository to another
    /// database means changing the line below and nothing else. Settable so a test can substitute
    /// a fake.
    /// </summary>
    internal static class Repositories
    {
        // The lambda defers touching Redis.db until first use, after Redis.Start() has connected.
        internal static IInlineBotBlockRepository InlineBotBlocks { get; set; } =
            new RedisInlineBotBlockRepository(() => Redis.db);

        internal static IChannelPostRepository ChannelPosts { get; set; } =
            new RedisChannelPostRepository(() => Redis.db);

        internal static IMuteRepository Mutes { get; set; } =
            new RedisMuteRepository(() => Redis.db);

        internal static ITempbanRepository Tempbans { get; set; } =
#if PREMIUM
            new RedisTempbanRepository(() => Redis.db, "tempbannedPremium");
#else
            new RedisTempbanRepository(() => Redis.db, "tempbanned");
#endif
    }
}
