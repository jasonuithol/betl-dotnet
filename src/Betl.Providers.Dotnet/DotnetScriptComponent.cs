using Betl.Components;
using Betl.Core;
using Betl.Ssis.Shim.Script;

namespace Betl.Providers.Dotnet;

public sealed class DotnetScriptComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly BetlScript _instance;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public DotnetScriptComponent(DotnetScriptStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;
        OutputSchema = step.OutputSchema;

        if (step.Lang != "csharp")
            throw new BetlException($"dotnet.script '{step.Id}': only 'csharp' supported (got '{step.Lang}').");

        var type = DotnetCompiler.CompileAndFindSubclass<BetlScript>(step.Source, $"dotnet.script '{step.Id}'");
        _instance = (BetlScript)Activator.CreateInstance(type)!;
    }

    public IEnumerable<Row> Stream()
    {
        foreach (var row in _upstream.Stream())
        {
            foreach (var emitted in _instance.OnRow(row.Values))
            {
                if (emitted.Length != OutputSchema.Columns.Count)
                    throw new BetlException(
                        $"dotnet.script '{Id}': emitted row has {emitted.Length} values, schema declares {OutputSchema.Columns.Count}.");
                yield return new Row(OutputSchema, emitted);
            }
        }
        foreach (var emitted in _instance.OnEof())
        {
            if (emitted.Length != OutputSchema.Columns.Count)
                throw new BetlException(
                    $"dotnet.script '{Id}': OnEof emitted row has {emitted.Length} values, schema declares {OutputSchema.Columns.Count}.");
            yield return new Row(OutputSchema, emitted);
        }
    }
}
