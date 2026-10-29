using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Enforcer5.Data;
using Enforcer5.Models;
using Newtonsoft.Json;

namespace Enforcer5.Recovery
{
    /// <summary>
    /// The file a dev imports with /recovery: data read back from the log channels after a data loss, per group.
    /// <code>
    /// { "batch": "redis-2026-10", "from": "2026-08-22T04:12:00Z", "to": "2026-10-26T09:26:00Z",
    ///   "groups": [ { "chat": -100…, "items": [ { "kind": "warns", "member": 123,
    ///       "changes": [ { "type": "hash", "key": "chat:-100…:warns", "field": "123", "before": "1", "after": "3" } ],
    ///       "evidence": [ { "at": "2026-08-22T17:23:00Z", "log": -100…, "post": 456, "what": "/warn …" } ] } ] } ] }
    /// </code>
    /// Only what an offer may set is accepted: each change is checked against its item's kind, its group and its
    /// member, so a file cannot reach any other key. The whole file is refused when anything in it is wrong.
    /// </summary>
    internal static class RecoveryFile
    {
        internal const int MaxGroups = 2000;
        internal const int MaxItems = 1000;
        internal const int MaxEvidence = 50;

        /// <summary>The kinds of item, and the change types and key suffixes each may set.</summary>
        internal static readonly IReadOnlyDictionary<string, (string Type, string[] Suffixes)[]> Kinds =
            new Dictionary<string, (string, string[])[]>
            {
                ["warns"] = new[] { ("hash", new[] { "warns", "totalWarns", "prewarns" }) },
                ["reportblock"] = new[] { ("set", new[] { "reportblocked" }) },
                ["inline"] = new[] { ("inline", new[] { "blockedinline" }) },
                ["stickerset"] = new[] { ("stickerset", new[] { "blockedstickersets" }) },
                ["extra"] = new[] { ("hash", new[] { "extra" }) },
                ["warnreasons"] = new[] { ("str", new[] { "warnreasons" }) },
            };

        /// <summary>The keys whose values are counts: a recovery adds the difference when they changed since.</summary>
        internal static bool IsCount(RecoveryChange change) =>
            change.Type == "hash" && Suffix(change.Key) is "warns" or "totalWarns" or "prewarns";

        internal static string Suffix(string key)
        {
            var parts = key.Split(':', 3);
            return parts.Length == 3 ? parts[2] : null;
        }

        internal sealed record Parsed(string Batch, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<NewRecoveryOffer> Offers);

        private sealed class FileJson
        {
            public string Batch { get; set; }
            public DateTimeOffset? From { get; set; }
            public DateTimeOffset? To { get; set; }
            public List<GroupJson> Groups { get; set; }
        }

        private sealed class GroupJson
        {
            public long Chat { get; set; }
            public List<ItemJson> Items { get; set; }
        }

        private sealed class ItemJson
        {
            public string Kind { get; set; }
            public long? Member { get; set; }
            public List<RecoveryChange> Changes { get; set; }
            public List<RecoveryEvidence> Evidence { get; set; }
        }

        private static readonly Regex BatchName = new("^[A-Za-z0-9._-]{1,40}$", RegexOptions.CultureInvariant);

        /// <summary>Reads and checks the file; throws <see cref="FormatException"/> saying what is wrong.</summary>
        internal static Parsed Parse(string json)
        {
            FileJson file;
            try
            {
                file = JsonConvert.DeserializeObject<FileJson>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.DateTimeOffset });
            }
            catch (JsonException e)
            {
                throw new FormatException($"Not a recovery file: {e.Message}");
            }
            if (file == null) throw new FormatException("The file is empty.");
            if (file.Batch == null || !BatchName.IsMatch(file.Batch)) throw new FormatException("batch: 1-40 letters, digits, . _ or -");
            if (file.From is not { } from || file.To is not { } to || to <= from) throw new FormatException("from and to: the period, from before to");
            if (file.Groups == null || file.Groups.Count == 0) throw new FormatException("groups: none");
            if (file.Groups.Count > MaxGroups) throw new FormatException($"groups: more than {MaxGroups}");
            if (file.Groups.Select(g => g.Chat).Distinct().Count() != file.Groups.Count) throw new FormatException("groups: a group twice");

            var offers = new List<NewRecoveryOffer>();
            foreach (var group in file.Groups)
            {
                var where = $"group {group.Chat}";
                if (group.Chat >= 0) throw new FormatException($"{where}: not a group id");
                if (group.Items == null || group.Items.Count == 0) throw new FormatException($"{where}: no items");
                if (group.Items.Count > MaxItems) throw new FormatException($"{where}: more than {MaxItems} items");
                var items = new List<RecoveryItem>();
                for (var i = 0; i < group.Items.Count; i++)
                    items.Add(Check(group.Chat, i + 1, group.Items[i], $"{where} item {i + 1}"));
                offers.Add(new NewRecoveryOffer(group.Chat, items));
            }
            return new Parsed(file.Batch, from, to, offers);
        }

        private static RecoveryItem Check(long chat, int position, ItemJson item, string where)
        {
            if (item.Kind == null || !Kinds.TryGetValue(item.Kind, out var allowed)) throw new FormatException($"{where}: unknown kind {item.Kind}");
            var perMember = item.Kind is "warns" or "reportblock";
            if (perMember != (item.Member is > 0)) throw new FormatException($"{where}: {(perMember ? "needs" : "takes no")} member");
            if (item.Changes == null || item.Changes.Count == 0) throw new FormatException($"{where}: no changes");
            if (item.Changes.Count > 10) throw new FormatException($"{where}: more than 10 changes");
            var evidence = item.Evidence ?? new List<RecoveryEvidence>();
            if (evidence.Count > MaxEvidence) throw new FormatException($"{where}: more than {MaxEvidence} posts");
            if (evidence.Any(e => e.Log >= 0 || e.Post <= 0)) throw new FormatException($"{where}: a post without its channel or id");

            foreach (var change in item.Changes)
            {
                var prefix = $"chat:{chat}:";
                var suffix = change.Key != null && change.Key.StartsWith(prefix, StringComparison.Ordinal) ? change.Key.Substring(prefix.Length) : null;
                if (suffix == null || !allowed.Any(a => a.Type == change.Type && a.Suffixes.Contains(suffix)))
                    throw new FormatException($"{where}: may not set {change.Type} {change.Key}");
                if (change.Before == change.After) throw new FormatException($"{where}: {change.Key} does not change");
                switch (change.Type)
                {
                    case "hash" when item.Kind == "warns":
                        if (change.Field != item.Member.ToString()) throw new FormatException($"{where}: {change.Key} is not the member's");
                        if (!IsCountValue(change.Before) || !IsCountValue(change.After))
                            throw new FormatException($"{where}: {change.Key} is a count");
                        break;
                    case "hash": // extra
                        if (change.Field is not { Length: > 1 } tag || tag[0] != '#') throw new FormatException($"{where}: an extra is a #tag");
                        break;
                    case "set":
                        if (change.Field != item.Member.ToString()) throw new FormatException($"{where}: {change.Key} is not the member's");
                        CheckFlag(change, where);
                        break;
                    case "inline":
                        if (InlineBotBlock.TryParse(change.Field, out _, out _) != InlineBotBlockParseResult.Ok)
                            throw new FormatException($"{where}: {change.Field} is not an inline bot block");
                        CheckFlag(change, where);
                        break;
                    case "stickerset":
                        if (string.IsNullOrWhiteSpace(change.Field)) throw new FormatException($"{where}: no sticker pack");
                        CheckFlag(change, where);
                        break;
                    case "str":
                        if (change.Field != null) throw new FormatException($"{where}: {change.Key} has no field");
                        break;
                }
            }
            return new RecoveryItem(position, item.Kind, item.Member, item.Changes, evidence, true, null, null);
        }

        private static bool IsCountValue(string value) => value == null || long.TryParse(value, out var n) && n >= 0;

        private static void CheckFlag(RecoveryChange change, string where)
        {
            if (change.Before is not ("true" or "false") || change.After is not ("true" or "false"))
                throw new FormatException($"{where}: {change.Key} is true or false");
        }
    }
}
