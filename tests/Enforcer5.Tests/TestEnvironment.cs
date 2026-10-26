using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests
{
    [TestClass]
    public static class TestEnvironment
    {
        /// <summary>
        /// Points the error log at a scratch directory. LogHelper writes under LogPath, which
        /// otherwise resolves next to the test assembly. Must run before anything touches
        /// RegHelper, which reads the environment once in its static constructor.
        /// </summary>
        [AssemblyCleanup]
        public static void Cleanup() => Storage.TestPostgres.DropMigratedAsync().GetAwaiter().GetResult();

        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            // As Program.Main does: the command code blocks pool threads on .Result, and the
            // harness runs a lot of it at once. Without this the pool adds threads slowly and a
            // scenario spends seconds waiting for one.
            System.Threading.ThreadPool.SetMinThreads(200, 200);
            var logs = Path.Combine(Path.GetTempPath(), "enforcer5-tests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("LogPath", logs);
        }

        /// <summary>
        /// The repository checkout, found by walking up from the test assembly to Enforcer5.slnx.
        /// Works locally and inside the Docker build, which copies the whole tree.
        /// </summary>
        public static string RepoRoot
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Enforcer5.slnx"))) return dir.FullName;
                }
                throw new InvalidOperationException($"Enforcer5.slnx not found above {AppContext.BaseDirectory}");
            }
        }

        public static string SourceRoot => Path.Combine(RepoRoot, "src", "Enforcer5");

        public static string EnglishXmlPath => Path.Combine(SourceRoot, "Languages", "English.xml");
    }
}
