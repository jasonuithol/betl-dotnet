using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Betl.Core;

public static class PipelineLoader
{
    public static Pipeline Load(string yamlText)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yamlText);
        try { stream.Load(reader); }
        catch (YamlException ex)
        {
            throw new PipelineLoadException($"YAML parse error: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0)
            throw new PipelineLoadException("YAML document is empty.");

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new PipelineLoadException("Top-level node must be a mapping.");

        return new PipelineParser().ParsePipeline(root);
    }

    public static Pipeline LoadFile(string path) => Load(File.ReadAllText(path));
}

internal sealed class PipelineParser
{
    public Pipeline ParsePipeline(YamlMappingNode root)
    {
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var ver = ReqInt(root, "betl", consumed);
        if (ver != 1) throw new PipelineLoadException($"Unsupported betl version: {ver}");

        var name = ReqStr(root, "name", consumed);
        var desc = OptStr(root, "description", consumed);
        var includes = OptStrList(root, "include", consumed);
        var parameters = OptMap(root, "parameters", consumed, ParseParameter);
        var connections = OptMap(root, "connections", consumed, ParseConnection);

        var pipelineSeq = ReqSeq(root, "pipeline", consumed);
        var steps = pipelineSeq.Children
            .Select(n => ParseStep(RequireMap(n, "pipeline entry")))
            .ToList();

        // Tolerate runtime hints / lua_init etc. at the root for forward-compat
        // by consuming the keys we know about and ignoring others at root level.
        // (Strict unknown-key rejection applies to step bodies, not the root.)
        consumed.Add("runtime");
        consumed.Add("lua_init");

        RejectUnknown(root, consumed, "<pipeline root>");

        return new Pipeline
        {
            BetlVersion = ver,
            Name = name,
            Description = desc,
            Includes = includes,
            Parameters = parameters,
            Connections = connections,
            Steps = steps,
        };
    }

    private Parameter ParseParameter(YamlMappingNode m)
    {
        var c = new HashSet<string>(StringComparer.Ordinal);
        var p = new Parameter
        {
            TypeSpelling = ReqStr(m, "type", c),
            Required = OptBool(m, "required", c) ?? false,
            Default = OptScalar(m, "default", c),
            Doc = OptStr(m, "doc", c),
        };
        if (m.Children.TryGetValue(new YamlScalarNode("enum"), out var enumNode))
        {
            c.Add("enum");
            if (enumNode is not YamlSequenceNode seq)
                throw new PipelineLoadException("Parameter 'enum' must be a sequence.");
            p = p with { Enum = seq.Children.Select(ScalarValue).ToList()! };
        }
        RejectUnknown(m, c, "parameter");
        return p;
    }

    private Connection ParseConnection(YamlMappingNode m)
    {
        var c = new HashSet<string>(StringComparer.Ordinal);
        var conn = new Connection
        {
            Type = ReqStr(m, "type", c),
            Dsn = ReqStr(m, "dsn", c),
        };
        var extras = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in m.Children)
        {
            var key = ((YamlScalarNode)kv.Key).Value!;
            if (c.Contains(key)) continue;
            extras[key] = ScalarValueOrNull(kv.Value);
        }
        return conn with { ExtraOptions = extras };
    }

    private Step ParseStep(YamlMappingNode m)
    {
        var c = new HashSet<string>(StringComparer.Ordinal);
        var id = ReqStr(m, "id", c);
        var type = ReqStr(m, "type", c);
        ConsumeCommonStepKeys(m, c);

        Step step = type switch
        {
            "dataflow"           => ParseDataflowBody(m, id, c),
            "foreach"            => ParseForeachBody(m, id, c),
            "csv.read"           => ParseCsvReadBody(m, id, c),
            "csv.write"          => ParseCsvWriteBody(m, id, c),
            "json.read"          => ParseJsonReadBody(m, id, c),
            "json.write"         => ParseJsonWriteBody(m, id, c),
            "filter"             => ParseFilterBody(m, id, c),
            "map"                => ParseMapBody(m, id, c),
            "distinct"           => ParseDistinctBody(m, id, c),
            "limit"              => ParseLimitBody(m, id, c),
            "union"              => ParseUnionBody(m, id, c),
            "sort"               => ParseSortBody(m, id, c),
            "aggregate"          => ParseAggregateBody(m, id, c),
            "conditional_split"  => ParseConditionalSplitBody(m, id, c),
            "multicast"          => ParseMulticastBody(m, id, c),
            "join"               => ParseJoinBody(m, id, c),
            _ when type.StartsWith("lua.", StringComparison.Ordinal) || type == "python.transform"
                => throw new PipelineLoadException(
                    $"Step '{id}' uses '{type}' which is not supported by betl.dotnet (no embedded {type.Split('.')[0]} runtime). "
                    + "Use 'ssisexpr' for inline expressions, or 'dotnet.script'/'dotnet.task' (planned) for scripts."),
            _ => throw new PipelineLoadException(
                $"Step '{id}' has unsupported type '{type}'."),
        };

        step = ApplyCommonStepKeys(step, m);
        RejectUnknown(m, c, $"step '{id}' ({type})");
        return step;
    }

    private static void ConsumeCommonStepKeys(YamlMappingNode m, HashSet<string> c)
    {
        foreach (var key in new[] { "after", "on_failure", "retries", "retry_backoff", "timeout", "condition", "description" })
        {
            if (m.Children.ContainsKey(new YamlScalarNode(key))) c.Add(key);
        }
    }

    private Step ApplyCommonStepKeys(Step step, YamlMappingNode m)
    {
        var noTrack = new HashSet<string>();
        var after = OptStrList(m, "after", noTrack);
        var onFailure = OptStr(m, "on_failure", noTrack) switch
        {
            null or "stop" => OnFailureMode.Stop,
            "continue" => OnFailureMode.Continue,
            "retry" => OnFailureMode.Retry,
            var v => throw new PipelineLoadException($"Invalid on_failure: '{v}'"),
        };
        var retries = OptInt(m, "retries", noTrack) ?? 0;
        var retryBackoff = OptStr(m, "retry_backoff", noTrack);
        var timeout = OptStr(m, "timeout", noTrack);
        var description = OptStr(m, "description", noTrack);
        var condition = OptExpr(m, "condition", noTrack);

        return step with
        {
            After = after,
            OnFailure = onFailure,
            Retries = retries,
            RetryBackoff = retryBackoff,
            Timeout = timeout,
            Description = description,
            Condition = condition,
        };
    }

    private DataflowStep ParseDataflowBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var stepsSeq = ReqSeq(m, "steps", c);
        var steps = stepsSeq.Children.Select(n => ParseStep(RequireMap(n, "dataflow step"))).ToList();
        return new DataflowStep { Id = id, Steps = steps };
    }

    private CsvReadStep ParseCsvReadBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var path = ReqStr(m, "path", c);
        var delim = OptStr(m, "delimiter", c) ?? ",";
        var header = OptBool(m, "header", c) ?? true;
        var encoding = OptStr(m, "encoding", c) ?? "utf-8";
        var schemaSeq = ReqSeq(m, "schema", c);
        var schema = ParseSchema(schemaSeq);
        return new CsvReadStep
        {
            Id = id,
            Path = path,
            Delimiter = delim,
            Header = header,
            Encoding = encoding,
            Schema = schema,
        };
    }

    private CsvWriteStep ParseCsvWriteBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        return new CsvWriteStep
        {
            Id = id,
            From = ReqStr(m, "from", c),
            Path = ReqStr(m, "path", c),
            Delimiter = OptStr(m, "delimiter", c) ?? ",",
            Header = OptBool(m, "header", c) ?? true,
        };
    }

    private FilterStep ParseFilterBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var where = ReqExpr(m, "where", c);
        return new FilterStep { Id = id, From = from, Where = where };
    }

    private MapStep ParseMapBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var addNode = OptNode(m, "add", c);
        var selectNode = OptNode(m, "select", c);

        if ((addNode is null) == (selectNode is null))
            throw new PipelineLoadException($"map step '{id}' must specify exactly one of 'add' or 'select'.");

        IReadOnlyDictionary<string, Expression>? add = null;
        if (addNode is not null)
        {
            if (addNode is not YamlMappingNode addMap)
                throw new PipelineLoadException($"map step '{id}': 'add' must be a mapping.");
            var dict = new Dictionary<string, Expression>(StringComparer.Ordinal);
            foreach (var kv in addMap.Children)
                dict[((YamlScalarNode)kv.Key).Value!] = ParseExpressionNode(kv.Value);
            add = dict;
        }

        IReadOnlyList<SelectColumn>? select = null;
        if (selectNode is not null)
        {
            if (selectNode is not YamlSequenceNode selSeq)
                throw new PipelineLoadException($"map step '{id}': 'select' must be a sequence.");
            select = selSeq.Children.Select(ParseSelectColumn).ToList();
        }

        return new MapStep { Id = id, From = from, Add = add, Select = select };
    }

    private ForeachStep ParseForeachBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var overSeq = ReqSeq(m, "over", c);
        var over = overSeq.Children
            .Select(n => (n as YamlScalarNode)?.Value
                ?? throw new PipelineLoadException($"foreach '{id}': 'over' entries must be strings."))
            .ToList();
        var varName = ReqStr(m, "as", c);
        var bodySeq = ReqSeq(m, "body", c);
        var body = bodySeq.Children
            .Select(n => ParseStep(RequireMap(n, $"foreach '{id}' body entry")))
            .ToList();
        return new ForeachStep { Id = id, Over = over, Variable = varName, Body = body };
    }

    private JsonReadStep ParseJsonReadBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var path = ReqStr(m, "path", c);
        var formatStr = OptStr(m, "format", c);
        var format = formatStr switch
        {
            null or "ndjson" => JsonFormat.Ndjson,
            "array" => JsonFormat.Array,
            _ => throw new PipelineLoadException($"json.read '{id}': unknown format '{formatStr}'."),
        };
        var colsSeq = ReqSeq(m, "columns", c);
        var columns = colsSeq.Children
            .Select(n => (n as YamlScalarNode)?.Value
                ?? throw new PipelineLoadException($"json.read '{id}': 'columns' entries must be strings."))
            .ToList();
        return new JsonReadStep { Id = id, Path = path, Format = format, Columns = columns };
    }

    private JsonWriteStep ParseJsonWriteBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var path = ReqStr(m, "path", c);
        var formatStr = OptStr(m, "format", c);
        var format = formatStr switch
        {
            null or "ndjson" => JsonFormat.Ndjson,
            "array" => JsonFormat.Array,
            _ => throw new PipelineLoadException($"json.write '{id}': unknown format '{formatStr}'."),
        };
        return new JsonWriteStep { Id = id, From = from, Path = path, Format = format };
    }

    private DistinctStep ParseDistinctBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var keys = OptStrList(m, "keys", c);
        return new DistinctStep
        {
            Id = id,
            From = from,
            Keys = keys.Count == 0 ? null : keys,
        };
    }

    private LimitStep ParseLimitBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var n = OptInt(m, "n", c)
            ?? throw new PipelineLoadException($"limit '{id}': 'n' is required.");
        if (n < 0) throw new PipelineLoadException($"limit '{id}': 'n' must be >= 0 (got {n}).");
        return new LimitStep { Id = id, From = from, N = n };
    }

    private UnionStep ParseUnionBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var fromSeq = ReqSeq(m, "from", c);
        var from = fromSeq.Children
            .Select(n => (n as YamlScalarNode)?.Value
                ?? throw new PipelineLoadException($"union '{id}': 'from' entries must be strings."))
            .ToList();
        if (from.Count < 2)
            throw new PipelineLoadException($"union '{id}': needs at least 2 input streams.");
        return new UnionStep { Id = id, From = from };
    }

    private SortStep ParseSortBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var bySeq = ReqSeq(m, "by", c);
        var by = bySeq.Children.Select(n => n switch
        {
            YamlScalarNode s => new SortKey(s.Value!, SortDirection.Asc),
            YamlMappingNode km => ParseSortKey(km, id),
            _ => throw new PipelineLoadException($"sort '{id}': 'by' entries must be a string or mapping."),
        }).ToList();
        if (by.Count == 0)
            throw new PipelineLoadException($"sort '{id}': 'by' must list at least one key.");
        return new SortStep { Id = id, From = from, By = by };
    }

    private static SortKey ParseSortKey(YamlMappingNode m, string stepId)
    {
        var c = new HashSet<string>(StringComparer.Ordinal);
        var col = ReqStr(m, "col", c);
        var dirStr = OptStr(m, "dir", c) ?? "asc";
        var dir = dirStr switch
        {
            "asc" => SortDirection.Asc,
            "desc" => SortDirection.Desc,
            _ => throw new PipelineLoadException($"sort '{stepId}': dir must be 'asc' or 'desc' (got '{dirStr}')."),
        };
        RejectUnknown(m, c, $"sort '{stepId}' key");
        return new SortKey(col, dir);
    }

    private AggregateStep ParseAggregateBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var groupBy = OptStrList(m, "group_by", c);
        c.Add("compute");
        var computeNode = GetChild(m, "compute") as YamlMappingNode
            ?? throw new PipelineLoadException($"aggregate '{id}': 'compute' must be a mapping.");

        var compute = new List<KeyValuePair<string, AggregateCompute>>();
        foreach (var kv in computeNode.Children)
        {
            var name = ((YamlScalarNode)kv.Key).Value!;
            if (kv.Value is not YamlMappingNode body)
                throw new PipelineLoadException($"aggregate '{id}.{name}': must be a mapping with 'agg' and optional 'over'.");
            var bc = new HashSet<string>(StringComparer.Ordinal);
            var aggStr = ReqStr(body, "agg", bc);
            var over = OptStr(body, "over", bc);
            RejectUnknown(body, bc, $"aggregate '{id}.{name}'");

            var op = aggStr switch
            {
                "sum" => AggregateOp.Sum,
                "count" => AggregateOp.Count,
                "count_distinct" => AggregateOp.CountDistinct,
                "min" => AggregateOp.Min,
                "max" => AggregateOp.Max,
                "avg" => AggregateOp.Avg,
                "first" => AggregateOp.First,
                "last" => AggregateOp.Last,
                _ => throw new PipelineLoadException($"aggregate '{id}.{name}': unknown agg '{aggStr}'."),
            };
            if (op != AggregateOp.Count && over is null)
                throw new PipelineLoadException($"aggregate '{id}.{name}': '{aggStr}' requires 'over'.");
            compute.Add(KeyValuePair.Create(name, new AggregateCompute(op, over)));
        }
        if (compute.Count == 0)
            throw new PipelineLoadException($"aggregate '{id}': 'compute' is empty.");
        return new AggregateStep { Id = id, From = from, GroupBy = groupBy, Compute = compute };
    }

    private ConditionalSplitStep ParseConditionalSplitBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var defaultCase = OptStr(m, "default_case", c);
        c.Add("cases");
        var casesNode = GetChild(m, "cases") as YamlMappingNode
            ?? throw new PipelineLoadException($"conditional_split '{id}': 'cases' must be a mapping.");

        var cases = new List<KeyValuePair<string, Expression>>();
        foreach (var kv in casesNode.Children)
        {
            var name = ((YamlScalarNode)kv.Key).Value!;
            cases.Add(KeyValuePair.Create(name, ParseExpressionNode(kv.Value)));
        }
        if (cases.Count == 0)
            throw new PipelineLoadException($"conditional_split '{id}': 'cases' is empty.");
        return new ConditionalSplitStep
        {
            Id = id,
            From = from,
            Cases = cases,
            DefaultCase = defaultCase,
        };
    }

    private MulticastStep ParseMulticastBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var from = ReqStr(m, "from", c);
        var outputs = OptStrList(m, "outputs", c);
        if (outputs.Count < 2)
            throw new PipelineLoadException(
                $"multicast '{id}': 'outputs' must list at least two port names.");
        return new MulticastStep { Id = id, From = from, Outputs = outputs };
    }

    private JoinStep ParseJoinBody(YamlMappingNode m, string id, HashSet<string> c)
    {
        var left = ReqStr(m, "left", c);
        var right = ReqStr(m, "right", c);
        var kindStr = OptStr(m, "kind", c) ?? "inner";
        var kind = kindStr switch
        {
            "inner" => JoinKind.Inner,
            "left" => JoinKind.Left,
            "right" => JoinKind.Right,
            "full" => JoinKind.Full,
            _ => throw new PipelineLoadException($"join '{id}': unknown kind '{kindStr}'."),
        };
        c.Add("on");
        var onNode = GetChild(m, "on") as YamlMappingNode
            ?? throw new PipelineLoadException($"join '{id}': 'on' must be a mapping (leftCol: rightCol).");
        var on = new List<KeyValuePair<string, string>>();
        foreach (var kv in onNode.Children)
        {
            var l = ((YamlScalarNode)kv.Key).Value!;
            var r = (kv.Value as YamlScalarNode)?.Value
                ?? throw new PipelineLoadException($"join '{id}': 'on.{l}' must be a string.");
            on.Add(KeyValuePair.Create(l, r));
        }
        if (on.Count == 0)
            throw new PipelineLoadException($"join '{id}': 'on' needs at least one key.");
        return new JoinStep { Id = id, Left = left, Right = right, On = on, Kind = kind };
    }

    private SelectColumn ParseSelectColumn(YamlNode n)
    {
        if (n is YamlScalarNode scalar) return new PassthroughColumn(scalar.Value!);
        if (n is not YamlMappingNode m)
            throw new PipelineLoadException("map.select entry must be a string or mapping.");

        var c = new HashSet<string>(StringComparer.Ordinal);
        var name = ReqStr(m, "name", c);
        var fromCol = OptStr(m, "from", c);
        var lang = OptStr(m, "lang", c);
        var expr = OptStr(m, "expr", c);
        var value = OptScalar(m, "value", c);
        RejectUnknown(m, c, $"select column '{name}'");

        if (fromCol is not null) return new RenameColumn(name, fromCol);
        if (lang == "literal") return new LiteralColumn(name, value);
        if (expr is not null)
        {
            if (lang is null)
                throw new PipelineLoadException(
                    $"select column '{name}': bare 'expr:' requires an explicit 'lang:' (lua shorthand not supported).");
            return new ComputedColumn(name, new LangExpression(lang, expr));
        }
        throw new PipelineLoadException(
            $"select column '{name}' must be a string, {{name, from}}, {{name, lang, expr}}, or {{name, lang: literal, value}}.");
    }

    private Schema ParseSchema(YamlSequenceNode seq)
    {
        var cols = new List<Column>();
        foreach (var entry in seq.Children)
        {
            if (entry is not YamlMappingNode m)
                throw new PipelineLoadException("Schema entries must be mappings.");
            var c = new HashSet<string>(StringComparer.Ordinal);
            var name = ReqStr(m, "name", c);
            var typeSpelling = ReqStr(m, "type", c);
            var nullable = OptBool(m, "nullable", c) ?? true;
            var doc = OptStr(m, "doc", c);
            RejectUnknown(m, c, $"schema column '{name}'");
            cols.Add(new Column
            {
                Name = name,
                ArrowType = ArrowTypes.Parse(typeSpelling),
                Nullable = nullable,
                Doc = doc,
            });
        }
        return new Schema { Columns = cols };
    }

    private Expression ParseExpressionNode(YamlNode n)
    {
        if (n is YamlScalarNode scalar)
        {
            throw new PipelineLoadException(
                $"Bare shorthand expression \"{scalar.Value}\" defaults to lang: lua, which betl.dotnet does not support. " +
                "Spell explicitly as { lang: ssisexpr, expr: \"...\" } or { lang: literal, value: ... }.");
        }
        if (n is not YamlMappingNode m)
            throw new PipelineLoadException("Expression must be a mapping ({lang, expr} or {lang: literal, value}).");

        var c = new HashSet<string>(StringComparer.Ordinal);
        var lang = OptStr(m, "lang", c)
            ?? throw new PipelineLoadException("Expression must declare 'lang:' explicitly (no default in betl.dotnet).");
        if (lang == "literal")
        {
            var value = OptScalar(m, "value", c);
            RejectUnknown(m, c, "literal expression");
            return new LiteralExpression(value);
        }
        var expr = ReqStr(m, "expr", c);
        RejectUnknown(m, c, $"{lang} expression");
        return new LangExpression(lang, expr);
    }

    // --- node helpers --------------------------------------------------

    private static YamlMappingNode RequireMap(YamlNode n, string context) =>
        n as YamlMappingNode ?? throw new PipelineLoadException($"{context} must be a mapping.");

    private static YamlScalarNode? GetScalar(YamlMappingNode m, string key)
    {
        return m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v as YamlScalarNode : null;
    }

    private static YamlNode? GetChild(YamlMappingNode m, string key)
    {
        return m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;
    }

    private static string ReqStr(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        return GetScalar(m, key)?.Value
            ?? throw new PipelineLoadException($"Missing required key '{key}'.");
    }

    private static string? OptStr(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        return GetScalar(m, key)?.Value;
    }

    private static int ReqInt(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        var s = GetScalar(m, key)?.Value
            ?? throw new PipelineLoadException($"Missing required key '{key}'.");
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new PipelineLoadException($"Key '{key}' must be an integer, got '{s}'.");
    }

    private static int? OptInt(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        var s = GetScalar(m, key)?.Value;
        if (s is null) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : throw new PipelineLoadException($"Key '{key}' must be an integer.");
    }

    private static bool? OptBool(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        var s = GetScalar(m, key)?.Value;
        if (s is null) return null;
        return s.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new PipelineLoadException($"Key '{key}' must be boolean, got '{s}'."),
        };
    }

    private static YamlSequenceNode ReqSeq(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        return GetChild(m, key) as YamlSequenceNode
            ?? throw new PipelineLoadException($"Missing required sequence '{key}'.");
    }

    private static YamlNode? OptNode(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        return GetChild(m, key);
    }

    private static IReadOnlyList<string> OptStrList(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        if (GetChild(m, key) is not YamlSequenceNode seq) return [];
        return seq.Children.Select(c => (c as YamlScalarNode)?.Value
            ?? throw new PipelineLoadException($"'{key}' entries must be strings.")).ToList();
    }

    private static object? OptScalar(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        return ScalarValueOrNull(GetChild(m, key));
    }

    private static Expression? OptExpr(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        var n = GetChild(m, key);
        if (n is null) return null;
        if (n is YamlScalarNode s)
        {
            // For top-level `condition:` the upstream spec accepts true/false/yes/no/1/0 literals.
            return s.Value?.ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => new LiteralExpression(true),
                "false" or "no" or "0" => new LiteralExpression(false),
                _ => new LiteralExpression(s.Value),
            };
        }
        return new PipelineParser().ParseExpressionNode(n);
    }

    private static Expression ReqExpr(YamlMappingNode m, string key, ISet<string> consumed)
    {
        consumed.Add(key);
        var n = GetChild(m, key)
            ?? throw new PipelineLoadException($"Missing required expression '{key}'.");
        return new PipelineParser().ParseExpressionNode(n);
    }

    private static IReadOnlyDictionary<string, T> OptMap<T>(
        YamlMappingNode m, string key, ISet<string> consumed, Func<YamlMappingNode, T> parser)
    {
        consumed.Add(key);
        if (GetChild(m, key) is not YamlMappingNode map) return new Dictionary<string, T>();
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var kv in map.Children)
        {
            var k = ((YamlScalarNode)kv.Key).Value!;
            if (kv.Value is not YamlMappingNode v)
                throw new PipelineLoadException($"'{key}.{k}' must be a mapping.");
            result[k] = parser(v);
        }
        return result;
    }

    private static object? ScalarValueOrNull(YamlNode? n)
    {
        if (n is not YamlScalarNode s) return null;
        return ScalarValue(s);
    }

    private static object? ScalarValue(YamlNode n)
    {
        if (n is not YamlScalarNode s) throw new PipelineLoadException("Expected scalar value.");
        var v = s.Value;
        if (v is null) return null;
        if (s.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted) return v;
        if (string.Equals(v, "null", StringComparison.OrdinalIgnoreCase) || v == "~") return null;
        if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return v;
    }

    private static void RejectUnknown(YamlMappingNode m, ISet<string> consumed, string context)
    {
        var unknown = new List<string>();
        foreach (var kv in m.Children)
        {
            var k = ((YamlScalarNode)kv.Key).Value!;
            if (!consumed.Contains(k)) unknown.Add(k);
        }
        if (unknown.Count > 0)
            throw new PipelineLoadException(
                $"Unknown key(s) in {context}: {string.Join(", ", unknown)}.");
    }
}
