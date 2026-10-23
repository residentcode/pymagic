using PyMagic.Core.Syntax;

namespace PyMagic.Core.Semantics;

/// <summary>The interpreter a module is analysed for; decides conditions such as <c>sys.version_info &gt;= (3, 12)</c>.</summary>
public sealed record PythonEnvironment(int Major, int Minor, string Platform)
{
    public static PythonEnvironment Default { get; } = new(3, 13, "win32");
}

/// <summary>
/// Conditions whose outcome is fixed for a given interpreter: version and platform checks, TYPE_CHECKING and
/// literals. Branches that cannot run are not bound, so stubs full of <c>if sys.version_info &gt;= ...</c> define
/// exactly the names that exist for the configured version.
/// </summary>
public static class StaticConditions
{
    /// <summary>True/false when the condition is decided by the environment, null when it depends on run-time values.</summary>
    public static bool? Evaluate(Expr e, PythonEnvironment env)
    {
        switch (e)
        {
            case ConstExpr { Kind: ConstKind.True }: return true;
            case ConstExpr { Kind: ConstKind.False or ConstKind.None }: return false;
            case ConstExpr { Kind: ConstKind.Int } c: return !c.Text.All(ch => ch == '0' || ch == '_');

            case NameExpr { Name: "TYPE_CHECKING" }: return true;
            case AttributeExpr { Attr: "TYPE_CHECKING" }: return true;

            case UnaryExpr { Op: "not" } u: return Evaluate(u.Operand, env) is bool b ? !b : null;

            case BinaryExpr { Op: "and" } a:
            {
                var l = Evaluate(a.Left, env);
                var r = Evaluate(a.Right, env);
                if (l == false || r == false) return false;
                return l == true && r == true ? true : null;
            }
            case BinaryExpr { Op: "or" } o:
            {
                var l = Evaluate(o.Left, env);
                var r = Evaluate(o.Right, env);
                if (l == true || r == true) return true;
                return l == false && r == false ? false : null;
            }

            case CompareExpr { Ops.Count: 1 } c:
                return EvaluateComparison(c.Left, c.Ops[0], c.Comparators[0], env);

            case CallExpr { Func: AttributeExpr { Attr: "startswith", Value: var target }, Args.Count: 1 } call
                when IsSysAttribute(target, "platform") && call.Args[0].Value is ConstExpr { Kind: ConstKind.Str } prefix:
                return env.Platform.StartsWith(Unquote(prefix.Text), StringComparison.Ordinal);
        }
        return null;
    }

    static bool? EvaluateComparison(Expr left, string op, Expr right, PythonEnvironment env)
    {
        // sys.platform == "linux"
        if (IsSysAttribute(left, "platform") && right is ConstExpr { Kind: ConstKind.Str } s && op is "==" or "!=")
        {
            bool eq = env.Platform == Unquote(s.Text);
            return op == "==" ? eq : !eq;
        }

        // sys.version_info >= (3, 9), sys.version_info[:2] < (3, 10), sys.version_info[0] == 3, sys.version_info.minor > 8
        if (TryVersionOperand(left, env, out var leftValues) && TryIntTuple(right, out var rightValues))
            return Compare(leftValues, op, rightValues);
        return null;
    }

    static bool IsSysAttribute(Expr e, string attr) => e is AttributeExpr { Value: NameExpr { Name: "sys" } } a && a.Attr == attr;

    static string Unquote(string literal)
    {
        int q = 0;
        while (q < literal.Length && literal[q] != '"' && literal[q] != '\'') q++;
        return q < literal.Length - 1 ? literal.Substring(q + 1, literal.Length - q - 2) : literal;
    }

    /// <summary>The version tuple, or the part of it the expression selects.</summary>
    static bool TryVersionOperand(Expr e, PythonEnvironment env, out int[] values)
    {
        values = Array.Empty<int>();
        int[] full = { env.Major, env.Minor, 0 };   // micro and below are not fixed by the environment
        if (IsSysAttribute(e, "version_info")) { values = full.Append(0).Append(0).ToArray(); return true; }

        if (e is AttributeExpr { Value: var inner } a && IsSysAttribute(inner, "version_info") && a.Attr is "major" or "minor")
        {
            values = new[] { a.Attr == "major" ? env.Major : env.Minor };
            return true;
        }
        if (e is SubscriptExpr { Value: var v } sub && IsSysAttribute(v, "version_info"))
        {
            var all = full.Append(0).Append(0).ToArray();
            if (sub.Index is ConstExpr { Kind: ConstKind.Int } idx && int.TryParse(idx.Text, out int i) && i >= 0 && i < all.Length)
            {
                values = new[] { all[i] };
                return true;
            }
            if (sub.Index is SliceExpr { Lower: null, Step: null, Upper: ConstExpr { Kind: ConstKind.Int } up } && int.TryParse(up.Text, out int n))
            {
                values = all.Take(Math.Clamp(n, 0, all.Length)).ToArray();
                return true;
            }
        }
        return false;
    }

    static bool TryIntTuple(Expr e, out int[] values)
    {
        values = Array.Empty<int>();
        if (e is ConstExpr { Kind: ConstKind.Int } c && int.TryParse(c.Text, out int single)) { values = new[] { single }; return true; }
        if (e is TupleExpr t)
        {
            var list = new List<int>();
            foreach (var item in t.Items)
            {
                if (item is ConstExpr { Kind: ConstKind.Int } ic && int.TryParse(ic.Text, out int v)) list.Add(v);
                else return false;
            }
            values = list.ToArray();
            return true;
        }
        return false;
    }

    /// <summary>Python tuple comparison: element-wise, and a shorter tuple that is a prefix of the other is the smaller one.</summary>
    static bool Compare(int[] left, string op, int[] right)
    {
        int cmp = 0;
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n && cmp == 0; i++) cmp = left[i].CompareTo(right[i]);
        if (cmp == 0) cmp = left.Length.CompareTo(right.Length);
        return op switch
        {
            "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, ">=" => cmp >= 0, "==" => cmp == 0, "!=" => cmp != 0,
            _ => false,
        };
    }
}
