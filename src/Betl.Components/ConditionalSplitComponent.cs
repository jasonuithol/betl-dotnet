using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Multi-output router: each input row is sent to the first case whose predicate
/// returns TRUE (3VL NULL or FALSE => no match). Unmatched rows either go to the
/// declared <see cref="ConditionalSplitStep.DefaultCase"/> port or are dropped.
/// </summary>
public sealed class ConditionalSplitComponent
{
    public string Id { get; }
    public Schema OutputSchema { get; }
    public IReadOnlyList<KeyValuePair<string, IDataComponent>> Outputs { get; }

    public ConditionalSplitComponent(
        ConditionalSplitStep step,
        IDataComponent upstream,
        Func<Expression, ICompiledExpression> compile)
    {
        Id = step.Id;
        OutputSchema = upstream.OutputSchema;

        var caseExprs = step.Cases.Select(c => (c.Key, Predicate: compile(c.Value))).ToList();

        var allPorts = new List<string>(caseExprs.Count + 1);
        foreach (var c in caseExprs) allPorts.Add(c.Key);
        if (step.DefaultCase is not null) allPorts.Add(step.DefaultCase);

        var buckets = new Lazy<Dictionary<string, List<Row>>>(() =>
        {
            var b = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            foreach (var name in allPorts) b[name] = [];
            foreach (var row in upstream.Stream())
            {
                var matched = false;
                foreach (var (name, predicate) in caseExprs)
                {
                    var v = predicate.Evaluate(row);
                    if (v is bool tb && tb)
                    {
                        b[name].Add(row);
                        matched = true;
                        break;
                    }
                }
                if (!matched && step.DefaultCase is not null) b[step.DefaultCase].Add(row);
            }
            return b;
        });

        var outs = new List<KeyValuePair<string, IDataComponent>>(allPorts.Count);
        foreach (var name in allPorts)
        {
            var capturedName = name;
            var portId = $"{step.Id}:{capturedName}";
            outs.Add(KeyValuePair.Create(capturedName,
                (IDataComponent)new Port(portId, OutputSchema, () => (IEnumerable<Row>)buckets.Value[capturedName])));
        }
        Outputs = outs;
    }
}
