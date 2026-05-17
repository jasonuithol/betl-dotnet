using Betl.Components;
using Betl.Core;
using Betl.Ssis.Shim.PipelineComponent;

namespace Betl.Providers.Dotnet;

/// <summary>
/// Hosts a user-supplied <see cref="BetlPipelineComponent"/> subclass. The user
/// source is Roslyn-compiled at construction; the SSIS-style lifecycle
/// (PreExecute → ProcessInput → PostExecute → Cleanup) is driven on each batch
/// of upstream rows.
/// </summary>
public sealed class DotnetPipelineComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly BetlPipelineComponent _instance;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public DotnetPipelineComponent(DotnetPipelineComponentStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;
        OutputSchema = step.OutputSchema;

        if (step.Lang != "csharp")
            throw new BetlException($"dotnet.pipelinecomponent '{step.Id}': only 'csharp' supported (got '{step.Lang}').");

        var type = DotnetCompiler.CompileAndFindSubclass<BetlPipelineComponent>(
            step.Source, $"dotnet.pipelinecomponent '{step.Id}'");
        _instance = (BetlPipelineComponent)Activator.CreateInstance(type)!;
    }

    public IEnumerable<Row> Stream()
    {
        // Materialise the upstream into one batch (Phase 5 simplification — the
        // upstream betl.linux model invokes ProcessInput per Arrow batch).
        var rows = _upstream.Stream().ToList();
        var buffer = new BetlPipelineBuffer(rows, _upstream.OutputSchema, OutputSchema);

        try
        {
            _instance.PreExecute();
            _instance.ProcessInput(buffer);
            _instance.PostExecute();
        }
        finally
        {
            _instance.Cleanup();
        }

        foreach (var values in buffer.DrainOutput())
            yield return new Row(OutputSchema, values);
    }
}
