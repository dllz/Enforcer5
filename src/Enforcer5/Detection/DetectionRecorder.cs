using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Every detection, buffered and written to <c>detections</c> with the principal flush once a
    /// minute (after the principals, so the member and the group exist). Best effort: dropped while
    /// PostgreSQL is not ready, and the oldest go first when the buffer is full.
    /// </summary>
    internal static class DetectionRecorder
    {
        internal const int Capacity = 20_000;
        private const int BatchSize = 2_000;
        private static readonly ConcurrentQueue<DetectionRecord> Buffer = new();
        private static int _count;
        private static long _dropped;

        internal static void Add(DetectionRecord record)
        {
            if (!PostgresStorage.Ready) return;
            Buffer.Enqueue(record);
            if (System.Threading.Interlocked.Increment(ref _count) > Capacity && Buffer.TryDequeue(out _))
            {
                System.Threading.Interlocked.Decrement(ref _count);
                System.Threading.Interlocked.Increment(ref _dropped);
            }
        }

        /// <summary>Writes what is buffered. Never throws; a failed batch is reported and kept for the next flush.</summary>
        internal static async Task FlushAsync()
        {
            var dropped = System.Threading.Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) ErrorReporter.Report($"Detection buffer full; {dropped} detections dropped");
            if (!PostgresStorage.Ready) return;
            await WriteAsync();
            // The near misses after the detections (step 13).
            await NearMisses.FlushAsync();
        }

        private static async Task WriteAsync()
        {
            while (true)
            {
                var batch = new List<DetectionRecord>(BatchSize);
                while (batch.Count < BatchSize && Buffer.TryDequeue(out var record))
                {
                    System.Threading.Interlocked.Decrement(ref _count);
                    batch.Add(record);
                }
                if (batch.Count == 0) return;
                try
                {
                    await Repositories.DetectionLog.WriteDetectionsAsync(batch);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Writing {batch.Count} detections; retrying next minute");
                    foreach (var record in batch) Add(record);
                    return;
                }
            }
        }

        internal static void ResetForTests()
        {
            while (Buffer.TryDequeue(out _)) { }
            _count = 0;
            _dropped = 0;
        }
    }
}
