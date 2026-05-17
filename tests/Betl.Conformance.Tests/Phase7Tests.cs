using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Conformance.Tests;

public sealed class Phase7Tests
{
    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase7", sub);

    private static void Run(string fixtureSub, Dictionary<string, string> @params)
    {
        var pipeline = PipelineLoader.LoadFile(Path.Combine(FixtureDir(fixtureSub), "pipeline.betl.yml"));
        var engines = new EngineRegistry().Register(new SsisExpressionEngine());
        var sql = new ConnectionRegistry().Register(new SqliteProvider());
        var ctx = ParameterContext.Build(pipeline, @params);
        new Executor(pipeline, ctx, engines, sql).Run();
    }

    private static void AssertFileMatches(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
        var actual = File.ReadAllText(actualPath).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Ssis_async_pipelinecomponent_aggregator_uses_PrimeOutput_AddRow()
    {
        var dir = FixtureDir("ssis-async");
        var outPath = Path.Combine(Path.GetTempPath(), $"p7-async-{Guid.NewGuid():N}.csv");
        try
        {
            Run("ssis-async", new Dictionary<string, string> { ["out"] = outPath });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Fact]
    public void Ssis_error_output_pipelinecomponent_routes_via_DirectErrorRow()
    {
        var dir = FixtureDir("ssis-error-output");
        var okPath = Path.Combine(Path.GetTempPath(), $"p7-ok-{Guid.NewGuid():N}.csv");
        var errPath = Path.Combine(Path.GetTempPath(), $"p7-err-{Guid.NewGuid():N}.csv");
        try
        {
            Run("ssis-error-output", new Dictionary<string, string>
            {
                ["out_ok"] = okPath,
                ["out_err"] = errPath,
            });
            AssertFileMatches(Path.Combine(dir, "expected-ok.csv"), okPath);
            AssertFileMatches(Path.Combine(dir, "expected-err.csv"), errPath);
        }
        finally
        {
            if (File.Exists(okPath)) File.Delete(okPath);
            if (File.Exists(errPath)) File.Delete(errPath);
        }
    }

    [Fact]
    public void Pivot_with_explicit_pivot_values_filters_and_orders_columns()
    {
        var dir = FixtureDir("pivot-values");
        var outPath = Path.Combine(Path.GetTempPath(), $"p7-piv-{Guid.NewGuid():N}.csv");
        try
        {
            Run("pivot-values", new Dictionary<string, string>
            {
                ["in"] = Path.Combine(dir, "input.csv"),
                ["out"] = outPath,
            });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Fact]
    public void HttpGet_fetches_json_from_local_listener_and_round_trips_to_csv()
    {
        var port = FindFreeTcpPort();
        var prefix = $"http://localhost:{port}/";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = System.Threading.Tasks.Task.Run(() =>
        {
            var ctx = listener.GetContext();
            var body = System.Text.Encoding.UTF8.GetBytes(
                """[{"id":1,"name":"alpha"},{"id":2,"name":"beta"},{"id":3,"name":"gamma"}]""");
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.Close();
        });

        var dir = FixtureDir("http-get");
        var fetchPath = Path.Combine(Path.GetTempPath(), $"p7-http-{Guid.NewGuid():N}.json");
        var outPath = Path.Combine(Path.GetTempPath(), $"p7-http-{Guid.NewGuid():N}.csv");
        try
        {
            Run("http-get", new Dictionary<string, string>
            {
                ["url"] = prefix + "data",
                ["fetch_to"] = fetchPath,
                ["out"] = outPath,
            });
            // http.get blocks until the response is read, so by here the server
            // task has handled the request and exited.
            _ = serverTask;
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally
        {
            listener.Stop();
            if (File.Exists(fetchPath)) File.Delete(fetchPath);
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    private static int FindFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
