using System.Globalization;
using Apache.Arrow.Types;
using Betl.Core;
using ClosedXML.Excel;

namespace Betl.Components;

/// <summary>
/// Excel source — opens an .xlsx via ClosedXML, walks the named (or first)
/// worksheet, optionally consumes the first row as headers, and emits one
/// utf8-typed row per data row. Schema is determined eagerly from the
/// header row so downstream wiring can resolve column refs.
/// </summary>
public sealed class XlsxReadComponent : IDataComponent
{
    private readonly string _path;
    private readonly bool _header;
    private readonly string? _sheet;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public XlsxReadComponent(XlsxReadStep step, string resolvedPath)
    {
        Id = step.Id;
        _path = resolvedPath;
        _header = step.Header;
        _sheet = step.Sheet;

        // Determine the schema eagerly: ClosedXML must open the workbook to
        // know how wide the data is, and downstream components need a schema
        // at construction time anyway.
        using var wb = new XLWorkbook(_path);
        var ws = ResolveSheet(wb);
        var firstRow = ws.FirstRowUsed();
        var ncols = firstRow?.LastCellUsed()?.Address.ColumnNumber ?? 0;

        var names = new List<string>();
        if (_header && firstRow is not null)
        {
            for (var i = 1; i <= ncols; i++)
                names.Add(firstRow.Cell(i).GetString());
        }
        else
        {
            for (var i = 1; i <= ncols; i++)
                names.Add($"c{i}");
        }

        var utf8 = (IArrowType)StringType.Default;
        OutputSchema = new Schema
        {
            Columns = names.Select(n => new Column { Name = n, ArrowType = utf8, Nullable = true }).ToList(),
        };
    }

    public IEnumerable<Row> Stream()
    {
        using var wb = new XLWorkbook(_path);
        var ws = ResolveSheet(wb);
        var rows = ws.RowsUsed();
        var ncols = OutputSchema.Columns.Count;

        var skipped = false;
        foreach (var row in rows)
        {
            if (_header && !skipped)
            {
                skipped = true;
                continue;
            }
            var values = new object?[ncols];
            for (var i = 0; i < ncols; i++)
                values[i] = CellAsString(row.Cell(i + 1));
            yield return new Row(OutputSchema, values);
        }
    }

    private IXLWorksheet ResolveSheet(IXLWorkbook wb)
    {
        if (_sheet is null) return wb.Worksheets.First();
        if (wb.TryGetWorksheet(_sheet, out var ws)) return ws;
        throw new BetlException($"xlsx.read '{Id}': workbook has no sheet named '{_sheet}'.");
    }

    private static string? CellAsString(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        // ClosedXML returns native types via .Value — stringify uniformly to
        // match the v0.1 utf8 surface (callers cast in a downstream map).
        return cell.DataType switch
        {
            XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
            XLDataType.Number => cell.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            XLDataType.DateTime => cell.GetDateTime().ToString("o", CultureInfo.InvariantCulture),
            _ => cell.GetString(),
        };
    }
}

/// <summary>
/// Excel sink — writes the upstream stream into a single worksheet, with
/// an optional header row. Values are stringified at write time.
/// </summary>
public sealed class XlsxWriteSink : ISink
{
    private readonly string _path;
    private readonly bool _header;
    private readonly string _sheet;

    public string Id { get; }

    public XlsxWriteSink(XlsxWriteStep step, string resolvedPath)
    {
        Id = step.Id;
        _path = resolvedPath;
        _header = step.Header;
        _sheet = step.Sheet;
    }

    public void Drain(IDataComponent input)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(_sheet);

        var schema = input.OutputSchema;
        var ncols = schema.Columns.Count;

        var rowIdx = 1;
        if (_header)
        {
            for (var c = 0; c < ncols; c++)
                ws.Cell(rowIdx, c + 1).SetValue(schema.Columns[c].Name);
            rowIdx++;
        }

        foreach (var row in input.Stream())
        {
            for (var c = 0; c < ncols; c++)
            {
                var v = row.Values[c];
                if (v is null) continue;
                ws.Cell(rowIdx, c + 1).SetValue(FormatCell(v));
            }
            rowIdx++;
        }

        wb.SaveAs(_path);
    }

    private static string FormatCell(object v) => v switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}
