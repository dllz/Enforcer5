using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Enforcer5.Storage
{
    internal sealed record GroupSettingsSnapshot(
        long ChatId,
        string SettingGroup,
        string RedisKey,
        IReadOnlyDictionary<string, string> Fields,
        bool SourceExists,
        long SourceRevision,
        DateTimeOffset ObservedAt)
    {
        internal string Checksum => CalculateChecksum(Fields);

        internal static string CalculateChecksum(IReadOnlyDictionary<string, string> fields)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var pair in fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(hash, pair.Key);
                Append(hash, pair.Value);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void Append(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
    }
}
