using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Enforcer5.Tests")]
// The migration benchmarks build real schemas with SchemaMigrationRunner so they measure the
// shipped schema rather than a hand-written copy of it. Not shipped; see tools/Enforcer5.Benchmarks.
[assembly: InternalsVisibleTo("Enforcer.Benchmarks")]
