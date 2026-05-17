namespace Betl.Ssis.Shim.Task;

/// <summary>
/// Standalone task base — the .NET analogue of the SSIS Script Task. User code
/// subclasses and overrides <see cref="Execute"/>. The supplied
/// <see cref="BetlTaskContext"/> exposes pipeline params and a log hook.
/// </summary>
public abstract class BetlTask
{
    public abstract void Execute(BetlTaskContext ctx);
}

public sealed class BetlTaskContext
{
    public required IReadOnlyDictionary<string, object?> Params { get; init; }
    public required Action<string> Log { get; init; }
}
