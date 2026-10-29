using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The chats linked as a group's admin chat (Reports menu → Link admin chat). The bot is there to talk to the
    /// admins (Detection reports, recovery offers, extras), not to moderate (Daniel, 2026-10-29): in an admin chat
    /// nothing is enforced, counted or detected, and the premium edition stays in it when one of its groups is a
    /// premium group. Read from the groups' settings in PostgreSQL at startup and every <see cref="ReloadEvery"/>
    /// (the other edition's links), and changed at once when this process links or unlinks one. Every lookup is
    /// in memory, so it costs nothing per message; without PostgreSQL there are none.
    /// </summary>
    internal static class AdminChats
    {
        internal static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(5);

        // Admin chat → the groups that linked it (one chat can serve several groups).
        private static volatile Dictionary<long, long[]> _byChat = new();
        private static readonly object Lock = new();

        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await ReloadAsync();
                    await Clock.WaitAsync(ReloadEvery, ct);
                }
            }, stop, "AdminChats");

        /// <summary>Reads them all again; keeps what memory has when PostgreSQL does not answer.</summary>
        internal static async Task ReloadAsync()
        {
            if (!PostgresStorage.Ready) return;
            try
            {
                var all = await Repositories.AdminChats.AllAsync();
                var byChat = all.GroupBy(l => l.AdminChat).ToDictionary(g => g.Key, g => g.Select(l => l.Group).Distinct().ToArray());
                lock (Lock) _byChat = byChat;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Reading the linked admin chats");
            }
        }

        /// <summary>Whether <paramref name="chatId"/> is some group's admin chat.</summary>
        internal static bool IsAdminChat(long chatId) => chatId < 0 && _byChat.ContainsKey(chatId);

        /// <summary>The groups whose admin chat <paramref name="chatId"/> is; empty for any other chat.</summary>
        internal static IReadOnlyList<long> GroupsOf(long chatId) =>
            _byChat.TryGetValue(chatId, out var groups) ? groups : Array.Empty<long>();

        /// <summary><paramref name="group"/> linked <paramref name="adminChat"/> (instead of any chat before).</summary>
        internal static void Linked(long group, long adminChat)
        {
            lock (Lock)
            {
                var next = Without(group);
                next[adminChat] = next.TryGetValue(adminChat, out var groups) ? groups.Append(group).ToArray() : new[] { group };
                _byChat = next;
            }
        }

        /// <summary><paramref name="group"/> unlinked its admin chat.</summary>
        internal static void Unlinked(long group)
        {
            lock (Lock) _byChat = Without(group);
        }

        private static Dictionary<long, long[]> Without(long group) =>
            _byChat.Select(e => (e.Key, Groups: e.Value.Where(g => g != group).ToArray()))
                .Where(e => e.Groups.Length > 0).ToDictionary(e => e.Key, e => e.Groups);

        internal static void ResetForTests()
        {
            lock (Lock) _byChat = new Dictionary<long, long[]>();
        }
    }
}
