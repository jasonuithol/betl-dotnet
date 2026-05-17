using Betl.Core;

namespace Betl.Components;

public sealed class DistinctComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly int[] _keyIndices;

    public string Id { get; }
    public Schema OutputSchema => _upstream.OutputSchema;

    public DistinctComponent(DistinctStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;

        if (step.Keys is { Count: > 0 })
        {
            _keyIndices = RowOps.ResolveColumnIndices(upstream.OutputSchema, step.Keys, $"distinct '{step.Id}'");
        }
        else
        {
            // Full-row dedupe -> all columns are keys.
            _keyIndices = new int[upstream.OutputSchema.Columns.Count];
            for (var i = 0; i < _keyIndices.Length; i++) _keyIndices[i] = i;
        }
    }

    public IEnumerable<Row> Stream()
    {
        var seen = new HashSet<object?[]>(ObjectArrayComparer.Instance);
        foreach (var row in _upstream.Stream())
        {
            var k = RowOps.ExtractKey(row, _keyIndices);
            if (seen.Add(k)) yield return row;
        }
    }
}
