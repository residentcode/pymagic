using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>The type of the selected expression, for Ctrl+Shift+P.</summary>
public sealed record ExpressionTypeInfo(string Type, string Text, Range Range);

/// <summary>Where the documentation of a name is on the web, for Shift+F1: Python's own pages for the standard library, PyPI for the rest.</summary>
public sealed record ExternalDocInfo(string Url, string Name);

/// <summary>What the editor asks about the code under the cursor: an expression's type and a name's documentation on the web.</summary>
public sealed partial class LanguageService
{
    /// <summary>The smallest expression that covers the selection (or the one at the cursor), with its inferred type.</summary>
    public ExpressionTypeInfo? ExpressionType(string uri, Range range)
    {
        var m = ModuleFor(uri);
        if (m == null) return null;
        var lines = Lines(m);
        int s = lines.OffsetOf(range.Start), e = lines.OffsetOf(range.End);
        Expr? best = null;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not Expr x || x is ErrorExpr || x.Start > s || x.End < e) continue;
            if (best == null || x.End - x.Start <= best.End - best.Start) best = x;
        }
        if (best == null) return null;
        var ev = Current().Workspace.Evaluator;
        var t = ev.TypeOf(m, best);
        string text = SourceText(m, best);
        return new ExpressionTypeInfo(t is UnknownType ? "Unknown" : FormatType(ev, t), text.Length > 80 ? text[..77] + "..." : text, lines.RangeOf(best.Start, best.End));
    }

    /// <summary>The web page that documents the name at the cursor: docs.python.org for the standard library and the builtins,
    /// the project's page on PyPI for an installed package; null for the project's own code.</summary>
    public ExternalDocInfo? ExternalDoc(string uri, Position pos)
    {
        var m = ModuleFor(uri);
        if (m == null) return null;
        int offset = Lines(m).OffsetOf(pos);
        var ev = Current().Workspace.Evaluator;
        var target = NameLikeAt(m, offset);
        if (target == null) return null;
        var t = ev.TypeOf(m, target);
        (ModuleInfo? module, string? qual) = t switch
        {
            FunctionType f => (f.Info.Module, f.Info.Owner != null ? f.Info.Owner.Name + "." + f.Info.Name : f.Info.Name),
            OverloadedType { Overloads: [var first, ..] } => (first.Info.Module, first.Info.Owner != null ? first.Info.Owner.Name + "." + first.Info.Name : first.Info.Name),
            ClassType c => (c.Info.Module, c.Info.Name),
            ModuleType mt => (mt.Module, null),
            _ => (null, null),
        };
        if (module == null) return null;
        string name = qual == null ? module.Name : module.Name + "." + qual;
        string path = (module.Path ?? "").Replace('\\', '/');
        bool installed = path.Contains("/site-packages/", StringComparison.OrdinalIgnoreCase);
        bool project = !installed && !module.IsStub && !path.Contains("/Lib/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/lib/python", StringComparison.OrdinalIgnoreCase);
        if (installed || module.IsStub && path.Contains("/stubs/", StringComparison.OrdinalIgnoreCase))
            return new ExternalDocInfo("https://pypi.org/project/" + module.Name.Split('.')[0].Replace('_', '-') + "/", name);
        if (project) return null;
        if (module.Name == "builtins")
        {
            // functions are on one page, the methods of the built-in types on another
            string page = t is ClassType || qual?.Contains('.') == true ? "stdtypes.html" : "functions.html";
            return new ExternalDocInfo($"https://docs.python.org/3/library/{page}#{qual}", qual ?? "builtins");
        }
        return new ExternalDocInfo($"https://docs.python.org/3/library/{module.Name}.html" + (qual != null ? "#" + name : ""), name);
    }
}
