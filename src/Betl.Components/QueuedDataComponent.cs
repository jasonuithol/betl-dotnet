using System.Buffers;
using System.Collections.Concurrent;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Wraps any <see cref="IDataComponent"/> with a producer thread + bounded
/// queue so the upstream computes its next batch of rows while the downstream
/// is busy with the current one.
///
/// Rows cross the producer/consumer boundary in <c>Row[]</c> batches (default
/// 256 rows per batch, 4 batches in flight) — passing rows one at a time
/// through a BlockingCollection costs roughly one lock pair per row and
/// dominates a 4-stage pipeline's runtime for sub-µs per-row work. Batching
/// amortizes the lock cost across batch_size rows.
///
/// The Row[] buffers themselves come from <see cref="ArrayPool{T}"/> so a
/// steady-state pipeline stops allocating buffers. Pool Rent may return an
/// array larger than batch_size, so we carry the live Count alongside the
/// buffer rather than trusting Array.Length. clearArray:true on Return is
/// mandatory: Row is a reference type, and an un-cleared buffer would pin
/// already-consumed rows in memory for the life of the pool slot.
///
/// Env vars (read once at process start):
///   BETL_PARALLEL=off          — disable wrapping entirely (default: on)
///   BETL_PARALLEL_BATCH=N      — rows per batch (default: 256)
///   BETL_PARALLEL_DEPTH=N      — batches in flight (default: 4)
/// </summary>
public sealed class QueuedDataComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly int _batchSize;
    private readonly int _batches;

    public string Id => _upstream.Id;
    public Schema OutputSchema => _upstream.OutputSchema;

    public QueuedDataComponent(IDataComponent upstream, int batchesInFlight, int batchSize)
    {
        _upstream = upstream;
        _batches = Math.Max(1, batchesInFlight);
        _batchSize = Math.Max(1, batchSize);
    }

    public IEnumerable<Row> Stream()
    {
        var queue = new BlockingCollection<Batch>(_batches);
        Exception? producerError = null;
        var pool = ArrayPool<Row>.Shared;
        var batchSize = _batchSize;

        var producer = Task.Run(() =>
        {
            Row[]? buf = pool.Rent(batchSize);
            var n = 0;
            try
            {
                foreach (var row in _upstream.Stream())
                {
                    buf![n++] = row;
                    if (n == batchSize)
                    {
                        queue.Add(new Batch(buf, batchSize));
                        buf = pool.Rent(batchSize);
                        n = 0;
                    }
                }
                if (n > 0)
                {
                    queue.Add(new Batch(buf!, n));
                    buf = null;
                }
            }
            catch (Exception ex) { producerError = ex; }
            finally
            {
                if (buf is not null) pool.Return(buf, clearArray: true);
                queue.CompleteAdding();
            }
        });

        foreach (var batch in queue.GetConsumingEnumerable())
        {
            try
            {
                var b = batch.Buffer;
                var c = batch.Count;
                for (var i = 0; i < c; i++)
                    yield return b[i];
            }
            finally
            {
                pool.Return(batch.Buffer, clearArray: true);
            }
        }

        producer.Wait();
        if (producerError is not null)
            throw new BetlException(
                $"queued component '{Id}' producer thread failed: {producerError.Message}",
                producerError);
    }

    private readonly struct Batch
    {
        public Row[] Buffer { get; }
        public int Count { get; }
        public Batch(Row[] buffer, int count) { Buffer = buffer; Count = count; }
    }

    public static bool ParallelEnabledByDefault()
    {
        var v = Environment.GetEnvironmentVariable("BETL_PARALLEL");
        return !string.Equals(v, "off", StringComparison.OrdinalIgnoreCase);
    }

    public static int DefaultBatchSize()
    {
        var v = Environment.GetEnvironmentVariable("BETL_PARALLEL_BATCH");
        return int.TryParse(v, out var n) && n > 0 ? n : 256;
    }

    public static int DefaultDepth()
    {
        var v = Environment.GetEnvironmentVariable("BETL_PARALLEL_DEPTH");
        return int.TryParse(v, out var n) && n > 0 ? n : 4;
    }
}
