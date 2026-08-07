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
        internal static StorageMode Current
        {
            get
            {
                var configured = Helpers.RegHelper.GetRegValue("StorageMode") ??
                                 nameof(StorageMode.RedisPrimaryPostgresShadow);
                return Enum.TryParse<StorageMode>(configured, true, out var mode)
                    ? mode
                    : throw new InvalidOperationException($"Unsupported StorageMode '{configured}'.");
            }
        }

        internal static bool PostgresIsPrimary => Current != StorageMode.RedisPrimaryPostgresShadow;
        internal static bool RedisMirrorEnabled => Current == StorageMode.PostgresPrimaryRedisMirror;
    }
}
