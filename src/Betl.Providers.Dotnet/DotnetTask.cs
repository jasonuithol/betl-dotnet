using Betl.Components;
using Betl.Core;
using Betl.Ssis.Shim.Task;

namespace Betl.Providers.Dotnet;

public sealed class DotnetTask : IControlTask
{
    private readonly BetlTask _instance;
    private readonly IReadOnlyDictionary<string, object?> _params;

    public string Id { get; }

    public DotnetTask(DotnetTaskStep step, IReadOnlyDictionary<string, object?> resolvedParams)
    {
        Id = step.Id;
        if (step.Lang != "csharp")
            throw new BetlException($"dotnet.task '{step.Id}': only 'csharp' supported (got '{step.Lang}').");

        var type = DotnetCompiler.CompileAndFindSubclass<BetlTask>(step.Source, $"dotnet.task '{step.Id}'");
        _instance = (BetlTask)Activator.CreateInstance(type)!;
        _params = resolvedParams;
    }

    public void Execute(Action<string>? log)
    {
        var ctx = new BetlTaskContext
        {
            Params = _params,
            Log = msg => log?.Invoke($"   [{Id}] {msg}"),
        };
        _instance.Execute(ctx);
    }
}
