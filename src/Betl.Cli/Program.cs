using Betl.Core;
using Betl.Expressions.SsisExpr;
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
    }

    private static int Run(string[] argv)
    {
        if (argv.Length == 0) return Print($"Missing pipeline path.\n\n{Usage}", 64);

        var (path, cliParams) = ParseRunArgs(argv);
        var pipeline = PipelineLoader.LoadFile(path);
        var parameters = ParameterContext.Build(pipeline, cliParams);
        var engines = BuildEngines();

        Console.Error.WriteLine($"betl: running '{pipeline.Name}'");
        new Executor(pipeline, parameters, engines, msg => Console.Error.WriteLine(msg)).Run();
        Console.Error.WriteLine("betl: done");
        return 0;
    }

    private static int Validate(string[] argv)
    {
        if (argv.Length == 0) return Print($"Missing pipeline path.\n\n{Usage}", 64);

        var path = argv[0];
        var yamlText = File.ReadAllText(path);

        var schemaErrors = SchemaValidator.Validate(yamlText);
        if (schemaErrors.Count > 0)
        {
            Console.Error.WriteLine($"schema errors in '{path}':");
            foreach (var e in schemaErrors) Console.Error.WriteLine($"  - {e}");
            return 1;
        }

        // Typed-parse adds the semantic checks the JSON Schema cannot express
        // (Lua rejection, unknown step bodies, etc.).
        var pipeline = PipelineLoader.Load(yamlText);
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
