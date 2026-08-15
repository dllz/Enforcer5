using System.Collections.Generic;

namespace Enforcer5.Tests;

/// <summary>
/// Serialises tests that read or write process-global configuration.
/// <para>
/// Production code reads configuration live through <c>RegHelper</c>, and several static entry
/// points branch on it at call time - <c>GroupSettingsStore.SetAsync</c> checks
/// <c>StorageModeProvider.PostgresIsPrimary</c> on every call, for example. A test that repoints
/// <c>StorageMode</c> or <c>PostgresConnection</c> at a temporary schema therefore changes the
/// behaviour of any other test observing those values at that moment, which showed up as three to
/// four unrelated failures per suite run.
/// </para>
/// <para>
/// The lock is a plain static rather than an xUnit collection because the assembly-level
/// <c>CollectionBehavior</c> and a <c>xunit.runner.json</c> both failed to prevent the
/// interference. A static lock does not depend on the runner honouring its configuration.
/// </para>
/// </summary>
internal sealed class GlobalConfigScope : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Dictionary<string, string> _original = new(StringComparer.Ordinal);
    private bool _disposed;

    private GlobalConfigScope()
    {
    }

    /// <summary>
    /// Acquires exclusive access to process-global configuration. Every variable named here is
    /// snapshotted on entry and restored on dispose, whether or not the test changes it.
    /// </summary>
    internal static GlobalConfigScope Acquire(params string[] variables)
    {
        Gate.Wait();
        var scope = new GlobalConfigScope();
        try
        {
            foreach (var variable in variables)
                scope._original[variable] = Environment.GetEnvironmentVariable(variable);
        }
        catch
        {
            Gate.Release();
            throw;
        }
        return scope;
    }

    /// <summary>Sets a variable for the duration of the scope.</summary>
    internal void Set(string variable, string value)
    {
        if (!_original.ContainsKey(variable))
            throw new InvalidOperationException(
                $"'{variable}' was not captured by this scope. Pass it to Acquire so it is restored.");
        Environment.SetEnvironmentVariable(variable, value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            foreach (var (variable, value) in _original)
                Environment.SetEnvironmentVariable(variable, value);
        }
        finally
        {
            Gate.Release();
        }
    }
}
