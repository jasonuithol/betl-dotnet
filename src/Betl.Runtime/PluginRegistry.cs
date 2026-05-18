using System.Reflection;
using System.Runtime.Loader;
using Betl.Components;
using Betl.Core;

namespace Betl.Runtime;

/// <summary>
/// Discovers and registers external plugin assemblies. A plugin assembly
/// is any managed DLL containing one or more non-abstract classes
/// implementing <see cref="IBetlComponentPlugin"/> or
/// <see cref="IBetlTaskPlugin"/>.
///
/// Discovery convention:
///   1. <c>./plugins/</c> relative to the current working directory.
///   2. Each path in the <c>BETL_PLUGINS</c> environment variable
///      (semicolon-separated on Windows, colon on Unix — uses
///      <see cref="Path.PathSeparator"/>).
///
/// All <c>*.dll</c> files in each existing directory are loaded into
/// <see cref="AssemblyLoadContext.Default"/>. This means plugin DLLs and
/// their support DLLs should sit side by side.
/// </summary>
public sealed class PluginRegistry
{
    private readonly Dictionary<string, IBetlComponentPlugin> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBetlTaskPlugin> _tasks = new(StringComparer.Ordinal);

    public IReadOnlySet<string> StepTypes { get; }

    public PluginRegistry(IEnumerable<string> directories)
    {
        var stepTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                Assembly asm;
                try { asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll)); }
                catch (BadImageFormatException) { continue; }  // native or otherwise unloadable
                catch (FileLoadException) { continue; }        // already loaded by a different ALC — skip

                foreach (var t in SafeGetTypes(asm))
                {
                    if (t.IsAbstract || t.IsInterface) continue;

                    if (typeof(IBetlComponentPlugin).IsAssignableFrom(t))
                    {
                        var inst = (IBetlComponentPlugin)Activator.CreateInstance(t)!;
                        if (!_components.TryAdd(inst.StepType, inst))
                            throw new BetlException(
                                $"plugin: component step type '{inst.StepType}' is registered twice " +
                                $"(in '{dll}' and an earlier plugin).");
                        stepTypes.Add(inst.StepType);
                    }
                    else if (typeof(IBetlTaskPlugin).IsAssignableFrom(t))
                    {
                        var inst = (IBetlTaskPlugin)Activator.CreateInstance(t)!;
                        if (!_tasks.TryAdd(inst.StepType, inst))
                            throw new BetlException(
                                $"plugin: task step type '{inst.StepType}' is registered twice " +
                                $"(in '{dll}' and an earlier plugin).");
                        stepTypes.Add(inst.StepType);
                    }
                }
            }
        }

        StepTypes = stepTypes;
    }

    public bool TryGetComponent(string stepType, out IBetlComponentPlugin plugin)
        => _components.TryGetValue(stepType, out plugin!);

    public bool TryGetTask(string stepType, out IBetlTaskPlugin plugin)
        => _tasks.TryGetValue(stepType, out plugin!);

    /// <summary>
    /// Scan the conventional locations (CWD/plugins + BETL_PLUGINS env var)
    /// and build a registry. Always returns a non-null instance; if nothing
    /// is found, StepTypes is empty and lookups always miss.
    /// </summary>
    public static PluginRegistry Discover()
    {
        var dirs = new List<string> { Path.Combine(Environment.CurrentDirectory, "plugins") };
        var envVar = Environment.GetEnvironmentVariable("BETL_PLUGINS");
        if (!string.IsNullOrWhiteSpace(envVar))
            dirs.AddRange(envVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return new PluginRegistry(dirs);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
