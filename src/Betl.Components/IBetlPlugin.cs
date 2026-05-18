using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Marker base for betl plugin extension points. Plugin assemblies declare
/// one or more non-abstract classes implementing <see cref="IBetlComponentPlugin"/>
/// or <see cref="IBetlTaskPlugin"/>; the runtime scans configured plugin
/// directories at startup and registers the discovered <see cref="StepType"/>
/// strings so they become first-class step types in YAML.
/// </summary>
public interface IBetlPlugin
{
    /// <summary>
    /// The YAML step-type string this plugin handles (e.g. "myco.foo"). Must
    /// be globally unique within the loaded plugin set. Collision is a startup
    /// error.
    /// </summary>
    string StepType { get; }
}

/// <summary>
/// Plugin that contributes a data-flow component (source, transform, or
/// pass-through). The runtime resolves <c>from:</c> from the YAML body and
/// hands the resulting upstream <see cref="IDataComponent"/> to <see cref="Create"/>;
/// a null upstream indicates this is a source step.
/// </summary>
public interface IBetlComponentPlugin : IBetlPlugin
{
    /// <summary>
    /// Build the runtime component instance. <paramref name="body"/> is the
    /// raw YAML body of the step as a dictionary (scalars unboxed to
    /// long/double/bool/string/null; nested mappings/sequences as
    /// IReadOnlyDictionary / IReadOnlyList). Plugin is responsible for
    /// validating the fields it cares about and raising <see cref="BetlException"/>
    /// on bad input.
    /// </summary>
    IDataComponent Create(
        string stepId,
        IReadOnlyDictionary<string, object?> body,
        IDataComponent? upstream,
        IReadOnlyDictionary<string, object?> resolvedParams);
}

/// <summary>
/// Plugin that contributes a top-level control-flow task (e.g. an external
/// API call, a custom side-effecting action). No upstream port.
/// </summary>
public interface IBetlTaskPlugin : IBetlPlugin
{
    IControlTask Create(
        string stepId,
        IReadOnlyDictionary<string, object?> body,
        IReadOnlyDictionary<string, object?> resolvedParams);
}
