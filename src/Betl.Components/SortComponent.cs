using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Materialising stable sort. Loads the upstream into memory, sorts by the declared
/// key list (ASC/DESC per key), then re-emits. NULLs sort first.
/// </summary>
public sealed class SortComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly int[] _keyIndices;
    private readonly SortDirection[] _dirs;

    public string Id { get; }
    public Schema OutputSchema => _upstream.OutputSchema;

    public SortComponent(SortStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;
        _keyIndices = new int[step.By.Count];
        _dirs = new SortDirection[step.By.Count];
        for (var i = 0; i < step.By.Count; i++)
        {
            _keyIndices[i] = upstream.OutputSchema.IndexOf(step.By[i].Column);
            if (_keyIndices[i] < 0)
                throw new BetlException($"sort '{step.Id}': key column '{step.By[i].Column}' is not in schema.");
            _dirs[i] = step.By[i].Direction;
        }
    }

    public IEnumerable<Row> Stream()
    {
        var indexed = new List<(Row Row, int Idx)>();
        var idx = 0;
        foreach (var r in _upstream.Stream()) indexed.Add((r, idx++));

        indexed.Sort((a, b) =>
        {
            for (var i = 0; i < _keyIndices.Length; i++)
            {
                var c = RowOps.CompareScalars(a.Row.Values[_keyIndices[i]], b.Row.Values[_keyIndices[i]]);
                if (_dirs[i] == SortDirection.Desc) c = -c;
                if (c != 0) return c;
            }
            // Stability tie-breaker on original index.
            return a.Idx.CompareTo(b.Idx);
        });

        foreach (var p in indexed) yield return p.Row;
    }
}
