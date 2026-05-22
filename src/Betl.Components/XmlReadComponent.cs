using System.Xml;
using System.Xml.XPath;
using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// XML source — emits one row per node matched by <c>row_xpath:</c>, with
/// columns projected via row-relative XPath expressions. All output columns
/// are utf8 in v0.1.
/// </summary>
public sealed class XmlReadComponent : IDataComponent
{
    private readonly string _path;
    private readonly string _rowXPath;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _columns;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public XmlReadComponent(XmlReadStep step, string resolvedPath)
    {
        Id = step.Id;
        _path = resolvedPath;
        _rowXPath = step.RowXPath;
        _columns = step.Columns;

        var utf8 = (IArrowType)StringType.Default;
        OutputSchema = new Schema
        {
            Columns = step.Columns.Select(kv => new Column
            {
                Name = kv.Key,
                ArrowType = utf8,
                Nullable = true,
            }).ToList(),
        };
    }

    public IEnumerable<Row> Stream()
    {
        var doc = new XPathDocument(_path);
        var nav = doc.CreateNavigator();

        XPathNodeIterator rowIter;
        try { rowIter = nav.Select(_rowXPath); }
        catch (XPathException ex)
        {
            throw new BetlException(
                $"xml.read '{Id}': row_xpath '{_rowXPath}' is invalid: {ex.Message}");
        }

        while (rowIter.MoveNext())
        {
            var rowNav = rowIter.Current!;
            var values = new object?[_columns.Count];
            for (var i = 0; i < _columns.Count; i++)
            {
                var (_, expr) = (_columns[i].Key, _columns[i].Value);
                try
                {
                    var iter = rowNav.Select(expr);
                    values[i] = iter.MoveNext() ? iter.Current!.Value : null;
                }
                catch (XPathException ex)
                {
                    throw new BetlException(
                        $"xml.read '{Id}': column '{_columns[i].Key}' xpath '{expr}' invalid: {ex.Message}");
                }
            }
            yield return new Row(OutputSchema, values);
        }
    }
}
