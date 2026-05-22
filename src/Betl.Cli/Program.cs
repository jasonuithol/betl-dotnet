using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Cli;

internal static class Program
{
    private const string Usage = """
        betl — pure-.NET ETL runtime (Phase 1)

        Usage:
          betl run <pipeline.yml> [--param key=value ...]
          betl validate <pipeline.yml>

        Phase 1 supports: dataflow with csv.read, csv.write, filter, map,
        plus the `ssisexpr` and `literal` expression engines.
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine(Usage); return 64; }

        try
        {
            return args[0] switch
            {
                "run"      => Run(args[1..]),
                "validate" => Validate(args[1..]),
                "--help" or "-h" or "help" => Print(Usage, 0),
                _ => Print($"Unknown command '{args[0]}'.\n\n{Usage}", 64),
            };
        }
        catch (BetlException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            // Provider exceptions (Npgsql, Microsoft.Data.SqlClient,
            // HttpRequestException, IO errors, …) shouldn't surface as raw
            // unhandled-exception stack dumps to end users. Render them as
            // a one-line error: with the exception type for context, and
            // attach the full stack trace only when BETL_TRACE is set so
            // diagnosis is still possible without surprising the casual
            // user. Exit 1 stays consistent with the BetlException branch.
            Console.Error.WriteLine($"error ({ex.GetType().Name}): {ex.Message}");
            if (Environment.GetEnvironmentVariable("BETL_TRACE") is not null)
                Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static int Run(string[] argv)
    {
        if (argv.Length == 0) return Print($"Missing pipeline path.\n\n{Usage}", 64);

        var (path, cliParams) = ParseRunArgs(argv);
        var plugins = PluginRegistry.Discover();
        var pipeline = PipelineLoader.LoadFile(path, plugins.StepTypes);
        var parameters = ParameterContext.Build(pipeline, cliParams);
        var engines = BuildEngines();
        var sqlRegistry = BuildSqlRegistry();

        Console.Error.WriteLine($"betl: running '{pipeline.Name}'" +
            (plugins.StepTypes.Count > 0 ? $" ({plugins.StepTypes.Count} plugin step type(s) loaded)" : ""));
        new Executor(pipeline, parameters, engines, sqlRegistry, plugins,
            msg => Console.Error.WriteLine(msg)).Run();
        Console.Error.WriteLine("betl: done");
        return 0;
    }

    private static ConnectionRegistry BuildSqlRegistry() => new ConnectionRegistry()
        .Register(new SqliteProvider())
        .Register(new PostgresProvider())
        .Register(new MsSqlProvider());

    private static int Validate(string[] argv)
    {
        if (argv.Length == 0) return Print($"Missing pipeline path.\n\n{Usage}", 64);

        var path = argv[0];
        var yamlText = File.ReadAllText(path);

        // SchemaValidator (embedded JSON Schema) is intentionally NOT called
        // here. The schema is the original upstream contract circa Phase 1
        // and lags behind the typed parser by ~20 step types (var.set, audit,
        // multicast, conditional_split, xml.read, xlsx.*, dotnet.*, the
        // Phase 10 SQL extensions, generators…) plus `ssisexpr` as a lang.
        // Re-enabling it would reject most modern pipelines.
        //
        // The typed PipelineLoader is the authoritative source of truth —
        // it knows every supported step type, lang, and shape, and its
        // errors point at the exact YAML key. Plugin step types are honored
        // here so `validate` and `run` agree on what's legal.
        var plugins = PluginRegistry.Discover();
        var pipeline = PipelineLoader.Load(yamlText, plugins.StepTypes);
        Console.WriteLine(
            $"ok: pipeline '{pipeline.Name}' (betl: {pipeline.BetlVersion}), " +
            $"{pipeline.Steps.Count} top-level step(s).");
        return 0;
    }

    private static (string Path, Dictionary<string, string> Params) ParseRunArgs(string[] argv)
    {
        string? path = null;
        var cliParams = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--param")
            {
                if (i + 1 >= argv.Length) throw new BetlException("--param requires a value (key=value).");
                var kv = argv[++i];
                var eq = kv.IndexOf('=');
                if (eq < 0) throw new BetlException($"--param '{kv}' must be key=value.");
                cliParams[kv[..eq]] = kv[(eq + 1)..];
            }
            else if (path is null)
            {
                path = argv[i];
            }
            else
            {
                throw new BetlException($"Unexpected argument '{argv[i]}'.");
            }
        }

        if (path is null) throw new BetlException("Missing pipeline path.");
        return (path, cliParams);
    }

    private static EngineRegistry BuildEngines() => new EngineRegistry()
        .Register(new SsisExpressionEngine());

    private static int Print(string text, int code)
    {
        Console.Error.WriteLine(text);
        return code;
    }
}
