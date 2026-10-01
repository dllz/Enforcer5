using System;
using System.Linq;
using System.Reflection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests
{
    /// <summary>
    /// Bot.Initialize binds commands, callbacks and inline queries by reflection. A handler with
    /// the wrong signature throws there and the bot never starts receiving; one in the wrong
    /// place, or not public, is silently never bound.
    /// </summary>
    [TestClass]
    public class CommandRegistrationTests
    {
        private const BindingFlags AllMethods =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static readonly Assembly App = typeof(Commands).Assembly;

        [TestMethod]
        public void CommandHandlers_BindLikeBotInitialize()
        {
            foreach (var method in typeof(Commands).GetMethods().Where(m => m.IsDefined(typeof(Attributes.Command))))
            {
                AssertBinds(method, typeof(Bot.ChatCommandMethod));
            }
        }

        [TestMethod]
        public void CallbackHandlers_BindLikeBotInitialize()
        {
            foreach (var method in typeof(CallBacks).GetMethods().Where(m => m.IsDefined(typeof(Attributes.Callback))))
            {
                AssertBinds(method, typeof(Bot.ChatCallbackMethod));
            }
        }

        [TestMethod]
        public void InlineQueryHandlers_BindLikeBotInitialize()
        {
            foreach (var method in typeof(Queries).GetMethods().Where(m => m.IsDefined(typeof(Attributes.Query))))
            {
                AssertBinds(method, typeof(Bot.InlineQuery));
            }
        }

        [DataTestMethod]
        [DataRow(typeof(Attributes.Command), typeof(Commands))]
        [DataRow(typeof(Attributes.Callback), typeof(CallBacks))]
        [DataRow(typeof(Attributes.Query), typeof(Queries))]
        public void Attributes_AreOnlyWhereBotInitializeLooks(Type attribute, Type scanned)
        {
            var misplaced = App.GetTypes()
                .SelectMany(t => t.GetMethods(AllMethods))
                .Where(m => m.IsDefined(attribute))
                .Where(m => m.DeclaringType != scanned || !m.IsPublic)
                .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
                .ToList();
            Assert.AreEqual(0, misplaced.Count, $"Never bound: {string.Join(", ", misplaced)}");
        }

        [TestMethod]
        public void CommandTriggers_AreUnique()
        {
            // Dispatch takes the first case-insensitive match, so a duplicate is unreachable.
            AssertUnique(typeof(Commands).GetMethods().SelectMany(m => m.GetCustomAttributes<Attributes.Command>()).Select(a => a.Trigger));
        }

        [TestMethod]
        public void CallbackTriggers_AreUnique()
        {
            AssertUnique(typeof(CallBacks).GetMethods().SelectMany(m => m.GetCustomAttributes<Attributes.Callback>()).Select(a => a.Trigger));
        }

        [TestMethod]
        public void InlineBlockCommands_AreAdminOnlyAndGroupOnly()
        {
            foreach (var trigger in new[] { "blockinline", "unblockinline", "blockedinline" })
            {
                var command = typeof(Commands).GetMethods()
                    .SelectMany(m => m.GetCustomAttributes<Attributes.Command>())
                    .SingleOrDefault(a => a.Trigger == trigger);
                Assert.IsNotNull(command, trigger);
                Assert.IsTrue(command.GroupAdminOnly, $"{trigger} is admin only");
                Assert.IsTrue(command.InGroupOnly, $"{trigger} is group only");
            }
        }

        private static void AssertBinds(MethodInfo method, Type delegateType)
        {
            try
            {
                method.CreateDelegate(delegateType);
            }
            catch (ArgumentException e)
            {
                Assert.Fail($"{method.DeclaringType?.Name}.{method.Name} does not match {delegateType.Name}: {e.Message}");
            }
        }

        private static void AssertUnique(System.Collections.Generic.IEnumerable<string> triggers)
        {
            var duplicates = triggers
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Assert.AreEqual(0, duplicates.Count, $"Duplicate triggers: {string.Join(", ", duplicates)}");
        }
    }
}
