using Betl.Core;

namespace Betl.Ssis.Shim.PipelineComponent;

/// <summary>
/// A managed-only PipelineComponent base class with the same lifecycle order
/// as SSIS PipelineComponent (PreExecute → ProcessInput → PostExecute → Cleanup)
/// but a simplified API surface backed by <see cref="BetlPipelineBuffer"/>.
/// Suitable for new code and straightforward SSIS ports. The full
/// <c>Microsoft.SqlServer.Dts.Pipeline.*</c> compatibility shim from upstream
/// betl.native is a separate lift (deferred).
/// </summary>
public abstract class BetlPipelineComponent
{
    public virtual void PreExecute() { }
    public abstract void ProcessInput(BetlPipelineBuffer input);
    public virtual void PostExecute() { }
    public virtual void Cleanup() { }
}

/// <summary>
/// Row-cursor buffer over an input batch of rows plus an output collector.
/// Users call <see cref="NextRow"/> to advance, <c>GetXxx(idx)</c> to read input
/// values, and <see cref="AddOutputRow"/> to emit (with the output schema's
/// shape). For 1:1 transforms, a passthrough output is built automatically when
/// the user calls <see cref="AddOutputRowFromInput"/>.
/// </summary>
public sealed class BetlPipelineBuffer
{
    private readonly IReadOnlyList<Row> _input;
    private readonly Schema _inputSchema;
    private readonly Schema _outputSchema;
    private readonly List<object?[]> _output = new();
    private int _cursor = -1;

    public int Width => _inputSchema.Columns.Count;
    public int OutputWidth => _outputSchema.Columns.Count;

    public BetlPipelineBuffer(IReadOnlyList<Row> input, Schema inputSchema, Schema outputSchema)
    {
        _input = input;
        _inputSchema = inputSchema;
        _outputSchema = outputSchema;
    }

    /// <summary>Advance the cursor. Returns false past the last input row.</summary>
    public bool NextRow()
    {
        _cursor++;
        return _cursor < _input.Count;
    }

    public bool IsNull(int idx) => _input[_cursor].Values[idx] is null;

    public long   GetInt64(int idx)  => Convert.ToInt64(_input[_cursor].Values[idx]);
    public int    GetInt32(int idx)  => Convert.ToInt32(_input[_cursor].Values[idx]);
    public double GetDouble(int idx) => Convert.ToDouble(_input[_cursor].Values[idx]);
    public float  GetSingle(int idx) => Convert.ToSingle(_input[_cursor].Values[idx]);
    public bool   GetBoolean(int idx) => Convert.ToBoolean(_input[_cursor].Values[idx]);
    public string? GetString(int idx) => _input[_cursor].Values[idx]?.ToString();
    public object? GetValue(int idx)  => _input[_cursor].Values[idx];

    public int InputIndex(string name) => _inputSchema.IndexOf(name);
    public int OutputIndex(string name) => _outputSchema.IndexOf(name);

    /// <summary>Emit one output row from raw values.</summary>
    public void AddOutputRow(object?[] values)
    {
        if (values.Length != _outputSchema.Columns.Count)
            throw new BetlException(
                $"BetlPipelineBuffer.AddOutputRow: expected {_outputSchema.Columns.Count} values, got {values.Length}.");
        _output.Add(values);
    }

    /// <summary>Convenience: emit the current input row directly (must match output schema width).</summary>
    public void AddOutputRowFromInput()
    {
        var src = _input[_cursor].Values;
        if (src.Length != _outputSchema.Columns.Count)
            throw new BetlException(
                $"BetlPipelineBuffer.AddOutputRowFromInput: input width {src.Length} != output width {_outputSchema.Columns.Count}.");
        var copy = new object?[src.Length];
        Array.Copy(src, copy, src.Length);
        _output.Add(copy);
    }

    public IReadOnlyList<object?[]> DrainOutput() => _output;
}
