using System;

namespace Enforcer5.Storage
{
    internal enum StorageMode
    {
        RedisPrimaryPostgresShadow,
        PostgresPrimaryRedisMirror,
        PostgresOnly
    }

    internal static class StorageModeProvider
    {
        /// <summary>
        /// Last successful parse. A single immutable reference means a reader either sees a fully
        /// resolved pair or the previous one, never a torn combination of the two fields.
        /// </summary>
        private sealed record Resolved(string Configured, StorageMode Mode);

        private static Resolved _resolved;

        internal static StorageMode Current
        {
            get
            {
                var configured = Helpers.RegHelper.GetRegValue("StorageMode") ??
                                 nameof(StorageMode.RedisPrimaryPostgresShadow);

                // The configured value is still read live on every access, because
                // RegHelper reads the process environment live and the migration tests
                // rely on switching StorageMode mid-run. Only the parse is cached:
                // Enum.TryParse with ignoreCase is reflection-backed and allocates, and
                // this property is read on the message hot path by every store route.
                var resolved = _resolved;
                if (resolved != null && string.Equals(resolved.Configured, configured, StringComparison.Ordinal))
                    return resolved.Mode;

                if (!Enum.TryParse<StorageMode>(configured, true, out var mode))
                    throw new InvalidOperationException($"Unsupported StorageMode '{configured}'.");

                _resolved = new Resolved(configured, mode);
                return mode;
            }
        }

        internal static bool PostgresIsPrimary => Current != StorageMode.RedisPrimaryPostgresShadow;
        internal static bool RedisMirrorEnabled => Current == StorageMode.PostgresPrimaryRedisMirror;
    }
}
