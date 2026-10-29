using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>Field names inside the objects of the ORM: `Q(title__icontains=...)`, `F("price")`, `Sum("price")` and their kind.</summary>
public sealed partial class LanguageService
{
    /// <summary>Expressions whose first string is a field (or a path across relations): `F("price")`, `Sum("author__books")`.</summary>
    static readonly HashSet<string> FieldExpressions = new(StringComparer.Ordinal)
    {
        "F", "Sum", "Avg", "Min", "Max", "Count", "StdDev", "Variance", "Lower", "Upper", "Length", "Trim", "LTrim", "RTrim", "Abs", "Ceil", "Floor", "Round", "Reverse",
    };

    /// <summary>Expressions every string argument of which is a field: `Coalesce("name", "nickname")`.</summary>
    static readonly HashSet<string> FieldListExpressions = new(StringComparer.Ordinal) { "Coalesce", "Concat", "Greatest", "Least" };

    /// <summary>Calls that run a query, around the expressions in their arguments.</summary>
    static readonly HashSet<string> QueryAroundMethods = new(StringComparer.Ordinal)
    {
        "filter", "exclude", "get", "get_or_create", "update_or_create", "annotate", "alias", "aggregate", "update", "order_by", "values", "values_list",
        "distinct", "only", "defer", "count", "exists", "first", "last",
    };

    /// <summary>Calls that give names to what they compute: another expression of the same call may use them.</summary>
    static readonly HashSet<string> NamingMethods = new(StringComparer.Ordinal) { "annotate", "alias", "aggregate" };

    static bool IsExpression(string name) => name == "Q" || FieldExpressions.Contains(name) || FieldListExpressions.Contains(name);

    /// <summary>
    /// The model whose fields the expression <paramref name="expr"/> refers to: that of the query call around it (`Book.objects.filter(Q(...))`), or the
    /// model whose `class Meta` it is in (`CheckConstraint(check=Q(...))`). Null when it cannot be told: a chain that adds names of its own, a variable.
    /// </summary>
    ClassInfo? ModelAround(TypeEvaluator ev, ModuleInfo m, Node expr)
    {
        for (Node? n = m.ParentOf(expr); n != null; n = m.ParentOf(n))
        {
            switch (n)
            {
                case CallExpr { Func: AttributeExpr } c when CalleeName(c) is { } name && QueryAroundMethods.Contains(name):
                    return QueryModelOf(ev, m, c);
                case ClassDef { Name: "Meta" } when m.ParentOf(n) is ClassDef outer:
                    return ClassInfoOf(m, outer, ev) is { } info && ev.DjangoRoleOf(info) == DjangoClassRole.Model ? info : null;
                case FunctionDef or LambdaExpr or ClassDef:
                    return null;
            }
        }
        return null;
    }

    /// <summary>The names the call around an expression gives to what it computes (`annotate(total=Sum("price"), half=F("total") / 2)`).</summary>
    HashSet<string> NamesAddedAround(ModuleInfo m, Node expr)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (Node? n = m.ParentOf(expr); n != null; n = m.ParentOf(n))
            if (n is CallExpr c && CalleeName(c) is { } name && NamingMethods.Contains(name))
                foreach (var a in c.Args.Where(a => a.Name != null)) names.Add(a.Name!);
        return names;
    }

    /// <summary>Whether a call of this name is the ORM's and not the project's own: a class or function defined outside Django is left alone.</summary>
    static bool IsOrmExpression(ModuleInfo m, TypeEvaluator ev, CallExpr call)
    {
        string? origin = ev.TypeOf(m, call.Func) switch
        {
            ClassType c => c.Info.Module.Name,
            FunctionType f => f.Info.Module.Name,
            _ => null,
        };
        return origin == null ? m.Source.Contains("django", StringComparison.Ordinal) : origin.StartsWith("django", StringComparison.Ordinal);
    }

    /// <summary>The strings of an expression call that name fields: the first for most, all of them for `Coalesce` and its kind.</summary>
    static IEnumerable<(string Value, int Start, int End)> FieldStringsOf(CallExpr call, string name)
    {
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
        foreach (var arg in name is { } n && FieldListExpressions.Contains(n) ? positional : positional.Take(1))
            if (arg.Value is ConstExpr lit && FixtureModel.StringValue(lit) is { } value && FixtureModel.StringSpan(lit) is { } span)
                yield return (value, span.Start, span.End);
    }

    static bool LooksLikeFieldPath(string value) => value.Length > 0 && value.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    IEnumerable<Diag> DjangoExpressionDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
        {
            if (CalleeName(call) is not { } name || !IsExpression(name) || !IsOrmExpression(m, ev, call)) continue;
            if (ModelAround(ev, m, call) is not { } model) continue;
            var added = NamesAddedAround(m, call);

            if (name == "Q")
            {
                foreach (var arg in call.Args.Where(a => a.Name != null))
                {
                    if (added.Contains(arg.Name!.Split("__")[0]) || CheckLookup(ev, model, arg.Name!, allowLookups: true) is not { } bad) continue;
                    var finding = new Finding(arg.Start, arg.Name!.Length, $"Cannot resolve keyword '{bad.Bad}' into a field of {model.Name}. Choices are: {string.Join(", ", bad.Choices.Take(12))}", "django-field", FindingLevel.Warning);
                    if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
                }
                continue;
            }

            foreach (var (value, start, end) in FieldStringsOf(call, name))
            {
                if (value == "*" || !LooksLikeFieldPath(value.Replace("__", "_")) || added.Contains(value.Split("__")[0])) continue;
                if (value == "pk" || CheckLookup(ev, model, value, allowLookups: false) is not { } bad) continue;
                var finding = new Finding(start, end - start, $"Cannot resolve '{bad.Bad}' into a field of {model.Name}. Choices are: {string.Join(", ", bad.Choices.Take(12))}", "django-field", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
        }
    }
}
