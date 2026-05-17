using Betl.Components;
using Betl.Core;

namespace Betl.Runtime;

public sealed class Executor
{
    private readonly Pipeline _pipeline;
    private readonly ParameterContext _params;
    private readonly EngineRegistry _engines;
    private readonly Action<string>? _log;

    public Executor(Pipeline pipeline, ParameterContext parameters, EngineRegistry engines, Action<string>? log = null)
    {
        _pipeline = pipeline;
        _params = parameters;
        _engines = engines;
        _log = log;
    }

    public void Run()
    {
        foreach (var step in _pipeline.Steps)
        {
            switch (step)
            {
                case DataflowStep df:
                    RunDataflow(df);
                    break;
                default:
                    throw new BetlException(
                        $"Top-level step type '{step.GetType().Name}' is not supported in Phase 1. " +
                        "(Phase 1 implements only `dataflow`; tasks/control-flow land later.)");
            }
        }
    }

    private void RunDataflow(DataflowStep df)
    {
        Log($"-> dataflow '{df.Id}' ({df.Steps.Count} steps)");

        var components = new Dictionary<string, IDataComponent>(StringComparer.Ordinal);
        var sinks = new List<(ISink Sink, IDataComponent Upstream)>();

        foreach (var inner in df.Steps)
        {
            switch (inner)
            {
                case CsvReadStep cr:
                {
                    var path = _params.Substitute(cr.Path);
                    components[cr.Id] = new CsvReadComponent(cr, path);
                    Log($"   {cr.Id}: csv.read {path} ({cr.Schema.Columns.Count} cols)");
                    break;
                }

                case FilterStep f:
                {
                    var upstream = ResolveUpstream(components, f.From, f.Id, "filter.from");
                    var predicate = Compile(f.Where, upstream.OutputSchema);
                    components[f.Id] = new FilterComponent(f, upstream, predicate);
                    Log($"   {f.Id}: filter from={f.From}");
                    break;
                }

                case MapStep m:
                {
                    var upstream = ResolveUpstream(components, m.From, m.Id, "map.from");
                    components[m.Id] = new MapComponent(m, upstream,
                        e => Compile(e, upstream.OutputSchema));
                    Log($"   {m.Id}: map from={m.From}");
                    break;
                }

                case CsvWriteStep cw:
                {
                    var upstream = ResolveUpstream(components, cw.From, cw.Id, "csv.write.from");
                    var path = _params.Substitute(cw.Path);
                    sinks.Add((new CsvWriteComponent(cw, path), upstream));
                    Log($"   {cw.Id}: csv.write {path}");
                    break;
                }

                default:
                    throw new BetlException(
                        $"Dataflow step type '{inner.GetType().Name}' is not supported in Phase 1.");
            }
        }

        if (sinks.Count == 0)
            throw new BetlException($"dataflow '{df.Id}' has no sinks — nothing would happen.");

        foreach (var (sink, upstream) in sinks)
        {
            Log($"   drain -> {sink.Id}");
            sink.Drain(upstream);
        }

        Log($"<- dataflow '{df.Id}' done");
    }

    /// <summary>
    /// Compiles an expression, first expanding <c>${params.X}</c> / <c>${env.X}</c>
    /// in any string-valued literal so per-run parameter values land in the AST.
    /// </summary>
    private ICompiledExpression Compile(Expression expr, Schema inputSchema)
    {
        if (expr is LiteralExpression lit && lit.Value is string s)
            expr = new LiteralExpression(_params.Substitute(s));
        return _engines.Compile(expr, inputSchema);
    }

    private static IDataComponent ResolveUpstream(
        Dictionary<string, IDataComponent> components, string fromId, string stepId, string keyName)
    {
        if (components.TryGetValue(fromId, out var u)) return u;
        throw new BetlException(
            $"Step '{stepId}' references unknown {keyName} '{fromId}'. " +
            "(Forward references aren't allowed; upstream must be declared earlier.)");
    }

    private void Log(string msg) => _log?.Invoke(msg);
}
