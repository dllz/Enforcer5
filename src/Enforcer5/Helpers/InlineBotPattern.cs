using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Compiles and evaluates the admin-supplied regular expressions on inline bot blocklists.
    ///
    /// The patterns come from group admins and are evaluated on the update hot path, so they run
    /// with <see cref="RegexOptions.NonBacktracking"/>: matching is linear in the input, which
    /// rules out catastrophic backtracking whatever an admin types. The input is a bot username
    /// (at most 32 characters) and the pattern length is capped, so evaluation is cheap.
    /// </summary>
    internal static class InlineBotPattern
    {
        /// <summary>
        /// Usernames are at most 32 characters, so longer patterns buy nothing. The cap also keeps
        /// a full blocklist inside a single Telegram message when /blockedinline lists it.
        /// </summary>
        internal const int MaxLength = 64;

        private const RegexOptions Options =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

        // Belt and braces: NonBacktracking is already linear, this bounds the constant factor.
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        // Compiled patterns are shared across chats and reused for every message. Bounded so a
        // stream of distinct patterns cannot grow it forever; past the cap a pattern is compiled
        // per use instead. Counted with Interlocked because ConcurrentDictionary.Count takes
        // every bucket lock.
        private const int CacheLimit = 1024;
        private static readonly ConcurrentDictionary<string, Regex> Cache =
            new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);
        private static int _cacheCount;

        /// <summary>
        /// Checks that <paramref name="pattern"/> compiles and can be evaluated. On failure
        /// <paramref name="error"/> holds the reason, suitable for showing to the admin.
        /// </summary>
        internal static bool TryValidate(string pattern, out string error)
        {
            try
            {
                // Run it once as well: NonBacktracking builds parts of its automaton lazily, so
                // construction alone does not prove the pattern is usable.
                Compile(pattern).IsMatch("validation_probe_bot");
                error = null;
                return true;
            }
            catch (ArgumentException e) // includes RegexParseException
            {
                error = e.Message;
            }
            catch (NotSupportedException e) // backreferences, lookarounds etc. under NonBacktracking
            {
                error = e.Message;
            }
            catch (RegexMatchTimeoutException)
            {
                error = "the expression is too slow to evaluate";
            }
            return false;
        }

        /// <summary>
        /// True if <paramref name="pattern"/> matches anywhere in <paramref name="input"/>,
        /// ignoring case. Throws if the pattern is invalid or evaluation times out.
        /// </summary>
        internal static bool IsMatch(string pattern, string input)
        {
            if (!Cache.TryGetValue(pattern, out var regex))
            {
                regex = Compile(pattern);
                if (Volatile.Read(ref _cacheCount) < CacheLimit && Cache.TryAdd(pattern, regex))
                {
                    Interlocked.Increment(ref _cacheCount);
                }
            }
            return regex.IsMatch(input);
        }

        private static Regex Compile(string pattern) => new Regex(pattern, Options, MatchTimeout);
    }
}
