using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// PySpark: a column a DataFrame does not have, where its columns can be read from the code (Spark stops with AnalysisException
/// "[UNRESOLVED_COLUMN.WITH_SUGGESTION]"). The columns come from `spark.createDataFrame(rows, ["a", "b"])`, a DDL string ("a INT, b STRING")
/// or a StructType of StructFields, and follow the DataFrame through withColumn, withColumnRenamed, drop, select of plain names and the
/// methods that keep the columns (filter, where, distinct, orderBy, limit, cache, ...). Names are followed statement by statement in one
/// block: a name assigned again inside an if, a loop or a with is not followed after it. Checked: df["x"], df.x for a name that is not a
/// DataFrame method, and the plain names and F.col("x") / col("x") given to select, filter, where, withColumn, groupBy, orderBy and sort.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> SparkDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || !m.Source.Contains("pyspark", StringComparison.Ordinal)) yield break;
        var found = new List<Finding>();
        SparkBlock(m, ev, m.Ast.Body, new Dictionary<string, List<string>?>(StringComparer.Ordinal), found);
        foreach (var f in found)
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    /// <summary>The DataFrame methods whose result has the receiver's columns.</summary>
    static readonly HashSet<string> SameColumns = new(StringComparer.Ordinal)
    {
        "filter", "where", "distinct", "dropDuplicates", "drop_duplicates", "orderBy", "sort", "limit", "cache", "persist", "unpersist", "repartition",
        "coalesce", "sample", "alias", "dropna", "fillna", "localCheckpoint", "checkpoint", "hint", "sortWithinPartitions", "union", "unionAll",
    };

    /// <summary>The methods whose plain-string and col() arguments name columns of the receiver.</summary>
    static readonly HashSet<string> TakesColumns = new(StringComparer.Ordinal)
    {
        "select", "filter", "where", "withColumn", "groupBy", "groupby", "orderBy", "sort", "sortWithinPartitions", "dropDuplicates", "drop_duplicates",
    };

    void SparkBlock(ModuleInfo m, TypeEvaluator ev, IReadOnlyList<Stmt> body, Dictionary<string, List<string>?> frames, List<Finding> found)
    {
        foreach (var stmt in body)
        {
            // what the statement reads is checked against the columns known before it
            foreach (var e in TopExpressions(stmt)) SparkCheck(m, ev, e, frames, found);
            switch (stmt)
            {
                case AssignStmt { Targets: [NameExpr name], Value: var value }:
                    frames[name.Name] = SparkColumns(m, ev, value, frames);
                    break;
                case FunctionDef f:
                    SparkBlock(m, ev, f.Body, new Dictionary<string, List<string>?>(StringComparer.Ordinal), found);
                    break;
                case ClassDef c:
                    SparkBlock(m, ev, c.Body, new Dictionary<string, List<string>?>(StringComparer.Ordinal), found);
                    break;
                default:
                    // a nested block: checked with what is known now; what it assigns is not known after it
                    foreach (var inner in NestedBlocks(stmt))
                    {
                        var copy = new Dictionary<string, List<string>?>(frames, StringComparer.Ordinal);
                        SparkBlock(m, ev, inner, copy, found);
                        foreach (var (k, v) in copy) if (!frames.TryGetValue(k, out var before) || !ReferenceEquals(before, v)) frames[k] = null;
                    }
                    foreach (var n in AstChildren.Descendants(stmt).OfType<NameExpr>())
                        if (IsAssignedIn(stmt, n)) frames[n.Name] = null;
                    break;
            }
        }
    }

    /// <summary>A name bound by the statement itself (a for target, a with target, an augmented assignment).</summary>
    static bool IsAssignedIn(Stmt stmt, NameExpr n) => stmt switch
    {
        ForStmt f => AstChildren.Descendants(f.Target).Contains(n) || ReferenceEquals(f.Target, n),
        WithStmt w => w.Items.Any(i => i.Target != null && (ReferenceEquals(i.Target, n) || AstChildren.Descendants(i.Target).Contains(n))),
        AugAssignStmt a => ReferenceEquals(a.Target, n),
        AssignStmt a => a.Targets.Any(t => ReferenceEquals(t, n) || AstChildren.Descendants(t).Contains(n)),
        _ => false,
    };

    static IEnumerable<IReadOnlyList<Stmt>> NestedBlocks(Stmt s) => s switch
    {
        IfStmt i => new[] { i.Body, i.Orelse },
        WhileStmt w => new[] { w.Body, w.Orelse },
        ForStmt f => new[] { f.Body, f.Orelse },
        WithStmt w => new[] { w.Body },
        TryStmt t => new[] { t.Body, t.Orelse, t.Finally }.Concat(t.Handlers.Select(h => h.Body)),
        _ => Array.Empty<IReadOnlyList<Stmt>>(),
    };

    /// <summary>The expressions of a statement that run with it (not those of its nested blocks).</summary>
    static IEnumerable<Expr> TopExpressions(Stmt s) => s switch
    {
        ExprStmt e => new[] { e.Value },
        AssignStmt a => a.Targets.Append(a.Value),
        AugAssignStmt a => new[] { a.Value },
        AnnAssignStmt a => a.Value != null ? new[] { a.Value } : Array.Empty<Expr>(),
        ReturnStmt { Value: { } v } => new[] { v },
        IfStmt i => new[] { i.Test },
        WhileStmt w => new[] { w.Test },
        ForStmt f => new[] { f.Iter },
        WithStmt w => w.Items.Select(i => i.Context),
        AssertStmt a => new[] { a.Test },
        _ => Array.Empty<Expr>(),
    };

    /// <summary>Whether the value is a PySpark DataFrame (pyspark itself, or its stubs).</summary>
    static bool IsSparkFrame(TypeEvaluator ev, PyType t) =>
        t is ClassType { IsInstance: true } c && ev.MroOf(c.Info).Any(b => b is ClassType { Info: { Name: "DataFrame" } i } && i.Module.Name.StartsWith("pyspark", StringComparison.Ordinal));

    /// <summary>The columns of a DataFrame expression, in order; null when they cannot be known.</summary>
    List<string>? SparkColumns(ModuleInfo m, TypeEvaluator ev, Expr e, Dictionary<string, List<string>?> frames)
    {
        switch (e)
        {
            case NameExpr n: return frames.GetValueOrDefault(n.Name);
            case CallExpr { Func: AttributeExpr { Attr: "createDataFrame" } } call:
            {
                var schema = call.Args.FirstOrDefault(a => a.Name == "schema")?.Value ?? call.Args.Where(a => a.Name == null && a.Stars == 0).Skip(1).FirstOrDefault()?.Value;
                return schema == null ? null : SchemaColumns(m, schema, frames);
            }
            case CallExpr { Func: AttributeExpr { Value: var recv, Attr: var method } } call when IsSparkFrame(ev, ev.TypeOf(m, recv)):
            {
                var cols = SparkColumns(m, ev, recv, frames);
                if (cols == null) return null;
                var strings = call.Args.Select(a => a.Name == null && a.Stars == 0 ? FixtureModel.StringValue(a.Value) : null).ToList();
                switch (method)
                {
                    case "withColumn" when strings.FirstOrDefault() is { } added:
                        return cols.Contains(added) ? cols : cols.Append(added).ToList();
                    case "withColumnRenamed" when strings.Count == 2 && strings[0] is { } from && strings[1] is { } to:
                        return cols.Select(c => c == from ? to : c).ToList();
                    case "drop" when strings.Count > 0 && strings.All(s => s != null):
                        return cols.Where(c => !strings.Contains(c)).ToList();
                    case "select" when call.Args.Count > 0 && strings.All(s => s != null && s != "*"):
                        return strings.Select(s => s!).ToList();
                    case "toDF" when call.Args.Count > 0 && strings.All(s => s != null):
                        return strings.Select(s => s!).ToList();
                    default:
                        return SameColumns.Contains(method) ? cols : null;
                }
            }
            default: return null;
        }
    }

    /// <summary>The column names of a schema: a list of names, a DDL string or a StructType of StructFields.</summary>
    static List<string>? SchemaColumns(ModuleInfo m, Expr schema, Dictionary<string, List<string>?> frames)
    {
        if (schema is ListExpr or TupleExpr)
        {
            var items = schema is ListExpr l ? l.Items : ((TupleExpr)schema).Items;
            var names = items.Select(FixtureModel.StringValue).ToList();
            return names.All(n => n != null) ? names.Select(n => n!).ToList() : null;
        }
        if (FixtureModel.StringValue(schema) is { } ddl)
        {
            // "a INT, b STRING" (a struct<...> type has commas of its own: such a schema is not read)
            if (ddl.Contains('<')) return null;
            var names = ddl.Split(',').Select(p => p.Trim().Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim('`')).ToList();
            return names.All(n => !string.IsNullOrEmpty(n)) ? names.Select(n => n!).ToList() : null;
        }
        if (schema is CallExpr st && FixtureModel.CalleeText(m, st.Func).EndsWith("StructType", StringComparison.Ordinal)
            && st.Args.FirstOrDefault()?.Value is ListExpr fields)
        {
            var names = fields.Items.Select(f => f is CallExpr fc && FixtureModel.CalleeText(m, fc.Func).EndsWith("StructField", StringComparison.Ordinal)
                ? FixtureModel.StringValue(fc.Args.FirstOrDefault()?.Value) : null).ToList();
            return names.All(n => n != null) ? names.Select(n => n!).ToList() : null;
        }
        return null;
    }

    void SparkCheck(ModuleInfo m, TypeEvaluator ev, Expr root, Dictionary<string, List<string>?> frames, List<Finding> found)
    {
        foreach (var node in AstChildren.Descendants(root).Prepend(root))
        {
            switch (node)
            {
                case SubscriptExpr { Value: NameExpr frame, Index: var index } when frames.GetValueOrDefault(frame.Name) is { } cols && FixtureModel.StringValue(index) is { } name:
                    Missing(index, name, cols);
                    break;
                case AttributeExpr { Value: NameExpr frame } attr when frames.GetValueOrDefault(frame.Name) is { } cols && !attr.Attr.StartsWith('_')
                    && IsSparkFrame(ev, ev.TypeOf(m, frame)) && ev.AttributeType(ev.TypeOf(m, frame), attr.Attr) is { } member && (member.Missing.Count > 0 || member.Declarations.Count == 0):
                    // not a method of DataFrame: Spark reads it as a column
                    if (!(m.ParentOf(attr) is AssignStmt a && a.Targets.Contains(attr))) Missing(attr, attr.Attr, cols, attr.End - attr.Attr.Length);
                    break;
                case CallExpr { Func: AttributeExpr { Value: var recv, Attr: var method } } call when TakesColumns.Contains(method) && IsSparkFrame(ev, ev.TypeOf(m, recv))
                    && SparkColumns(m, ev, recv, frames) is { } cols:
                    for (int i = 0; i < call.Args.Count; i++)
                    {
                        var arg = call.Args[i];
                        // withColumn's first argument names the new column; filter and where take a SQL text
                        if (method == "withColumn" && i == 0 || arg.Name != null || arg.Stars != 0) continue;
                        if (method is not ("filter" or "where") && FixtureModel.StringValue(arg.Value) is { } name && name != "*") Missing(arg.Value, name, cols);
                        foreach (var c in AstChildren.Descendants(arg.Value).Prepend(arg.Value).OfType<CallExpr>())
                            if (FixtureModel.CalleeText(m, c.Func) is "col" or "F.col" or "functions.col" or "column" or "F.column" && c.Args.Count == 1
                                && FixtureModel.StringValue(c.Args[0].Value) is { } cn && !cn.Contains('.') && cn != "*")
                                Missing(c.Args[0].Value, cn, cols);
                    }
                    break;
            }
        }

        void Missing(Node at, string name, List<string> cols, int? start = null)
        {
            // Spark resolves column names without regard to case by default (spark.sql.caseSensitive is false)
            if (cols.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) || name.Contains('.') || name.Contains('`')) return;
            int s = start ?? at.Start;
            if (found.Any(f => f.Start == s)) return;
            found.Add(new Finding(s, start != null ? name.Length : at.End - at.Start,
                $"Column '{name}' is not in the DataFrame (its columns: {string.Join(", ", cols)}): Spark raises AnalysisException", "spark-column", FindingLevel.Warning));
        }
    }
}
