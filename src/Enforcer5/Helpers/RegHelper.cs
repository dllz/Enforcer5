using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Single configuration gateway, replacing the Windows Registry reads this bot used to do.
    /// Keys are PascalCase and identical in appsettings.json and as environment variables, so a
    /// systemd "Environment=EnforcerAPI=..." line and a JSON key are interchangeable.
    ///
    /// Environment variables take precedence over appsettings.json: production ships no JSON file,
    /// and a stray one must never silently override the unit file.
    /// </summary>
    public static class RegHelper
    {
        private static readonly IConfiguration _configuration;

        static RegHelper()
        {
            // appsettings.json may sit next to the DLL (production) or at the repo root (dev).
            var configPath = FindConfigFile(AppContext.BaseDirectory, "appsettings.json");

            var builder = new ConfigurationBuilder();
            if (configPath != null)
            {
                builder.SetBasePath(Path.GetDirectoryName(configPath));
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            }
            builder.AddEnvironmentVariables();

            _configuration = builder.Build();
        }

        private static string FindConfigFile(string startDir, string fileName)
        {
            var dir = startDir;
            for (int i = 0; i < 6; i++)
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }

        public static bool IsLinux => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>Returns the configured value, or null if it is unset or blank.</summary>
        public static string GetRegValue(string key)
        {
            var value = _configuration[key];
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>Returns the configured value, or throws if it is missing - use for required secrets.</summary>
        public static string GetRequired(string key)
        {
            return GetRegValue(key)
                   ?? throw new InvalidOperationException(
                       $"Required configuration '{key}' is not set. Set it as an environment variable or in appsettings.json.");
        }

        public static long? GetLong(string key)
        {
            return long.TryParse(GetRegValue(key), out var value) ? value : (long?)null;
        }

        /// <summary>
        /// Resolves a configured path. Relative values are taken relative to the app directory so
        /// the defaults in appsettings.template.json work without absolute paths.
        /// </summary>
        public static string GetPath(string key, string fallback)
        {
            var value = GetRegValue(key) ?? fallback;
            return Path.IsPathRooted(value)
                ? value
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, value));
        }
    }
}
