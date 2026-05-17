using Betl.Core;

namespace Betl.Core.Tests;

public sealed class PipelineLoaderTests
{
    [Fact]
    public void LoadsTopLevelKeys()
    {
        var p = PipelineLoader.Load("""
            betl: 1
            name: simple
            pipeline:
              - id: df
                type: dataflow
                steps: []
            """);

        Assert.Equal(1, p.BetlVersion);
        Assert.Equal("simple", p.Name);
        Assert.Single(p.Steps);
        Assert.IsType<DataflowStep>(p.Steps[0]);
    }

    [Fact]
    public void RejectsUnknownTopLevelKey()
    {
        var ex = Assert.Throws<PipelineLoadException>(() => PipelineLoader.Load("""
            betl: 1
            name: x
            mystery_field: 42
            pipeline:
              - id: df
                type: dataflow
                steps: []
            """));
        Assert.Contains("mystery_field", ex.Message);
    }

    [Fact]
    public void RejectsLuaShorthand()
    {
        var ex = Assert.Throws<PipelineLoadException>(() => PipelineLoader.Load("""
            betl: 1
            name: x
            pipeline:
              - id: df
                type: dataflow
                steps:
                  - id: r
                    type: csv.read
                    path: foo.csv
                    schema:
                      - { name: a, type: int64 }
                  - id: f
                    type: filter
                    from: r
                    where: "row.a > 0"
            """));
        Assert.Contains("ssisexpr", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsLuaSteps()
    {
        var ex = Assert.Throws<PipelineLoadException>(() => PipelineLoader.Load("""
            betl: 1
            name: x
            pipeline:
              - id: df
                type: dataflow
                steps:
                  - id: r
                    type: csv.read
                    path: foo.csv
                    schema:
                      - { name: a, type: int64 }
                  - id: m
                    type: lua.map
                    from: r
                    script: "return row"
            """));
        Assert.Contains("not supported", ex.Message);
        Assert.Contains("lua", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesArrowDecimalAndTimestamp()
    {
        var p = PipelineLoader.Load("""
            betl: 1
            name: types
            pipeline:
              - id: df
                type: dataflow
                steps:
                  - id: r
                    type: csv.read
                    path: x.csv
                    schema:
                      - { name: price, type: "decimal128(12, 2)" }
                      - { name: ts,    type: "timestamp[us, UTC]" }
            """);
        var read = (CsvReadStep)((DataflowStep)p.Steps[0]).Steps[0];
        Assert.Equal("decimal128", read.Schema.Columns[0].ArrowType.Name);
        Assert.Equal("timestamp", read.Schema.Columns[1].ArrowType.Name);
    }
}
