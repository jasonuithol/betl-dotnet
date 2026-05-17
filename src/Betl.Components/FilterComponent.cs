using Betl.Core;

namespace Betl.Components;

public sealed class FilterComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly ICompiledExpression _predicate;

    public string Id { get; }
    public Schema OutputSchema => _upstream.OutputSchema;

    public FilterComponent(FilterStep step, IDataComponent upstream, ICompiledExpression predicate)
    {
        Id = step.Id;
        _upstream = upstream;
        _predicate = predicate;
    }

    public IEnumerable<Row> Stream()
    {
        foreach (var row in _upstream.Stream())
        {
            var v = _predicate.Evaluate(row);
            // 3VL: NULL filters the row out (matches WHERE semantics in SQL).
            if (v is bool b && b) yield return row;
        }
    }
}
