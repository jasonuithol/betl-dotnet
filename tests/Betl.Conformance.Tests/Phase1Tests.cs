using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Runtime;

namespace Betl.Conformance.Tests;

public sealed class Phase1Tests
{
    [Fact]
    public void OrdersCsvRoundtrip_filters_and_tags_then_matches_expected_output()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase1");
        var pipelinePath = Path.Combine(fixtureDir, "pipeline.betl.yml");
        var inputPath = Path.Combine(fixtureDir, "input.csv");
        var expectedPath = Path.Combine(fixtureDir, "expected.csv");

        var outPath = Path.Combine(Path.GetTempPath(),
            $"betl-phase1-{Guid.NewGuid():N}.csv");

        try
        {
            var pipeline = PipelineLoader.LoadFile(pipelinePath);
            var parameters = ParameterContext.Build(pipeline, new Dictionary<string, string>
            {
                ["in"] = inputPath,
                ["out"] = outPath,
            });
            var engines = new EngineRegistry().Register(new SsisExpressionEngine());

            new Executor(pipeline, parameters, engines).Run();

            var actual = File.ReadAllText(outPath).Replace("\r\n", "\n").TrimEnd('\n');
            var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
