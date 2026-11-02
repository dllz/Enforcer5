using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Enforcer5.Models;

namespace Enforcer5.Actions
{
    /// <summary>How a /batch command reads what follows it.</summary>
    internal enum BatchArguments
    {
        /// <summary>A reason, or nothing.</summary>
        Reason,
        /// <summary>A duration (or the group's default), then a reason.</summary>
        DurationThenReason
    }

    /// <summary>One command /batch can run: the action it becomes and how its arguments are read.</summary>
    internal sealed record BatchCommand(string Trigger, ActionKind Kind, BatchArguments Arguments);

    /// <summary>A /batch as the admin typed it, read once for every member on the list.</summary>
    internal sealed record BatchOrder(BatchCommand Command, string Reason, TimeSpan? Duration)
    {
        /// <summary>The text shown and stored for it: "tempban 2:00:00 spam ring".</summary>
        internal string Text => string.Join(" ", new[] { Command.Trigger, Duration is { } d ? Models.Duration.ToDisplay(d) : null, Reason }
            .Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>
    /// The commands /batch runs (<c>docs/action-seam.md</c>, section 9), and the reading of its list and
    /// arguments. A command that becomes an action later is batchable by adding one line here.
    /// </summary>
    internal static class BatchPlan
    {
        /// <summary>The most members one batch takes.</summary>
        internal const int MaxMembers = 5000;

        internal static readonly IReadOnlyList<BatchCommand> Commands = new[]
        {
            new BatchCommand("ban", ActionKind.Ban, BatchArguments.Reason),
            new BatchCommand("tempban", ActionKind.TempBan, BatchArguments.DurationThenReason),
            new BatchCommand("unban", ActionKind.Unban, BatchArguments.Reason),
            new BatchCommand("kick", ActionKind.Kick, BatchArguments.Reason),
            new BatchCommand("mute", ActionKind.Mute, BatchArguments.Reason),
            new BatchCommand("tempmute", ActionKind.TempMute, BatchArguments.DurationThenReason),
            new BatchCommand("unmute", ActionKind.Unmute, BatchArguments.Reason),
            new BatchCommand("warn", ActionKind.Warn, BatchArguments.Reason),
            new BatchCommand("prewarn", ActionKind.Prewarn, BatchArguments.Reason),
            new BatchCommand("removewarn", ActionKind.RemoveWarn, BatchArguments.Reason),
            new BatchCommand("resetwarns", ActionKind.ResetWarns, BatchArguments.Reason),
        };

        internal static BatchCommand Find(string trigger) =>
            Commands.FirstOrDefault(c => string.Equals(c.Trigger, trigger?.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

        internal enum ReadResult { Ok, UnknownCommand, InvalidDuration }

        /// <summary>
        /// Reads "tempban 2d spam ring" (what follows /batch). A duration-taking command without one gets
        /// <paramref name="defaultDuration"/>; text that starts with a digit must be a valid duration.
        /// </summary>
        internal static ReadResult Read(string text, TimeSpan defaultDuration, out BatchOrder order)
        {
            order = null;
            var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = words.Length > 0 ? Find(words[0]) : null;
            if (command == null) return ReadResult.UnknownCommand;
            var rest = words.Skip(1).ToList();
            TimeSpan? duration = null;
            if (command.Arguments == BatchArguments.DurationThenReason)
            {
                duration = defaultDuration;
                if (rest.Count > 0 && char.IsDigit(rest[0][0]))
                {
                    // "2 days" or "2d", as /tempban and /tempmute read theirs; two words first, or "2 hours" would be 2 minutes.
                    if (rest.Count > 1 && Duration.TryParse($"{rest[0]} {rest[1]}", out var two) == DurationParseResult.Ok)
                    {
                        duration = two;
                        rest.RemoveRange(0, 2);
                    }
                    else if (Duration.TryParse(rest[0], out var one) == DurationParseResult.Ok)
                    {
                        duration = one;
                        rest.RemoveAt(0);
                    }
                    else return ReadResult.InvalidDuration;
                }
            }
            var reason = string.Join(" ", rest);
            order = new BatchOrder(command, reason.Length > 0 ? reason : null, duration);
            return ReadResult.Ok;
        }

        // A user id: 5 to 15 digits standing on their own (not part of a word, a link or a negative chat id).
        private static readonly Regex UserId = new(@"(?<![\w@/:.-])\d{5,15}(?![\w])", RegexOptions.Compiled);

        /// <summary>Every user id in <paramref name="text"/>, in order, duplicates included.</summary>
        internal static IReadOnlyList<long> Ids(string text) =>
            string.IsNullOrEmpty(text)
                ? Array.Empty<long>()
                : UserId.Matches(text).Select(m => long.TryParse(m.Value, out var id) ? id : 0).Where(id => id > 0).ToList();
    }
}
