using Betl.Core;

namespace Betl.Components.Generators;

/// <summary>
/// Smoke / assertion sink. Counts rows from the upstream and (optionally)
/// throws if the count doesn't match <paramref name="expected"/>.
/// </summary>
public sealed class BetlCountRowsSink(string id, long? expected, Action<string>? log = null) : ISink
{
    public string Id { get; } = id;
    public long Counted { get; private set; }

    public void Drain(IDataComponent input)
    {
        Counted = 0;
        foreach (var _ in input.Stream()) Counted++;
        log?.Invoke($"   {Id}: counted {Counted} row(s)");
        if (expected is { } want && Counted != want)
            throw new BetlException(
                $"betl.count_rows '{Id}': expected {want} rows, got {Counted}.");
    }
}
