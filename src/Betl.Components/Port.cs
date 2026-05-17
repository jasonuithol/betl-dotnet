using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Thin <see cref="IDataComponent"/> wrapper used by multi-output components
/// (multicast, conditional_split) to expose each named output as an independent
/// stream.
/// </summary>
internal sealed class Port(string id, Schema schema, Func<IEnumerable<Row>> source) : IDataComponent
{
    public string Id { get; } = id;
    public Schema OutputSchema { get; } = schema;
    public IEnumerable<Row> Stream() => source();
}
