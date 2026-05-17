using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Conformance.Tests;

public sealed class Phase6Tests
{
    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase6", sub);

    [Fact]
    public void Ssis_PipelineComponent_full_shim_drives_PreExecute_and_ProcessInput()
    {
        var dir = FixtureDir("ssis-pipelinecomponent");
        var outPath = Path.Combine(Path.GetTempPath(), $"p6-{Guid.NewGuid():N}.csv");
        try
        {
            var pipeline = PipelineLoader.LoadFile(Path.Combine(dir, "pipeline.betl.yml"));
            var engines = new EngineRegistry().Register(new SsisExpressionEngine());
            var sql = new ConnectionRegistry().Register(new SqliteProvider());
            var ctx = ParameterContext.Build(pipeline, new Dictionary<string, string> { ["out"] = outPath });

            new Executor(pipeline, ctx, engines, sql).Run();

            var expected = File.ReadAllText(Path.Combine(dir, "expected.csv")).Replace("\r\n", "\n").TrimEnd('\n');
            var actual = File.ReadAllText(outPath).Replace("\r\n", "\n").TrimEnd('\n');
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
