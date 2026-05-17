using Betl.Core;

namespace Betl.Components;

/// <summary>
/// 1-input N-output fan-out. Materialises the upstream once, then exposes every
/// declared output port as an independent <see cref="IDataComponent"/> that
/// re-iterates the same buffered rows.
/// </summary>
public sealed class MulticastComponent
{
    public string Id { get; }
    public Schema OutputSchema { get; }
    public IReadOnlyList<KeyValuePair<string, IDataComponent>> Outputs { get; }

    public MulticastComponent(MulticastStep step, IDataComponent upstream)
    {
        Id = step.Id;
        OutputSchema = upstream.OutputSchema;

        var materialised = new Lazy<List<Row>>(() => upstream.Stream().ToList());

        var outs = new List<KeyValuePair<string, IDataComponent>>(step.Outputs.Count);
        foreach (var name in step.Outputs)
        {
            var portId = $"{step.Id}:{name}";
            outs.Add(KeyValuePair.Create(name, (IDataComponent)new Port(portId, OutputSchema, () => materialised.Value)));
        }
        Outputs = outs;
    }
}
