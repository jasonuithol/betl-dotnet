/* PipelineBuffer — the row-batch façade SSIS PipelineComponents
 * use to read/write rows during ProcessInput.
 *
 * Lifted from upstream betl.linux (providers/betl-dotnet/shim/
 * pipelinecomponent/PipelineBuffer.cs) — the abstract base class is
 * preserved verbatim so SSIS-derived source compiles unchanged.
 * The concrete implementations below are rewritten for the managed
 * betl.dotnet world: they read/write our object?[] Row representation
 * directly instead of marshalling across the Arrow C Data Interface
 * the way upstream does.
 *
 * Indexing convention (matches upstream): column indices are POSITIONS
 * in the output_schema. BufferManager.FindColumnByLineageID returns
 * the same number.
 */

using System;
using Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace Microsoft.SqlServer.Dts.Pipeline;

/* The base type has every accessor declared so SSIS-derived source
 * compiles against it. Sync transforms use one concrete buffer with
 * all of them; async transforms get two — an input view that only
 * implements Get* / NextRow and an output view that only implements
 * Set* / AddRow. Unsupported calls throw with a clear message. */
public abstract class PipelineBuffer
{
    public virtual long  RowCount    => throw NotSupported("RowCount");
    public virtual bool  EndOfRowset => throw NotSupported("EndOfRowset");
    public virtual int   ColumnCount => throw NotSupported("ColumnCount");
    public virtual bool  NextRow()                  => throw NotSupported("NextRow");
    public virtual void  AddRow()                   => throw NotSupported("AddRow");
    public virtual void  SetEndOfRowset()           { }    /* tolerated as a hint */

    /* Marks the current row for the error output stream. rowIdx is ignored
     * (the cursor's current row is always the one being processed); the
     * parameter is here for SSIS API parity. Only meaningful on a sync
     * buffer in error_output mode; throws elsewhere. */
    public virtual void DirectErrorRow(int rowIdx, int errorCode, int errorColumn)
        => throw NotSupported("DirectErrorRow");

    public virtual bool  IsNull   (int c)           => throw NotSupported("IsNull");
    public virtual void  SetNull  (int c)           => throw NotSupported("SetNull");

    public virtual long      GetInt64 (int c)               => throw NotSupported("GetInt64");
    public virtual void      SetInt64 (int c, long v)       => throw NotSupported("SetInt64");
    public virtual double    GetDouble(int c)               => throw NotSupported("GetDouble");
    public virtual void      SetDouble(int c, double v)     => throw NotSupported("SetDouble");
    public virtual bool      GetBoolean(int c)              => throw NotSupported("GetBoolean");
    public virtual void      SetBoolean(int c, bool v)      => throw NotSupported("SetBoolean");
    public virtual string    GetString(int c)               => throw NotSupported("GetString");
    public virtual void      SetString(int c, string v)     => throw NotSupported("SetString");
    public virtual byte[]    GetBytes(int c)                => throw NotSupported("GetBytes");
    public virtual void      SetBytes(int c, byte[] v)      => throw NotSupported("SetBytes");
    public virtual DateTime  GetDate(int c)                 => throw NotSupported("GetDate");
    public virtual void      SetDate(int c, DateTime v)     => throw NotSupported("SetDate");
    public virtual Guid      GetGuid(int c)                 => throw NotSupported("GetGuid");
    public virtual void      SetGuid(int c, Guid v)         => throw NotSupported("SetGuid");
    public virtual decimal   GetDecimal(int c)              => throw NotSupported("GetDecimal");
    public virtual void      SetDecimal(int c, decimal v)   => throw NotSupported("SetDecimal");
    public virtual TimeSpan  GetTime(int c)                 => throw NotSupported("GetTime");
    public virtual void      SetTime(int c, TimeSpan v)     => throw NotSupported("SetTime");
    public virtual DateTimeOffset GetDateTimeOffset(int c)  => throw NotSupported("GetDateTimeOffset");
    public virtual void      SetDateTimeOffset(int c, DateTimeOffset v)
        => throw NotSupported("SetDateTimeOffset");

    /* Narrow accessors route through the widened ones. */
    public int    GetInt32 (int c) => (int)   GetInt64(c);
    public short  GetInt16 (int c) => (short) GetInt64(c);
    public sbyte  GetSByte (int c) => (sbyte) GetInt64(c);
    public uint   GetUInt32(int c) => (uint)  GetInt64(c);
    public ushort GetUInt16(int c) => (ushort)GetInt64(c);
    public byte   GetByte  (int c) => (byte)  GetInt64(c);
    public float  GetSingle(int c) => (float) GetDouble(c);

    public void   SetInt32 (int c, int v)    => SetInt64(c, v);
    public void   SetInt16 (int c, short v)  => SetInt64(c, v);
    public void   SetSByte (int c, sbyte v)  => SetInt64(c, v);
    public void   SetUInt32(int c, uint v)   => SetInt64(c, v);
    public void   SetUInt16(int c, ushort v) => SetInt64(c, v);
    public void   SetByte  (int c, byte v)   => SetInt64(c, v);
    public void   SetSingle(int c, float v)  => SetDouble(c, v);

    private static Exception NotSupported(string op) =>
        new BetlPipelineException(
            "PipelineBuffer: " + op + " is not supported in this buffer's mode "
            + "(input vs output vs sync).");
}

// ============================================================================
// Managed concrete implementations (rewritten for managed Row backing —
// upstream's Arrow C-ABI versions don't apply in our pure .NET host).
// ============================================================================

/// <summary>
/// Sync-mode buffer: a single cursor over input rows. The "staging" cell
/// values are object?[] of <c>outputCols</c> width. Same-named input columns
/// pre-populate cells; other cells start null. User Get* reads, Set* writes,
/// NextRow advances and commits the prior row to the output collection.
/// </summary>
public class BetlSyncBuffer : PipelineBuffer
{
    private readonly object?[][] _inputRows;
    private readonly string[]    _inputNames;
    private readonly string[]    _outputNames;
    private readonly int[]       _inputForOutput;   // outputColIdx -> inputColIdx (-1 if none)

    private readonly List<object?[]> _outRows = new();
    private object?[]? _stage;   // current staged row (output-shape)
    private long _cursor = -1;

    public override long RowCount    => _inputRows.LongLength;
    public override bool EndOfRowset => _cursor >= _inputRows.LongLength;
    public override int  ColumnCount => _outputNames.Length;

    public BetlSyncBuffer(object?[][] inputRows, string[] inputNames, string[] outputNames)
    {
        _inputRows = inputRows;
        _inputNames = inputNames;
        _outputNames = outputNames;

        _inputForOutput = new int[outputNames.Length];
        for (var o = 0; o < outputNames.Length; o++)
        {
            _inputForOutput[o] = -1;
            for (var i = 0; i < inputNames.Length; i++)
            {
                if (string.Equals(inputNames[i], outputNames[o], StringComparison.OrdinalIgnoreCase))
                {
                    _inputForOutput[o] = i;
                    break;
                }
            }
        }
    }

    public override bool NextRow()
    {
        CommitStaged();
        _cursor++;
        if (_cursor >= _inputRows.LongLength) { _stage = null; return false; }

        // Pre-populate output-shape staging row from same-named input columns.
        var inRow = _inputRows[_cursor];
        _stage = new object?[_outputNames.Length];
        for (var o = 0; o < _outputNames.Length; o++)
        {
            var i = _inputForOutput[o];
            if (i >= 0) _stage[o] = inRow[i];
        }
        return true;
    }

    private void CommitStaged()
    {
        if (_stage is not null) _outRows.Add(_stage);
    }

    /// <summary>Called by the driver after ProcessInput returns to flush the final staged row.</summary>
    internal IReadOnlyList<object?[]> DrainOutput()
    {
        CommitStaged();
        _stage = null;
        return _outRows;
    }

    // --- typed accessors (all read/write the staging row) -------------------

    public override bool   IsNull(int c)               => _stage![c] is null;
    public override void   SetNull(int c)              { _stage![c] = null; }
    public override long   GetInt64(int c)             => Convert.ToInt64(_stage![c]);
    public override void   SetInt64(int c, long v)     { _stage![c] = v; }
    public override double GetDouble(int c)            => Convert.ToDouble(_stage![c]);
    public override void   SetDouble(int c, double v)  { _stage![c] = v; }
    public override bool   GetBoolean(int c)           => Convert.ToBoolean(_stage![c]);
    public override void   SetBoolean(int c, bool v)   { _stage![c] = v; }
    public override string GetString(int c)            => _stage![c]?.ToString() ?? "";
    public override void   SetString(int c, string v)  { _stage![c] = v; }
    public override byte[] GetBytes(int c)             => (byte[])_stage![c]!;
    public override void   SetBytes(int c, byte[] v)   { _stage![c] = v; }
    public override DateTime GetDate(int c)            => (DateTime)_stage![c]!;
    public override void   SetDate(int c, DateTime v)  { _stage![c] = v; }
    public override Guid   GetGuid(int c)              => (Guid)_stage![c]!;
    public override void   SetGuid(int c, Guid v)      { _stage![c] = v; }
    public override decimal GetDecimal(int c)          => Convert.ToDecimal(_stage![c]);
    public override void   SetDecimal(int c, decimal v) { _stage![c] = v; }
    public override TimeSpan GetTime(int c)            => (TimeSpan)_stage![c]!;
    public override void   SetTime(int c, TimeSpan v)  { _stage![c] = v; }
    public override DateTimeOffset GetDateTimeOffset(int c)
        => (DateTimeOffset)_stage![c]!;
    public override void   SetDateTimeOffset(int c, DateTimeOffset v) { _stage![c] = v; }
}

/// <summary>Async-mode INPUT buffer: cursor reads only.</summary>
public class BetlAsyncInputBuffer : PipelineBuffer
{
    private readonly object?[][] _rows;
    private readonly string[]    _names;
    private long _cursor = -1;

    public override long RowCount    => _rows.LongLength;
    public override bool EndOfRowset => _cursor >= _rows.LongLength;
    public override int  ColumnCount => _names.Length;

    public BetlAsyncInputBuffer(object?[][] rows, string[] columnNames)
    {
        _rows = rows;
        _names = columnNames;
    }

    public override bool NextRow()
    {
        _cursor++;
        return _cursor < _rows.LongLength;
    }

    public override bool   IsNull(int c)               => _rows[_cursor][c] is null;
    public override long   GetInt64(int c)             => Convert.ToInt64(_rows[_cursor][c]);
    public override double GetDouble(int c)            => Convert.ToDouble(_rows[_cursor][c]);
    public override bool   GetBoolean(int c)           => Convert.ToBoolean(_rows[_cursor][c]);
    public override string GetString(int c)            => _rows[_cursor][c]?.ToString() ?? "";
    public override byte[] GetBytes(int c)             => (byte[])_rows[_cursor][c]!;
    public override DateTime GetDate(int c)            => (DateTime)_rows[_cursor][c]!;
    public override Guid   GetGuid(int c)              => (Guid)_rows[_cursor][c]!;
    public override decimal GetDecimal(int c)          => Convert.ToDecimal(_rows[_cursor][c]);
    public override TimeSpan GetTime(int c)            => (TimeSpan)_rows[_cursor][c]!;
    public override DateTimeOffset GetDateTimeOffset(int c) => (DateTimeOffset)_rows[_cursor][c]!;
}

/// <summary>Async-mode OUTPUT buffer: AddRow + Set* on the current row.</summary>
public class BetlAsyncOutputBuffer : PipelineBuffer
{
    private readonly string[] _names;
    private readonly List<object?[]> _rows = new();
    private object?[]? _current;

    public override int  ColumnCount => _names.Length;
    public override long RowCount    => _rows.Count;

    public BetlAsyncOutputBuffer(string[] columnNames) { _names = columnNames; }

    public override void AddRow()
    {
        _current = new object?[_names.Length];
        _rows.Add(_current);
    }

    public override void SetEndOfRowset() { /* no-op for output */ }

    internal IReadOnlyList<object?[]> DrainOutput() => _rows;

    public override bool   IsNull(int c)               => _current![c] is null;
    public override void   SetNull(int c)              { _current![c] = null; }
    public override void   SetInt64(int c, long v)     { _current![c] = v; }
    public override void   SetDouble(int c, double v)  { _current![c] = v; }
    public override void   SetBoolean(int c, bool v)   { _current![c] = v; }
    public override void   SetString(int c, string v)  { _current![c] = v; }
    public override void   SetBytes(int c, byte[] v)   { _current![c] = v; }
    public override void   SetDate(int c, DateTime v)  { _current![c] = v; }
    public override void   SetGuid(int c, Guid v)      { _current![c] = v; }
    public override void   SetDecimal(int c, decimal v) { _current![c] = v; }
    public override void   SetTime(int c, TimeSpan v)  { _current![c] = v; }
    public override void   SetDateTimeOffset(int c, DateTimeOffset v) { _current![c] = v; }
}
