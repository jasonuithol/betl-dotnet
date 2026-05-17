using Betl.Core;

namespace Betl.Components;

public sealed class LimitComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly long _n;

    public string Id { get; }
    public Schema OutputSchema => _upstream.OutputSchema;

    public LimitComponent(LimitStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;
        _n = step.N;
    }

    public IEnumerable<Row> Stream()
    {
        if (_n == 0) yield break;
        long emitted = 0;
        foreach (var row in _upstream.Stream())
        {
            yield return row;
            if (++emitted >= _n) yield break;
        }
    }
}
