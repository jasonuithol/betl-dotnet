using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;
using Betl.Core;
using BetlSchema = Betl.Core.Schema;
using BetlColumn = Betl.Core.Column;
using ArrowSchema = Apache.Arrow.Schema;

namespace Betl.Components;

// ============================================================
// arrow.read — Apache Arrow IPC file source
// ============================================================
public sealed class ArrowReadComponent : IDataComponent
{
    private readonly string _path;
    private BetlSchema? _outputSchemaLazy;

    public string Id { get; }
    public BetlSchema OutputSchema => _outputSchemaLazy ??= ReadSchema();

    public ArrowReadComponent(ArrowReadStep step, string resolvedPath)
    {
        Id = step.Id;
        _path = resolvedPath;
    }

    private BetlSchema ReadSchema()
    {
        using var stream = File.OpenRead(_path);
        using var reader = new ArrowFileReader(stream);
        var arrowSchema = reader.Schema;
        return new BetlSchema
        {
            Columns = arrowSchema.FieldsList.Select(f => new BetlColumn
            {
                Name = f.Name,
                ArrowType = f.DataType,
                Nullable = f.IsNullable,
            }).ToList(),
        };
    }

    public IEnumerable<Row> Stream()
    {
        using var stream = File.OpenRead(_path);
        using var reader = new ArrowFileReader(stream);
        var schema = OutputSchema;

        while (true)
        {
            var batch = reader.ReadNextRecordBatch();
            if (batch is null) yield break;
            using (batch)
            {
                for (var r = 0; r < batch.Length; r++)
                {
                    var values = new object?[schema.Columns.Count];
                    for (var c = 0; c < schema.Columns.Count; c++)
                        values[c] = ArrowCodec.GetValue(batch.Column(c), r);
                    yield return new Row(schema, values);
                }
            }
        }
    }
}

// ============================================================
// arrow.write — Apache Arrow IPC file sink
// ============================================================
public sealed class ArrowWriteComponent : ISink
{
    private readonly string _path;
    private readonly int _batchSize;

    public string Id { get; }

    public ArrowWriteComponent(ArrowWriteStep step, string resolvedPath)
    {
        Id = step.Id;
        _path = resolvedPath;
        _batchSize = step.BatchSize > 0 ? step.BatchSize : 1024;
    }

    public void Drain(IDataComponent input)
    {
        var schema = input.OutputSchema;
        var arrowFields = schema.Columns.Select(c =>
            new Field(c.Name, c.ArrowType, c.Nullable)).ToList();
        var arrowSchema = new Apache.Arrow.Schema(arrowFields, null);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(_path);
        using var writer = new ArrowFileWriter(fs, arrowSchema);
        writer.WriteStart();

        var buffer = new List<Row>(_batchSize);
        foreach (var row in input.Stream())
        {
            buffer.Add(row);
            if (buffer.Count >= _batchSize) { FlushBatch(writer, schema, buffer); buffer.Clear(); }
        }
        if (buffer.Count > 0) FlushBatch(writer, schema, buffer);
        writer.WriteEnd();
    }

    private static void FlushBatch(ArrowFileWriter writer, BetlSchema schema, List<Row> rows)
    {
        var allocator = new NativeMemoryAllocator();
        var arrays = new IArrowArray[schema.Columns.Count];
        for (var c = 0; c < schema.Columns.Count; c++)
            arrays[c] = ArrowCodec.BuildArray(schema.Columns[c].ArrowType, rows, c, allocator);
        using var batch = new RecordBatch(
            new ArrowSchema(schema.Columns.Select(col =>
                new Field(col.Name, col.ArrowType, col.Nullable)).ToList(), null),
            arrays,
            rows.Count);
        writer.WriteRecordBatch(batch);
    }
}

// ============================================================
// Row <-> Arrow column codec
// ============================================================
internal static class ArrowCodec
{
    public static object? GetValue(IArrowArray col, int row)
    {
        if (col is null) return null;
        return col switch
        {
            Int64Array a   => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            Int32Array a   => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            Int16Array a   => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            Int8Array a    => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            UInt64Array a  => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            UInt32Array a  => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            UInt16Array a  => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            UInt8Array a   => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            DoubleArray a  => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            FloatArray a   => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            StringArray a  => a.GetString(row),
            BooleanArray a => a.IsNull(row) ? null : (object)a.GetValue(row)!,
            Date32Array a  => a.IsNull(row) ? null : (object)DateOnly.FromDateTime(a.GetDateTime(row)!.Value),
            _ => throw new BetlException($"arrow read not yet implemented for {col.GetType().Name}."),
        };
    }

    public static IArrowArray BuildArray(IArrowType type, IReadOnlyList<Row> rows, int colIdx, MemoryAllocator allocator)
    {
        switch (type)
        {
            case Int64Type:    { var b = new Int64Array.Builder(); foreach (var r in rows) AppendInt64(b, r.Values[colIdx]); return b.Build(allocator); }
            case Int32Type:    { var b = new Int32Array.Builder(); foreach (var r in rows) AppendInt32(b, r.Values[colIdx]); return b.Build(allocator); }
            case DoubleType:   { var b = new DoubleArray.Builder(); foreach (var r in rows) AppendDouble(b, r.Values[colIdx]); return b.Build(allocator); }
            case FloatType:    { var b = new FloatArray.Builder(); foreach (var r in rows) AppendFloat(b, r.Values[colIdx]); return b.Build(allocator); }
            case StringType:   { var b = new StringArray.Builder(); foreach (var r in rows) AppendString(b, r.Values[colIdx]); return b.Build(allocator); }
            case BooleanType:  { var b = new BooleanArray.Builder(); foreach (var r in rows) AppendBool(b, r.Values[colIdx]); return b.Build(allocator); }
            case Date32Type:   { var b = new Date32Array.Builder(); foreach (var r in rows) AppendDate32(b, r.Values[colIdx]); return b.Build(allocator); }
            default:
                throw new BetlException($"arrow write not yet implemented for {type.Name}.");
        }
    }

    static void AppendInt64(Int64Array.Builder b, object? v)   { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture)); }
    static void AppendInt32(Int32Array.Builder b, object? v)   { if (v is null) b.AppendNull(); else b.Append(Convert.ToInt32(v, CultureInfo.InvariantCulture)); }
    static void AppendDouble(DoubleArray.Builder b, object? v) { if (v is null) b.AppendNull(); else b.Append(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
    static void AppendFloat(FloatArray.Builder b, object? v)   { if (v is null) b.AppendNull(); else b.Append(Convert.ToSingle(v, CultureInfo.InvariantCulture)); }
    static void AppendString(StringArray.Builder b, object? v) { if (v is null) b.AppendNull(); else b.Append(v.ToString()); }
    static void AppendBool(BooleanArray.Builder b, object? v)  { if (v is null) b.AppendNull(); else b.Append(Convert.ToBoolean(v)); }
    static void AppendDate32(Date32Array.Builder b, object? v)
    {
        if (v is null) { b.AppendNull(); return; }
        var d = v switch
        {
            DateOnly d2 => d2,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => throw new BetlException($"date32 builder cannot accept {v.GetType().Name}."),
        };
        b.Append(d);
    }
}
