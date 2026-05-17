namespace Betl.Components;

/// <summary>
/// A control-flow step: anything with side effects that isn't a row pipeline.
/// Executor calls <see cref="Execute"/> exactly once per occurrence (or once per
/// foreach iteration). Tasks receive already-resolved field values (paths, URLs,
/// SQL text) — placeholder substitution happens at construction time.
/// </summary>
public interface IControlTask
{
    string Id { get; }
    void Execute(Action<string>? log);
}
