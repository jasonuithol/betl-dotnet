namespace Betl.Ssis.Shim.Script;

/// <summary>
/// Async script-component base. User C# subclasses override <see cref="OnRow"/>
/// to emit zero or more output rows per upstream input row, and optionally
/// <see cref="OnEof"/> to emit final buffered output rows when the upstream
/// signals end-of-data. Mirrors the upstream SSIS Script Component protocol
/// from betl.linux (`providers/betl-dotnet/shim/script/BetlScript.cs`),
/// adapted for managed-only hosting (no Arrow C ABI / NativeAOT).
/// </summary>
public abstract class BetlScript
{
    public abstract IEnumerable<object?[]> OnRow(IReadOnlyList<object?> row);
    public virtual IEnumerable<object?[]> OnEof() => [];
}
