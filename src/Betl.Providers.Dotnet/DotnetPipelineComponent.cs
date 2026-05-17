using Betl.Components;
using Betl.Core;
using Betl.Ssis.Shim.PipelineComponent;
using Microsoft.SqlServer.Dts.Pipeline;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using ShimPipelineComponent = Microsoft.SqlServer.Dts.Pipeline.PipelineComponent;

namespace Betl.Providers.Dotnet;

/// <summary>
/// Hosts a user-supplied PipelineComponent subclass. The user can derive from
/// either <see cref="BetlPipelineComponent"/> (simpler managed-only base —
/// recommended for new code) or <see cref="ShimPipelineComponent"/>
/// (full SSIS-compat shim — for porting existing SSIS source verbatim).
/// The driver compiles the source via Roslyn, detects which base, and drives
/// the appropriate lifecycle.
/// </summary>
public sealed class DotnetPipelineComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly Type _userType;
    private readonly bool _isSsis;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public DotnetPipelineComponent(DotnetPipelineComponentStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;
        OutputSchema = step.OutputSchema;

        if (step.Lang != "csharp")
            throw new BetlException($"dotnet.pipelinecomponent '{step.Id}': only 'csharp' supported (got '{step.Lang}').");

        var (type, matched) = DotnetCompiler.CompileAndFindAnyOf(
            [typeof(BetlPipelineComponent), typeof(ShimPipelineComponent)],
            step.Source,
            $"dotnet.pipelinecomponent '{step.Id}'");
        _userType = type;
        _isSsis = matched == typeof(ShimPipelineComponent);
    }

    public IEnumerable<Row> Stream()
    {
        var rows = _upstream.Stream().ToList();
        return _isSsis ? DriveSsis(rows) : DriveSimple(rows);
    }

    // ----- driver: simple BetlPipelineComponent (Phase 5 base) -----

    private IEnumerable<Row> DriveSimple(IReadOnlyList<Row> rows)
    {
        var instance = (BetlPipelineComponent)Activator.CreateInstance(_userType)!;
        var buffer = new BetlPipelineBuffer(rows, _upstream.OutputSchema, OutputSchema);
        try
        {
            instance.PreExecute();
            instance.ProcessInput(buffer);
            instance.PostExecute();
        }
        finally { instance.Cleanup(); }

        foreach (var values in buffer.DrainOutput())
            yield return new Row(OutputSchema, values);
    }

    // ----- driver: full Microsoft.SqlServer.Dts.Pipeline.* shim -----

    private IEnumerable<Row> DriveSsis(IReadOnlyList<Row> rows)
    {
        var instance = (ShimPipelineComponent)Activator.CreateInstance(_userType)!;
        var inSchema = _upstream.OutputSchema;
        var outSchema = OutputSchema;

        // Build column metadata. Per upstream BufferManager.cs, LineageID ==
        // column index in the SHARED (output-shape) buffer space. Input columns
        // present in the output by name inherit their output position as their
        // LineageID; absent input columns get -1 (unreachable in sync mode).
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

        instance.ComponentMetaData = metadata;
        instance.BufferManager = new BetlBufferManager();

        // Convert upstream rows to object?[][] in input-schema order.
        var inputArrays = new object?[rows.Count][];
        for (var i = 0; i < rows.Count; i++) inputArrays[i] = rows[i].Values;
        var inputNames = inSchema.Columns.Select(c => c.Name).ToArray();
        var outputNames = outSchema.Columns.Select(c => c.Name).ToArray();

        var buffer = new BetlSyncBuffer(inputArrays, inputNames, outputNames);

        try
        {
            instance.PreExecute();
            // PrimeOutput is meaningful only in async mode; pass the sync buffer for parity.
            instance.PrimeOutput(1, [output.ID], [buffer]);
            instance.ProcessInput(input.ID, buffer);
            instance.PostExecute();
        }
        finally { instance.Cleanup(); }

        foreach (var values in buffer.DrainOutput())
            yield return new Row(OutputSchema, values);
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
            // Lineage ID = output position of same-named column, else -1 (unreachable).
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

    private static DataType MapArrowToSsis(Apache.Arrow.Types.IArrowType t) => t switch
    {
        Apache.Arrow.Types.Int64Type   => DataType.DT_I8,
        Apache.Arrow.Types.Int32Type   => DataType.DT_I4,
        Apache.Arrow.Types.Int16Type   => DataType.DT_I2,
        Apache.Arrow.Types.Int8Type    => DataType.DT_I1,
        Apache.Arrow.Types.UInt64Type  => DataType.DT_UI8,
        Apache.Arrow.Types.UInt32Type  => DataType.DT_UI4,
        Apache.Arrow.Types.UInt16Type  => DataType.DT_UI2,
        Apache.Arrow.Types.UInt8Type   => DataType.DT_UI1,
        Apache.Arrow.Types.DoubleType  => DataType.DT_R8,
        Apache.Arrow.Types.FloatType   => DataType.DT_R4,
        Apache.Arrow.Types.BooleanType => DataType.DT_BOOL,
        Apache.Arrow.Types.StringType  => DataType.DT_WSTR,
        Apache.Arrow.Types.BinaryType  => DataType.DT_BYTES,
        Apache.Arrow.Types.Date32Type  => DataType.DT_DBDATE,
        Apache.Arrow.Types.TimestampType => DataType.DT_DBTIMESTAMP2,
        Apache.Arrow.Types.Decimal128Type => DataType.DT_NUMERIC,
        _ => DataType.DT_WSTR,
    };
}
