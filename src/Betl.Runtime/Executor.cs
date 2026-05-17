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
        foreach (var step in _pipeline.Steps) RunControlStep(step);
    }

    private void RunControlStep(Step step)
    {
        switch (step)
        {
            case DataflowStep df:
                RunDataflow(df);
                break;

            case ForeachStep fe:
                RunForeach(fe);
                break;

            default:
                throw new BetlException(
                    $"Top-level step type '{step.GetType().Name}' is not supported yet. " +
                    "(Phase 2 supports dataflow and foreach; control-flow tasks land in Phase 3.)");
        }
    }

    private void RunForeach(ForeachStep step)
    {
        Log($"-> foreach '{step.Id}' over [{string.Join(", ", step.Over)}] as {step.Variable}");
        foreach (var raw in step.Over)
        {
            var value = _params.Substitute(raw);
            Log($"   iter {step.Variable}={value}");
            using var _ = _params.PushVar(step.Variable, value);
            foreach (var inner in step.Body) RunControlStep(inner);
        }
        Log($"<- foreach '{step.Id}' done");
    }

    private void RunDataflow(DataflowStep df)
    {
        Log($"-> dataflow '{df.Id}' ({df.Steps.Count} steps)");

        var ports = new Dictionary<string, IDataComponent>(StringComparer.Ordinal);
        var sinks = new List<(ISink Sink, IDataComponent Upstream)>();

        foreach (var inner in df.Steps)
        {
            switch (inner)
            {
                // ----- sources -----
                case CsvReadStep cr:
                {
                    var path = _params.Substitute(cr.Path);
                    RegisterPort(ports, cr.Id, new CsvReadComponent(cr, path));
                    Log($"   {cr.Id}: csv.read {path} ({cr.Schema.Columns.Count} cols)");
                    break;
                }
                case JsonReadStep jr:
                {
                    var path = _params.Substitute(jr.Path);
                    RegisterPort(ports, jr.Id, new JsonReadComponent(jr, path));
                    Log($"   {jr.Id}: json.read {path} ({jr.Columns.Count} cols)");
                    break;
                }

                // ----- 1-in 1-out transforms -----
                case FilterStep f:
                {
                    var upstream = ResolveFrom(ports, f.From, f.Id, "filter.from");
                    var predicate = Compile(f.Where, upstream.OutputSchema);
                    RegisterPort(ports, f.Id, new FilterComponent(f, upstream, predicate));
                    Log($"   {f.Id}: filter from={f.From}");
                    break;
                }
                case MapStep m:
                {
                    var upstream = ResolveFrom(ports, m.From, m.Id, "map.from");
                    RegisterPort(ports, m.Id, new MapComponent(m, upstream,
                        e => Compile(e, upstream.OutputSchema)));
                    Log($"   {m.Id}: map from={m.From}");
                    break;
                }
                case DistinctStep d:
                {
                    var upstream = ResolveFrom(ports, d.From, d.Id, "distinct.from");
                    RegisterPort(ports, d.Id, new DistinctComponent(d, upstream));
                    Log($"   {d.Id}: distinct from={d.From}");
                    break;
                }
                case LimitStep l:
                {
                    var upstream = ResolveFrom(ports, l.From, l.Id, "limit.from");
                    RegisterPort(ports, l.Id, new LimitComponent(l, upstream));
                    Log($"   {l.Id}: limit n={l.N} from={l.From}");
                    break;
                }
                case SortStep s:
                {
                    var upstream = ResolveFrom(ports, s.From, s.Id, "sort.from");
                    RegisterPort(ports, s.Id, new SortComponent(s, upstream));
                    Log($"   {s.Id}: sort by [{string.Join(", ", s.By.Select(k => $"{k.Column} {k.Direction}"))}]");
                    break;
                }
                case AggregateStep a:
                {
                    var upstream = ResolveFrom(ports, a.From, a.Id, "aggregate.from");
                    RegisterPort(ports, a.Id, new AggregateComponent(a, upstream));
                    Log($"   {a.Id}: aggregate group_by=[{string.Join(", ", a.GroupBy)}] compute={a.Compute.Count}");
                    break;
                }

                // ----- N-in 1-out -----
                case UnionStep u:
                {
                    var upstreams = u.From
                        .Select(id => ResolveFrom(ports, id, u.Id, "union.from"))
                        .ToList();
                    RegisterPort(ports, u.Id, new UnionComponent(u, upstreams));
                    Log($"   {u.Id}: union from=[{string.Join(", ", u.From)}]");
                    break;
                }
                case JoinStep j:
                {
                    var left  = ResolveFrom(ports, j.Left,  j.Id, "join.left");
                    var right = ResolveFrom(ports, j.Right, j.Id, "join.right");
                    RegisterPort(ports, j.Id, new JoinComponent(j, left, right));
                    Log($"   {j.Id}: join {j.Kind} left={j.Left} right={j.Right}");
                    break;
                }

                // ----- 1-in N-out -----
                case MulticastStep mc:
                {
                    var upstream = ResolveFrom(ports, mc.From, mc.Id, "multicast.from");
                    var multi = new MulticastComponent(mc, upstream);
                    foreach (var (name, port) in multi.Outputs)
                        RegisterPort(ports, $"{mc.Id}:{name}", port);
                    Log($"   {mc.Id}: multicast outputs=[{string.Join(", ", mc.Outputs)}]");
                    break;
                }
                case ConditionalSplitStep cs:
                {
                    var upstream = ResolveFrom(ports, cs.From, cs.Id, "conditional_split.from");
                    var split = new ConditionalSplitComponent(cs, upstream,
                        e => Compile(e, upstream.OutputSchema));
                    foreach (var (name, port) in split.Outputs)
                        RegisterPort(ports, $"{cs.Id}:{name}", port);
                    Log($"   {cs.Id}: conditional_split cases=[{string.Join(", ", cs.Cases.Select(c => c.Key))}]" +
                        (cs.DefaultCase is null ? "" : $" default={cs.DefaultCase}"));
                    break;
                }

                // ----- sinks -----
                case CsvWriteStep cw:
                {
                    var upstream = ResolveFrom(ports, cw.From, cw.Id, "csv.write.from");
                    var path = _params.Substitute(cw.Path);
                    sinks.Add((new CsvWriteComponent(cw, path), upstream));
                    Log($"   {cw.Id}: csv.write {path}");
                    break;
                }
                case JsonWriteStep jw:
                {
                    var upstream = ResolveFrom(ports, jw.From, jw.Id, "json.write.from");
                    var path = _params.Substitute(jw.Path);
                    sinks.Add((new JsonWriteComponent(jw, path), upstream));
                    Log($"   {jw.Id}: json.write {path}");
                    break;
                }

                default:
                    throw new BetlException(
                        $"Dataflow step type '{inner.GetType().Name}' is not supported.");
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

    private static void RegisterPort(Dictionary<string, IDataComponent> ports, string key, IDataComponent component)
    {
        if (ports.ContainsKey(key))
            throw new BetlException($"Duplicate port id '{key}' in this dataflow.");
        ports[key] = component;
    }

    private static IDataComponent ResolveFrom(
        Dictionary<string, IDataComponent> ports, string fromRef, string stepId, string keyName)
    {
        if (ports.TryGetValue(fromRef, out var c)) return c;
        throw new BetlException(
            $"Step '{stepId}' references unknown {keyName} '{fromRef}'. " +
            "(Upstream must be declared earlier; multi-output upstreams need the `step:port` form.)");
    }

    /// <summary>
    /// Compiles an expression, first expanding <c>${params.X}</c> / <c>${env.X}</c> /
    /// <c>${vars.X}</c> in any string-valued literal so per-run / per-iteration values
    /// land in the AST.
    /// </summary>
    private ICompiledExpression Compile(Expression expr, Schema inputSchema)
    {
        if (expr is LiteralExpression lit && lit.Value is string s)
            expr = new LiteralExpression(_params.Substitute(s));
        return _engines.Compile(expr, inputSchema);
    }

    private void Log(string msg) => _log?.Invoke(msg);
}
