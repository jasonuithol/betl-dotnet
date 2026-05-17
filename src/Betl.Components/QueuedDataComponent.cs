using System.Collections.Concurrent;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Wraps any <see cref="IDataComponent"/> with a producer thread + bounded
/// queue so the upstream computes its next batch of rows while the downstream
/// is busy with the current one. Matches the betl.linux `BETL_PARALLEL` model
/// (env var: on by default, set <c>BETL_PARALLEL=off</c> to disable;
/// <c>BETL_PARALLEL_DEPTH=N</c> tunes ring-buffer capacity, default 256).
/// </summary>
public sealed class QueuedDataComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly int _depth;

    public string Id => _upstream.Id;
    public Schema OutputSchema => _upstream.OutputSchema;

    public QueuedDataComponent(IDataComponent upstream, int depth)
    {
        _upstream = upstream;
        _depth = depth;
    }

    public IEnumerable<Row> Stream()
    {
        var queue = new BlockingCollection<Row>(_depth);
        Exception? producerError = null;

        var producer = Task.Run(() =>
        {
            try
            {
                foreach (var row in _upstream.Stream()) queue.Add(row);
            }
            catch (Exception ex) { producerError = ex; }
            finally { queue.CompleteAdding(); }
        });

        foreach (var row in queue.GetConsumingEnumerable())
            yield return row;

        producer.Wait();
        if (producerError is not null)
            throw new BetlException(
                $"queued component '{Id}' producer thread failed: {producerError.Message}",
                producerError);
    }

    public static bool ParallelEnabledByDefault()
    {
        var v = Environment.GetEnvironmentVariable("BETL_PARALLEL");
        return !string.Equals(v, "off", StringComparison.OrdinalIgnoreCase);
    }

    public static int DefaultDepth()
    {
        var v = Environment.GetEnvironmentVariable("BETL_PARALLEL_DEPTH");
        return int.TryParse(v, out var n) && n > 0 ? n : 256;
    }
}
