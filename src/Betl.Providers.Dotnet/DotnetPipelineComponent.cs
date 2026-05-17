using Apache.Arrow.Types;
using Betl.Components;
using Betl.Core;
using Betl.Ssis.Shim.PipelineComponent;
using Microsoft.SqlServer.Dts.Pipeline;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using ShimPipelineComponent = Microsoft.SqlServer.Dts.Pipeline.PipelineComponent;

namespace Betl.Providers.Dotnet;

/// <summary>
/// Hosts a user-supplied PipelineComponent subclass. The user can derive from
/// either <see cref="BetlPipelineComponent"/> (simpler managed-only base) or
/// <see cref="ShimPipelineComponent"/> (full SSIS-compat shim). Supports
/// three modes: sync (default), async (separate input/output buffers, AddRow),
/// and sync + error_output (DirectErrorRow routes rows to a second port).
///
/// Exposes <see cref="Outputs"/> — always contains the default "out" port,
/// plus "error_out" when <c>error_output: true</c>. The executor registers
/// each by name.
/// </summary>
public sealed class DotnetPipelineComponent
{
    private readonly DotnetPipelineComponentStep _step;
    private readonly IDataComponent _upstream;
    private readonly Type _userType;
    private readonly bool _isSsis;

    /// <summary>Default + optional error_out ports.</summary>
    public IReadOnlyList<KeyValuePair<string, IDataComponent>> Outputs { get; }

    public DotnetPipelineComponent(DotnetPipelineComponentStep step, IDataComponent upstream)
    {
        _step = step;
        _upstream = upstream;

        if (step.Lang != "csharp")
            throw new BetlException($"dotnet.pipelinecomponent '{step.Id}': only 'csharp' supported (got '{step.Lang}').");

        var (type, matched) = DotnetCompiler.CompileAndFindAnyOf(
            [typeof(BetlPipelineComponent), typeof(ShimPipelineComponent)],
            step.Source,
            $"dotnet.pipelinecomponent '{step.Id}'");
        _userType = type;
        _isSsis = matched == typeof(ShimPipelineComponent);

        if (step.Async && step.ErrorOutput)
            throw new BetlException(
                $"dotnet.pipelinecomponent '{step.Id}': async + error_output is not supported.");

        if (step.Async && !_isSsis)
            throw new BetlException(
                $"dotnet.pipelinecomponent '{step.Id}': async mode requires the SSIS PipelineComponent base " +
                "(Microsoft.SqlServer.Dts.Pipeline.PipelineComponent), not BetlPipelineComponent.");

        // Lazy so both ports (out + error_out) share a single drive of the
        // user's ProcessInput. First port to call Stream() materialises.
        var lazyResult = new Lazy<(IReadOnlyList<Row> Normal, IReadOnlyList<Row> Errors)>(Drive);

        var outs = new List<KeyValuePair<string, IDataComponent>>
        {
            KeyValuePair.Create("out",
                (IDataComponent)new ResultPort(step.Id, step.OutputSchema, () => lazyResult.Value.Normal)),
        };
        if (step.ErrorOutput)
        {
            var errSchema = BuildErrorSchema(step.OutputSchema);
            outs.Add(KeyValuePair.Create("error_out",
                (IDataComponent)new ResultPort($"{step.Id}:error_out", errSchema, () => lazyResult.Value.Errors)));
        }
        Outputs = outs;
    }

    private static Schema BuildErrorSchema(Schema baseSchema)
    {
        var cols = new List<Column>(baseSchema.Columns.Count + 2);
        cols.AddRange(baseSchema.Columns);
        cols.Add(new Column { Name = "ErrorCode",   ArrowType = Int32Type.Default, Nullable = false });
        cols.Add(new Column { Name = "ErrorColumn", ArrowType = Int32Type.Default, Nullable = false });
        return new Schema { Columns = cols };
    }

    private sealed class ResultPort(string id, Schema schema, Func<IEnumerable<Row>> source) : IDataComponent
    {
        public string Id { get; } = id;
        public Schema OutputSchema { get; } = schema;
        public IEnumerable<Row> Stream() => source();
    }

    // ----- single drive that fills both ports -----

    private (IReadOnlyList<Row> Normal, IReadOnlyList<Row> Errors) Drive()
    {
        var rows = _upstream.Stream().ToList();
        if (!_isSsis) return (DriveSimple(rows), Array.Empty<Row>());
        if (_step.Async) return (DriveAsync(rows), Array.Empty<Row>());
        return DriveSyncSsis(rows);
    }

    private IReadOnlyList<Row> DriveSimple(IReadOnlyList<Row> rows)
    {
        var instance = (BetlPipelineComponent)Activator.CreateInstance(_userType)!;
        var buffer = new BetlPipelineBuffer(rows, _upstream.OutputSchema, _step.OutputSchema);
        try
        {
            instance.PreExecute();
            instance.ProcessInput(buffer);
            instance.PostExecute();
        }
        finally { instance.Cleanup(); }

        return buffer.DrainOutput()
            .Select(values => new Row(_step.OutputSchema, values))
            .ToList();
    }

    private (IReadOnlyList<Row> Normal, IReadOnlyList<Row> Errors) DriveSyncSsis(IReadOnlyList<Row> rows)
    {
        var instance = (ShimPipelineComponent)Activator.CreateInstance(_userType)!;
        var inSchema = _upstream.OutputSchema;
        var outSchema = _step.OutputSchema;

        var (inputId, outputId, metadata) = BuildSsisMetadata(inSchema, outSchema);
        instance.ComponentMetaData = metadata;
        instance.BufferManager = new BetlBufferManager();

        var inputArrays = ToArrays(rows);
        var inputNames = inSchema.Columns.Select(c => c.Name).ToArray();
        var outputNames = outSchema.Columns.Select(c => c.Name).ToArray();

        var buffer = new BetlSyncBuffer(inputArrays, inputNames, outputNames);

        try
        {
            instance.PreExecute();
            instance.PrimeOutput(1, [outputId], [buffer]);
            instance.ProcessInput(inputId, buffer);
            instance.PostExecute();
        }
        finally { instance.Cleanup(); }

        var normal = buffer.DrainOutput()
            .Select(values => new Row(outSchema, values))
            .ToList();

        if (!_step.ErrorOutput) return (normal, Array.Empty<Row>());

        var errSchema = BuildErrorSchema(outSchema);
        var errors = buffer.DrainErrors()
            .Select(e =>
            {
                var v = new object?[errSchema.Columns.Count];
                Array.Copy(e.Values, 0, v, 0, e.Values.Length);
                v[^2] = e.ErrorCode;
                v[^1] = e.ErrorColumn;
                return new Row(errSchema, v);
            })
            .ToList();

        return (normal, errors);
    }

    private IReadOnlyList<Row> DriveAsync(IReadOnlyList<Row> rows)
    {
        var instance = (ShimPipelineComponent)Activator.CreateInstance(_userType)!;
        var inSchema = _upstream.OutputSchema;
        var outSchema = _step.OutputSchema;

        var (inputId, outputId, _) = BuildSsisMetadata(inSchema, outSchema);
        var meta = BuildSsisMetadata(inSchema, outSchema).Metadata;
        instance.ComponentMetaData = meta;
        instance.BufferManager = new BetlBufferManager();

        var inputArrays = ToArrays(rows);
        var inputNames = inSchema.Columns.Select(c => c.Name).ToArray();
        var outputNames = outSchema.Columns.Select(c => c.Name).ToArray();

        var inputBuffer = new BetlAsyncInputBuffer(inputArrays, inputNames);
        var outputBuffer = new BetlAsyncOutputBuffer(outputNames);

        try
        {
            instance.PreExecute();
            instance.PrimeOutput(1, [outputId], [outputBuffer]);
            instance.ProcessInput(inputId, inputBuffer);
            instance.PostExecute();
        }
        finally { instance.Cleanup(); }

        return outputBuffer.DrainOutput()
            .Select(values => new Row(outSchema, values))
            .ToList();
    }

    // ----- shared helpers -----

    private static object?[][] ToArrays(IReadOnlyList<Row> rows)
    {
        var a = new object?[rows.Count][];
        for (var i = 0; i < rows.Count; i++) a[i] = rows[i].Values;
        return a;
    }

    private static (int InputId, int OutputId, BetlComponentMetaData Metadata) BuildSsisMetadata(
        Schema inSchema, Schema outSchema)
    {
        var outputCols = BuildOutputColumns(outSchema);
        var inputCols = BuildInputColumns(inSchema, outSchema);

        var input = new BetlInput
        {
            ID = 0,
            Buffer = 0,
            InputColumnCollection = new BetlInputColumnCollection(inputCols),
        };
        var output = new BetlOutput
        {
            ID = 1,
            Buffer = 0,
            OutputColumnCollection = new BetlOutputColumnCollection(outputCols),
        };
        var metadata = new BetlComponentMetaData
        {
            InputCollection = new BetlInputCollection([input]),
            OutputCollection = new BetlOutputCollection([output]),
        };
        return (input.ID, output.ID, metadata);
    }

    private static List<BetlOutputColumn> BuildOutputColumns(Schema outSchema)
    {
        var cols = new List<BetlOutputColumn>(outSchema.Columns.Count);
        for (var i = 0; i < outSchema.Columns.Count; i++)
        {
            var c = outSchema.Columns[i];
            cols.Add(new BetlOutputColumn
            {
                ID = i,
                LineageID = i,
                Name = c.Name,
                DataType = MapArrowToSsis(c.ArrowType),
            });
        }
        return cols;
    }

    private static List<BetlInputColumn> BuildInputColumns(Schema inSchema, Schema outSchema)
    {
        var cols = new List<BetlInputColumn>(inSchema.Columns.Count);
        for (var i = 0; i < inSchema.Columns.Count; i++)
        {
            var c = inSchema.Columns[i];
            var lineage = outSchema.IndexOf(c.Name);
            cols.Add(new BetlInputColumn
            {
                ID = i,
                LineageID = lineage,
                Name = c.Name,
                DataType = MapArrowToSsis(c.ArrowType),
            });
        }
        return cols;
    }

    private static DataType MapArrowToSsis(IArrowType t) => t switch
    {
        Int64Type   => DataType.DT_I8,
        Int32Type   => DataType.DT_I4,
        Int16Type   => DataType.DT_I2,
        Int8Type    => DataType.DT_I1,
        UInt64Type  => DataType.DT_UI8,
        UInt32Type  => DataType.DT_UI4,
        UInt16Type  => DataType.DT_UI2,
        UInt8Type   => DataType.DT_UI1,
        DoubleType  => DataType.DT_R8,
        FloatType   => DataType.DT_R4,
        BooleanType => DataType.DT_BOOL,
        StringType  => DataType.DT_WSTR,
        BinaryType  => DataType.DT_BYTES,
        Date32Type  => DataType.DT_DBDATE,
        TimestampType => DataType.DT_DBTIMESTAMP2,
        Decimal128Type => DataType.DT_NUMERIC,
        _ => DataType.DT_WSTR,
    };
}
