using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Two-input equi-join. Builds a hash on the right side keyed by the configured
/// join columns; streams the left side and emits each combination. Supports
/// inner / left / right / full kinds. The output schema is left columns + right
/// columns, in that order, with no suppression of the duplicate key column.
/// </summary>
public sealed class JoinComponent : IDataComponent
{
    private readonly IDataComponent _left;
    private readonly IDataComponent _right;
    private readonly int[] _leftKeyIndices;
    private readonly int[] _rightKeyIndices;
    private readonly JoinKind _kind;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public JoinComponent(JoinStep step, IDataComponent left, IDataComponent right)
    {
        Id = step.Id;
        _left = left;
        _right = right;
        _kind = step.Kind;

        _leftKeyIndices = step.On
            .Select(kv =>
            {
                var i = left.OutputSchema.IndexOf(kv.Key);
                if (i < 0) throw new BetlException(
                    $"join '{step.Id}': left column '{kv.Key}' is not in '{step.Left}' schema.");
                return i;
            }).ToArray();

        _rightKeyIndices = step.On
            .Select(kv =>
            {
                var i = right.OutputSchema.IndexOf(kv.Value);
                if (i < 0) throw new BetlException(
                    $"join '{step.Id}': right column '{kv.Value}' is not in '{step.Right}' schema.");
                return i;
            }).ToArray();

        var outCols = new List<Column>(left.OutputSchema.Columns.Count + right.OutputSchema.Columns.Count);
        outCols.AddRange(left.OutputSchema.Columns);
        outCols.AddRange(right.OutputSchema.Columns);
        OutputSchema = new Schema { Columns = outCols };
    }

    public IEnumerable<Row> Stream()
    {
        // Build hash on right side.
        var rightByKey = new Dictionary<object?[], List<Row>>(ObjectArrayComparer.Instance);
        foreach (var r in _right.Stream())
        {
            var k = RowOps.ExtractKey(r, _rightKeyIndices);
            if (!rightByKey.TryGetValue(k, out var list))
                rightByKey[k] = list = [];
            list.Add(r);
        }

        var matchedRightKeys = (_kind is JoinKind.Right or JoinKind.Full)
            ? new HashSet<object?[]>(ObjectArrayComparer.Instance)
            : null;

        var leftWidth = _left.OutputSchema.Columns.Count;
        var rightWidth = _right.OutputSchema.Columns.Count;

        foreach (var l in _left.Stream())
        {
            var k = RowOps.ExtractKey(l, _leftKeyIndices);
            if (rightByKey.TryGetValue(k, out var matches))
            {
                matchedRightKeys?.Add(k);
                foreach (var r in matches)
                    yield return Combine(l, r, leftWidth, rightWidth);
            }
            else if (_kind is JoinKind.Left or JoinKind.Full)
            {
                yield return CombineWithNullRight(l, leftWidth, rightWidth);
            }
            // INNER and RIGHT drop unmatched left rows.
        }

        if (_kind is JoinKind.Right or JoinKind.Full)
        {
            foreach (var (key, rows) in rightByKey)
            {
                if (matchedRightKeys!.Contains(key)) continue;
                foreach (var r in rows)
                    yield return CombineWithNullLeft(r, leftWidth, rightWidth);
            }
        }
    }

    private Row Combine(Row left, Row right, int leftWidth, int rightWidth)
    {
        var v = new object?[leftWidth + rightWidth];
        Array.Copy(left.Values, 0, v, 0, leftWidth);
        Array.Copy(right.Values, 0, v, leftWidth, rightWidth);
        return new Row(OutputSchema, v);
    }

    private Row CombineWithNullRight(Row left, int leftWidth, int rightWidth)
    {
        var v = new object?[leftWidth + rightWidth];
        Array.Copy(left.Values, 0, v, 0, leftWidth);
        return new Row(OutputSchema, v);
    }

    private Row CombineWithNullLeft(Row right, int leftWidth, int rightWidth)
    {
        var v = new object?[leftWidth + rightWidth];
        Array.Copy(right.Values, 0, v, leftWidth, rightWidth);
        return new Row(OutputSchema, v);
    }
}
