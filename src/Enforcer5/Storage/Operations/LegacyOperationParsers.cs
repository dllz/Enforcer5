using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Operations
{
    internal static class LegacyOperationParsers
    {
        private static readonly string[] LegacyDateFormats =
        {
            "h:mm:ss dd-MM-yyyy", "hh:mm:ss dd-MM-yyyy",
            "M/d/yyyy h:mm:ss tt", "M/d/yyyy h:mm:ss", "MM/dd/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"
        };

        internal static LegacyParseResult<LegacyTempBan> ParseTempBan(string expiryField,
            string value, string edition)
        {
            if (!long.TryParse(expiryField, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return Malformed<LegacyTempBan>("tempban expiry", expiryField);
            DateTimeOffset expiresAt;
            try { expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException e) { return LegacyParseResult<LegacyTempBan>.Malformed(e); }

            var parts = (value ?? string.Empty).Split(':', 4);
            if (parts.Length < 2 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupId) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
                return Malformed<LegacyTempBan>("tempban value", value);

            return LegacyParseResult<LegacyTempBan>.Success(new LegacyTempBan(groupId, userId,
                parts.Length > 2 ? parts[2] : null, parts.Length > 3 ? parts[3] : null,
                expiresAt, edition, $"{expiryField}:{value}"));
        }

        internal static LegacyParseResult<GlobalSanction> ParseGlobalBan(long userId,
            IReadOnlyDictionary<string, string> fields, string legacyKey)
        {
            if (!fields.TryGetValue("banned", out var banned) || (banned != "0" && banned != "1"))
                return Malformed<GlobalSanction>("global-ban field 'banned'", banned);

            var created = ParseOptionalTimestamp(fields, "time");
            if (created.Status == LegacyParseStatus.Malformed) return LegacyParseResult<GlobalSanction>.Malformed(created.Error);
            var revoked = ParseOptionalTimestamp(fields, "unbanTime");
            if (revoked.Status == LegacyParseStatus.Malformed) return LegacyParseResult<GlobalSanction>.Malformed(revoked.Error);

            var value = new GlobalSanction(userId, banned == "1", Get(fields, "motivation"),
                created.Value?.Instant, Get(fields, "unbanMotivation"), revoked.Value?.Instant, legacyKey);
            if (created.Status == LegacyParseStatus.Ambiguous || revoked.Status == LegacyParseStatus.Ambiguous)
                return LegacyParseResult<GlobalSanction>.Ambiguous(value,
                    new FormatException($"{legacyKey} contains a timezone-ambiguous timestamp."));
            return LegacyParseResult<GlobalSanction>.Success(value);
        }

        internal static LegacyParseResult<Report> ParseReport(long groupId, long messageId,
            IReadOnlyDictionary<string, string> fields, string legacyKey)
        {
            if (!fields.TryGetValue("Solved", out var solved) || (solved != "0" && solved != "1"))
                return Malformed<Report>("report field 'Solved'", solved);
            var created = ParseOptionalTimestamp(fields, "Created");
            var solvedAt = ParseOptionalTimestamp(fields, "SolvedAt");
            if (created.Status == LegacyParseStatus.Malformed) return LegacyParseResult<Report>.Malformed(created.Error);
            if (solvedAt.Status == LegacyParseStatus.Malformed) return LegacyParseResult<Report>.Malformed(solvedAt.Error);
            if (solved == "1" && (string.IsNullOrWhiteSpace(Get(fields, "solvedBy")) ||
                                  !fields.ContainsKey("SolvedAt")))
                return Malformed<Report>("solved report metadata", legacyKey);

            var value = new Report(0, groupId, messageId, ParseOptionalInt64(fields, "ReporterUserId"),
                Get(fields, "Reporter"), ParseOptionalInt64(fields, "ReportedUserId"),
                ParseOptionalInt64(fields, "ReportedMessageId"),
                solved == "1" ? "solved" : "open", created.Value?.Instant, null,
                Get(fields, "solvedBy"), solvedAt.Value?.Instant, legacyKey);
            if (created.Status == LegacyParseStatus.Ambiguous || solvedAt.Status == LegacyParseStatus.Ambiguous)
                return LegacyParseResult<Report>.Ambiguous(value,
                    new FormatException($"{legacyKey} contains a timezone-ambiguous timestamp."));
            return LegacyParseResult<Report>.Success(value);
        }

        internal static LegacyParseResult<ReportDelivery> ParseReportDelivery(long reportId,
            int index, string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                return LegacyParseResult<ReportDelivery>.Success(new ReportDelivery(reportId, index,
                    RequiredInt64(root, "adminChatId"), checked((int)RequiredInt64(root, "adminMsgId")),
                    root.TryGetProperty("chatMsgId", out var forwarded) ? forwarded.GetInt32() : null, json));
            }
            catch (Exception e) when (e is JsonException or FormatException or OverflowException)
            {
                return LegacyParseResult<ReportDelivery>.Malformed(e);
            }
        }

        internal static LegacyParseResult<LegacyTimestamp> ParseTimestamp(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Malformed<LegacyTimestamp>("timestamp", value);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var offset) &&
                HasExplicitOffset(value))
                return LegacyParseResult<LegacyTimestamp>.Success(new LegacyTimestamp(offset.UtcDateTime, offset));

            if (DateTime.TryParseExact(value, LegacyDateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var wallClock) ||
                DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out wallClock))
            {
                wallClock = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
                var instant = InterpretLegacyLocalTime(wallClock);
                return LegacyParseResult<LegacyTimestamp>.Success(new LegacyTimestamp(wallClock, instant));
            }
            return Malformed<LegacyTimestamp>("timestamp", value);
        }

        internal static LegacyParseResult<DateTimeOffset> ParseTicks(string value)
        {
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return Malformed<DateTimeOffset>("DateTime ticks", value);
            var wallClock = new DateTime(ticks, DateTimeKind.Unspecified);
            return LegacyParseResult<DateTimeOffset>.Success(InterpretLegacyLocalTime(wallClock));
        }

        private static DateTimeOffset InterpretLegacyLocalTime(DateTime wallClock)
        {
            var zoneId = RegHelper.GetRegValue("DisplayTimeZone") ?? "Europe/Amsterdam";
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
            catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
            catch (InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
            var offset = zone.GetUtcOffset(wallClock);
            try { return new DateTimeOffset(wallClock, offset).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException)
            {
                // DateTime.MinValue/MaxValue are legacy sentinel-like values; applying a real
                // timezone offset can underflow or overflow the representable UTC range.
                return new DateTimeOffset(wallClock, TimeSpan.Zero);
            }
        }

        private static LegacyParseResult<LegacyTimestamp> ParseOptionalTimestamp(
            IReadOnlyDictionary<string, string> fields, string name) =>
            !fields.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)
                ? LegacyParseResult<LegacyTimestamp>.Success(null)
                : ParseTimestamp(value);

        private static bool HasExplicitOffset(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) return true;
            if (trimmed.Length >= 6)
            {
                var suffix = trimmed.AsSpan(trimmed.Length - 6);
                if ((suffix[0] == '+' || suffix[0] == '-') && suffix[3] == ':' &&
                    char.IsDigit(suffix[1]) && char.IsDigit(suffix[2]) &&
                    char.IsDigit(suffix[4]) && char.IsDigit(suffix[5]) &&
                    ValidOffset(suffix[1], suffix[2], suffix[4], suffix[5])) return true;
            }
            if (trimmed.Length >= 5)
            {
                var suffix = trimmed.AsSpan(trimmed.Length - 5);
                if ((suffix[0] == '+' || suffix[0] == '-') &&
                    char.IsDigit(suffix[1]) && char.IsDigit(suffix[2]) &&
                    char.IsDigit(suffix[3]) && char.IsDigit(suffix[4]) &&
                    ValidOffset(suffix[1], suffix[2], suffix[3], suffix[4])) return true;
            }
            return false;
        }

        private static bool ValidOffset(char hourTens, char hourOnes, char minuteTens, char minuteOnes)
        {
            var hours = (hourTens - '0') * 10 + hourOnes - '0';
            var minutes = (minuteTens - '0') * 10 + minuteOnes - '0';
            return hours <= 14 && minutes <= 59 && (hours < 14 || minutes == 0);
        }

        private static long RequiredInt64(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var property) || !property.TryGetInt64(out var value))
                throw new FormatException($"Report delivery is missing numeric '{name}'.");
            return value;
        }

        private static string Get(IReadOnlyDictionary<string, string> fields, string name) =>
            fields.TryGetValue(name, out var value) ? value : null;

        private static long? ParseOptionalInt64(IReadOnlyDictionary<string, string> fields, string name) =>
            fields.TryGetValue(name, out var value) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed != 0
                ? parsed : null;

        private static LegacyParseResult<T> Malformed<T>(string name, string value) =>
            LegacyParseResult<T>.Malformed(new FormatException($"Malformed legacy {name}: '{value}'."));
    }
}
