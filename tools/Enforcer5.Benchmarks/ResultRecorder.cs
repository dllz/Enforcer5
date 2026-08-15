using System.Diagnostics;
using System.Text.Json;

namespace Enforcer5.Benchmarks;

/// <summary>
/// One timed measurement. Results are appended to a JSON file so runs are comparable across
/// machines and across changes to the loader design.
/// </summary>
internal sealed record BenchmarkResult(
    string Benchmark,
    string Variant,
    long Items,
    double Seconds,
    IReadOnlyDictionary<string, string> Notes)
{
    internal double ItemsPerSecond => Seconds <= 0 ? 0 : Items / Seconds;
}

internal sealed class ResultRecorder
{
    private readonly List<BenchmarkResult> _results = new();
    private readonly string _path;

    internal ResultRecorder(string path) => _path = path;

    internal IDisposable Measure(string benchmark, string variant, Func<long> itemCount,
        IReadOnlyDictionary<string, string> notes = null) =>
        new Scope(this, benchmark, variant, itemCount, notes);

    private void Add(BenchmarkResult result)
    {
        _results.Add(result);
        Console.WriteLine(
            $"  {result.Benchmark,-22} {result.Variant,-28} {result.Items,12:N0} items  " +
            $"{result.Seconds,8:N2}s  {result.ItemsPerSecond,12:N0}/s");
    }

    internal void Flush()
    {
        var payload = new
        {
            recordedAt = DateTimeOffset.UtcNow,
            machine = Environment.MachineName,
            processorCount = Environment.ProcessorCount,
            osVersion = Environment.OSVersion.VersionString,
            results = _results.Select(r => new
            {
                r.Benchmark, r.Variant, r.Items, r.Seconds,
                itemsPerSecond = Math.Round(r.ItemsPerSecond, 1), r.Notes
            })
        };
        File.WriteAllText(_path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nResults written to {_path}");
    }

    private sealed class Scope : IDisposable
    {
        private readonly ResultRecorder _recorder;
        private readonly string _benchmark;
        private readonly string _variant;
        private readonly Func<long> _itemCount;
        private readonly IReadOnlyDictionary<string, string> _notes;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        internal Scope(ResultRecorder recorder, string benchmark, string variant,
            Func<long> itemCount, IReadOnlyDictionary<string, string> notes)
        {
            _recorder = recorder;
            _benchmark = benchmark;
            _variant = variant;
            _itemCount = itemCount;
            _notes = notes ?? new Dictionary<string, string>();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _recorder.Add(new BenchmarkResult(_benchmark, _variant, _itemCount(),
                _stopwatch.Elapsed.TotalSeconds, _notes));
        }
    }
}
