using Betl.Core;

namespace Betl.Components;

public sealed class UnionComponent : IDataComponent
{
    private readonly IReadOnlyList<IDataComponent> _upstreams;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public UnionComponent(UnionStep step, IReadOnlyList<IDataComponent> upstreams)
    {
        if (upstreams.Count < 2)
            throw new BetlException($"union '{step.Id}': needs at least 2 upstream streams.");

        Id = step.Id;
        _upstreams = upstreams;

        // Schemas must match column-by-column on (name, ArrowType).
        var first = upstreams[0].OutputSchema;
        for (var i = 1; i < upstreams.Count; i++)
        {
            var other = upstreams[i].OutputSchema;
            if (other.Columns.Count != first.Columns.Count)
                throw new BetlException(
                    $"union '{step.Id}': input #{i + 1} has {other.Columns.Count} columns, " +
                    $"input #1 has {first.Columns.Count}.");
            for (var c = 0; c < first.Columns.Count; c++)
            {
                if (!string.Equals(first.Columns[c].Name, other.Columns[c].Name, StringComparison.Ordinal))
                    throw new BetlException(
                        $"union '{step.Id}': input #{i + 1} column {c} is '{other.Columns[c].Name}', " +
                        $"input #1 is '{first.Columns[c].Name}'.");
                if (!string.Equals(first.Columns[c].ArrowType.Name, other.Columns[c].ArrowType.Name, StringComparison.Ordinal))
                    throw new BetlException(
                        $"union '{step.Id}': input #{i + 1} column '{other.Columns[c].Name}' is " +
                        $"{other.Columns[c].ArrowType.Name}, input #1 is {first.Columns[c].ArrowType.Name}.");
            }
        }

        OutputSchema = first;
    }

    public IEnumerable<Row> Stream()
    {
        foreach (var up in _upstreams)
            foreach (var row in up.Stream())
                yield return row;
    }
}
