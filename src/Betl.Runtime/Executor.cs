using System.Globalization;
using System.Text.RegularExpressions;
using Betl.Components;
using Betl.Components.Generators;
using Betl.Components.Tasks;
using Betl.Core;
using Betl.Providers.Dotnet;
using Betl.Providers.Sql;

namespace Betl.Runtime;

public sealed partial class Executor
{
    private readonly Pipeline _pipeline;
    private readonly ParameterContext _params;
    private readonly EngineRegistry _engines;
    private readonly ConnectionRegistry? _sqlRegistry;
    private readonly Action<string>? _log;

    public Executor(
        Pipeline pipeline,
        ParameterContext parameters,
        EngineRegistry engines,
        ConnectionRegistry? sqlRegistry = null,
        Action<string>? log = null)
    {
        _pipeline = pipeline;
        _params = parameters;
        _engines = engines;
        _sqlRegistry = sqlRegistry;
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
            case DataflowStep df:    RunDataflow(df); break;
            case ForeachStep fe:     RunForeach(fe); break;
            case ShellStep sh:       RunShell(sh); break;
            case FileCopyStep fc:    RunTask(new FileCopyTask(fc.Id, _params.Substitute(fc.Src), _params.Substitute(fc.Dst))); break;
            case FileMoveStep fm:    RunTask(new FileMoveTask(fm.Id, _params.Substitute(fm.Src), _params.Substitute(fm.Dst))); break;
            case FileDeleteStep fd:  RunTask(new FileDeleteTask(fd.Id, _params.Substitute(fd.Path))); break;
            case HttpGetStep hg:     RunTask(new HttpGetTask(
                hg.Id, _params.Substitute(hg.Url), _params.Substitute(hg.SaveTo),
                hg.Headers?.Select(_params.Substitute).ToList(), ParseTimeout(hg.Timeout))); break;
            case HttpPostStep hp:    RunTask(new HttpPostTask(
                hp.Id, _params.Substitute(hp.Url), _params.Substitute(hp.SaveTo),
                hp.Body is null ? null : _params.Substitute(hp.Body),
                hp.BodyFile is null ? null : _params.Substitute(hp.BodyFile),
                hp.Headers?.Select(_params.Substitute).ToList(), ParseTimeout(hp.Timeout))); break;
            case SmtpSendStep sm:    RunSmtp(sm); break;
            case SqlExecuteStep se:  RunSqlExecute(se); break;
            case DotnetTaskStep dt:  RunDotnetTask(dt); break;
            default:
                throw new BetlException($"Top-level step type '{step.GetType().Name}' is not supported.");
        }
    }

    private void RunShell(ShellStep step)
    {
        var argv = step.Argv.Select(_params.Substitute).ToList();
        var env = step.Env.ToDictionary(kv => kv.Key, kv => _params.Substitute(kv.Value), StringComparer.Ordinal);
        RunTask(new ShellTask(step.Id, argv, ParseTimeout(step.Timeout), env, step.Stdout, step.Stderr));
    }

    private void RunSmtp(SmtpSendStep step)
    {
        var body = step.Body is not null
            ? _params.Substitute(step.Body)
            : File.ReadAllText(_params.Substitute(step.BodyFile!));

        RunTask(new SmtpSendTask(
            step.Id,
            _params.Substitute(step.Url),
            step.Username is null ? null : _params.Substitute(step.Username),
            step.Password is null ? null : _params.Substitute(step.Password),
            _params.Substitute(step.From),
            step.To.Select(_params.Substitute).ToList(),
            step.Cc?.Select(_params.Substitute).ToList(),
            _params.Substitute(step.Subject),
            body));
    }

    private void RunDotnetTask(DotnetTaskStep step)
    {
        var resolved = _pipeline.Parameters.Keys.ToDictionary(k => k, k => _params.Get(k));
        var merged = MergeDotnetRefs(
            step.References, step.Packages, $"dotnet.task '{step.Id}'");
        var withRefs = step with { References = merged };
        RunTask(new DotnetTask(withRefs, resolved));
    }

    private IReadOnlyList<string> MergeDotnetRefs(
        IReadOnlyList<string> references, IReadOnlyList<string> packages, string contextLabel)
    {
        var substRefs = references.Select(_params.Substitute).ToList();
        if (packages.Count == 0) return substRefs;
        var substPkgs = packages.Select(_params.Substitute).ToList();
        var resolved = PackageResolver.Resolve(substPkgs, contextLabel);
        var merged = new List<string>(substRefs.Count + resolved.Count);
        merged.AddRange(substRefs);
        merged.AddRange(resolved);
        return merged;
    }

    private void RunSqlExecute(SqlExecuteStep step)
    {
        var (provider, dsn) = ResolveConnection(step.Connection, $"sql.execute '{step.Id}'");
        var sql = step.Sql is not null
            ? _params.Substitute(step.Sql)
            : File.ReadAllText(_params.Substitute(step.File!));
        Log($"-> sql.execute '{step.Id}' connection={step.Connection}");
        RunTask(new SqlExecuteTask(step, provider, dsn, sql));
    }

    private void RunTask(IControlTask task)
    {
        Log($"-> task '{task.Id}' ({task.GetType().Name})");
        task.Execute(_log);
        Log($"<- task '{task.Id}' done");
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
                // ----- sources --------------------------------------------------
                case CsvReadStep cr:
                    RegisterPort(ports, cr.Id, new CsvReadComponent(cr, _params.Substitute(cr.Path)));
                    Log($"   {cr.Id}: csv.read {_params.Substitute(cr.Path)}");
                    break;
                case JsonReadStep jr:
                    RegisterPort(ports, jr.Id, new JsonReadComponent(jr, _params.Substitute(jr.Path)));
                    Log($"   {jr.Id}: json.read {_params.Substitute(jr.Path)}");
                    break;
                case ArrowReadStep ar:
                    RegisterPort(ports, ar.Id, new ArrowReadComponent(ar, _params.Substitute(ar.Path)));
                    Log($"   {ar.Id}: arrow.read {_params.Substitute(ar.Path)}");
                    break;
                case BetlGenInt64Step gi:
                    RegisterPort(ports, gi.Id, new BetlGenInt64Component(gi));
                    Log($"   {gi.Id}: betl.gen_int64 n={gi.N}");
                    break;
                case BetlGenStringsStep gs:
                    RegisterPort(ports, gs.Id, new BetlGenStringsComponent(gs));
                    Log($"   {gs.Id}: betl.gen_strings n={gs.N}");
                    break;
                case SqlReadStep sr:
                {
                    var (provider, dsn) = ResolveConnection(sr.Connection, $"{sr.ProviderHint}.read '{sr.Id}'");
                    var sql = sr.Sql is not null
                        ? _params.Substitute(sr.Sql)
                        : File.ReadAllText(_params.Substitute(sr.File!));
                    RegisterPort(ports, sr.Id, new SqlReadComponent(sr, provider, dsn, sql));
                    Log($"   {sr.Id}: {sr.ProviderHint}.read connection={sr.Connection}");
                    break;
                }

                // ----- 1-in 1-out transforms ------------------------------------
                case FilterStep f:
                {
                    var u = ResolveFrom(ports, f.From, f.Id, "filter.from");
                    RegisterPort(ports, f.Id, new FilterComponent(f, u, Compile(f.Where, u.OutputSchema)));
                    Log($"   {f.Id}: filter from={f.From}");
                    break;
                }
                case MapStep m:
                {
                    var u = ResolveFrom(ports, m.From, m.Id, "map.from");
                    RegisterPort(ports, m.Id, new MapComponent(m, u, e => Compile(e, u.OutputSchema)));
                    Log($"   {m.Id}: map from={m.From}");
                    break;
                }
                case DistinctStep d:
                {
                    var u = ResolveFrom(ports, d.From, d.Id, "distinct.from");
                    RegisterPort(ports, d.Id, new DistinctComponent(d, u));
                    Log($"   {d.Id}: distinct from={d.From}");
                    break;
                }
                case LimitStep l:
                {
                    var u = ResolveFrom(ports, l.From, l.Id, "limit.from");
                    RegisterPort(ports, l.Id, new LimitComponent(l, u));
                    Log($"   {l.Id}: limit n={l.N}");
                    break;
                }
                case SortStep s:
                {
                    var u = ResolveFrom(ports, s.From, s.Id, "sort.from");
                    RegisterPort(ports, s.Id, new SortComponent(s, u));
                    Log($"   {s.Id}: sort by [{string.Join(", ", s.By.Select(k => $"{k.Column} {k.Direction}"))}]");
                    break;
                }
                case AggregateStep a:
                {
                    var u = ResolveFrom(ports, a.From, a.Id, "aggregate.from");
                    RegisterPort(ports, a.Id, new AggregateComponent(a, u));
                    Log($"   {a.Id}: aggregate group_by=[{string.Join(", ", a.GroupBy)}] compute={a.Compute.Count}");
                    break;
                }
                case PivotStep pv:
                {
                    var u = ResolveFrom(ports, pv.From, pv.Id, "pivot.from");
                    RegisterPort(ports, pv.Id, new PivotComponent(pv, u));
                    Log($"   {pv.Id}: pivot keys=[{string.Join(", ", pv.PivotKeys)}] name={pv.NameColumn} value={pv.ValueColumn}");
                    break;
                }
                case UnpivotStep up:
                {
                    var u = ResolveFrom(ports, up.From, up.Id, "unpivot.from");
                    RegisterPort(ports, up.Id, new UnpivotComponent(up, u));
                    Log($"   {up.Id}: unpivot value_cols=[{string.Join(", ", up.ValueColumns)}]");
                    break;
                }
                case LookupStep lk:
                {
                    var u = ResolveFrom(ports, lk.From, lk.Id, "lookup.from");
                    var (provider, dsn) = ResolveConnection(lk.Connection, $"lookup '{lk.Id}'");
                    RegisterPort(ports, lk.Id, new SqlLookupComponent(lk, u, provider, dsn));
                    Log($"   {lk.Id}: lookup connection={lk.Connection} table={lk.Table}");
                    break;
                }
                case DotnetScriptStep ds:
                {
                    var u = ResolveFrom(ports, ds.From, ds.Id, "dotnet.script.from");
                    var dsRefs = MergeDotnetRefs(
                        ds.References, ds.Packages, $"dotnet.script '{ds.Id}'");
                    var dsWithRefs = ds with { References = dsRefs };
                    RegisterPort(ports, ds.Id, new DotnetScriptComponent(dsWithRefs, u));
                    Log($"   {ds.Id}: dotnet.script ({ds.OutputSchema.Columns.Count} out cols)");
                    break;
                }
                case DotnetPipelineComponentStep dpc:
                {
                    var u = ResolveFrom(ports, dpc.From, dpc.Id, "dotnet.pipelinecomponent.from");
                    var dpcRefs = MergeDotnetRefs(
                        dpc.References, dpc.Packages, $"dotnet.pipelinecomponent '{dpc.Id}'");
                    var dpcWithRefs = dpc with { References = dpcRefs };
                    var dpcDriver = new DotnetPipelineComponent(dpcWithRefs, u);
                    foreach (var (name, port) in dpcDriver.Outputs)
                    {
                        var key = name == "out" ? dpc.Id : $"{dpc.Id}:{name}";
                        RegisterPort(ports, key, port);
                    }
                    Log($"   {dpc.Id}: dotnet.pipelinecomponent " +
                        $"({dpc.OutputSchema.Columns.Count} out cols" +
                        (dpc.Async ? ", async" : "") +
                        (dpc.ErrorOutput ? ", error_output" : "") + ")");
                    break;
                }

                // ----- N-in 1-out ----------------------------------------------
                case UnionStep un:
                {
                    var ups = un.From.Select(id => ResolveFrom(ports, id, un.Id, "union.from")).ToList();
                    RegisterPort(ports, un.Id, new UnionComponent(un, ups));
                    Log($"   {un.Id}: union from=[{string.Join(", ", un.From)}]");
                    break;
                }
                case JoinStep j:
                {
                    var left = ResolveFrom(ports, j.Left, j.Id, "join.left");
                    var right = ResolveFrom(ports, j.Right, j.Id, "join.right");
                    RegisterPort(ports, j.Id, new JoinComponent(j, left, right));
                    Log($"   {j.Id}: join {j.Kind} left={j.Left} right={j.Right}");
                    break;
                }

                // ----- 1-in N-out ----------------------------------------------
                case MulticastStep mc:
                {
                    var u = ResolveFrom(ports, mc.From, mc.Id, "multicast.from");
                    var multi = new MulticastComponent(mc, u);
                    foreach (var (name, port) in multi.Outputs)
                        RegisterPort(ports, $"{mc.Id}:{name}", port);
                    Log($"   {mc.Id}: multicast outputs=[{string.Join(", ", mc.Outputs)}]");
                    break;
                }
                case ConditionalSplitStep cs:
                {
                    var u = ResolveFrom(ports, cs.From, cs.Id, "conditional_split.from");
                    var split = new ConditionalSplitComponent(cs, u, e => Compile(e, u.OutputSchema));
                    foreach (var (name, port) in split.Outputs)
                        RegisterPort(ports, $"{cs.Id}:{name}", port);
                    Log($"   {cs.Id}: conditional_split cases=[{string.Join(", ", cs.Cases.Select(c => c.Key))}]" +
                        (cs.DefaultCase is null ? "" : $" default={cs.DefaultCase}"));
                    break;
                }

                // ----- sinks ----------------------------------------------------
                case CsvWriteStep cw:
                {
                    var u = ResolveFrom(ports, cw.From, cw.Id, "csv.write.from");
                    sinks.Add((new CsvWriteComponent(cw, _params.Substitute(cw.Path)), u));
                    Log($"   {cw.Id}: csv.write {_params.Substitute(cw.Path)}");
                    break;
                }
                case JsonWriteStep jw:
                {
                    var u = ResolveFrom(ports, jw.From, jw.Id, "json.write.from");
                    sinks.Add((new JsonWriteComponent(jw, _params.Substitute(jw.Path)), u));
                    Log($"   {jw.Id}: json.write {_params.Substitute(jw.Path)}");
                    break;
                }
                case ArrowWriteStep aw:
                {
                    var u = ResolveFrom(ports, aw.From, aw.Id, "arrow.write.from");
                    sinks.Add((new ArrowWriteComponent(aw, _params.Substitute(aw.Path)), u));
                    Log($"   {aw.Id}: arrow.write {_params.Substitute(aw.Path)}");
                    break;
                }
                case BetlCountRowsStep cr:
                {
                    var u = ResolveFrom(ports, cr.From, cr.Id, "betl.count_rows.from");
                    sinks.Add((new BetlCountRowsSink(cr.Id, cr.ExpectedCount, _log), u));
                    Log($"   {cr.Id}: betl.count_rows from={cr.From}" + (cr.ExpectedCount is { } e ? $" expected={e}" : ""));
                    break;
                }
                case SqlUpsertStep up:
                {
                    var u = ResolveFrom(ports, up.From, up.Id, $"{up.ProviderHint}.upsert.from");
                    var (provider, dsn) = ResolveConnection(up.Connection, $"{up.ProviderHint}.upsert '{up.Id}'");
                    sinks.Add((new SqlUpsertComponent(up, provider, dsn), u));
                    Log($"   {up.Id}: {up.ProviderHint}.upsert table={up.Table}");
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

    private (ISqlProvider Provider, string Dsn) ResolveConnection(string name, string context)
    {
        if (_sqlRegistry is null)
            throw new BetlException(
                $"{context}: no SQL ConnectionRegistry was supplied to the Executor (CLI / host must register providers).");
        if (!_pipeline.Connections.TryGetValue(name, out var conn))
            throw new BetlException($"{context}: unknown connection '{name}' (declare it in `connections:`).");
        var provider = _sqlRegistry.Get(conn.Type);
        return (provider, _params.Substitute(conn.Dsn));
    }

    private static readonly bool ParallelEnabled = QueuedDataComponent.ParallelEnabledByDefault();
    private static readonly int ParallelDepth = QueuedDataComponent.DefaultDepth();
    private static readonly int ParallelBatchSize = QueuedDataComponent.DefaultBatchSize();

    private static void RegisterPort(Dictionary<string, IDataComponent> ports, string key, IDataComponent component)
    {
        if (ports.ContainsKey(key))
            throw new BetlException($"Duplicate port id '{key}' in this dataflow.");
        ports[key] = ParallelEnabled
            ? new QueuedDataComponent(component, ParallelDepth, ParallelBatchSize)
            : component;
    }

    private static IDataComponent ResolveFrom(
        Dictionary<string, IDataComponent> ports, string fromRef, string stepId, string keyName)
    {
        if (ports.TryGetValue(fromRef, out var c)) return c;
        throw new BetlException(
            $"Step '{stepId}' references unknown {keyName} '{fromRef}'. " +
            "(Upstream must be declared earlier; multi-output upstreams need the `step:port` form.)");
    }

    private ICompiledExpression Compile(Expression expr, Schema inputSchema)
    {
        if (expr is LiteralExpression lit && lit.Value is string s)
            expr = new LiteralExpression(_params.Substitute(s));
        return _engines.Compile(expr, inputSchema);
    }

    private static TimeSpan? ParseTimeout(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = TimeoutRegex().Match(s);
        if (!m.Success) throw new BetlException($"Invalid timeout '{s}' (expected like '30s', '5m', '2h', '250ms').");
        var n = int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" or "S" => TimeSpan.FromSeconds(n),
            "m" or "M" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            _ => throw new BetlException($"Invalid timeout unit in '{s}'."),
        };
    }

    [GeneratedRegex(@"^(\d+)(ms|s|S|m|M|h)$")]
    private static partial Regex TimeoutRegex();

    private void Log(string msg) => _log?.Invoke(msg);
}
