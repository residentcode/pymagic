using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>One choice of Generate… (Alt+Insert): edits of the open file, or a new test file (its text, or what is added to it when it is there).</summary>
public sealed record GenerateOption(string Title, string Kind, Dictionary<string, List<TextEdit>>? Changes, string? NewFile = null, string? NewText = null);

public sealed partial class LanguageService
{
    // the methods of object that are worth overriding; the others (__init_subclass__, __reduce__, ...) are not offered
    static readonly HashSet<string> ObjectOverrides = new(StringComparer.Ordinal) { "__str__", "__repr__", "__eq__", "__hash__" };

    // classes that make their own __init__ from the fields
    static readonly Regex FieldClass = new(@"(^|\.)(dataclass|define|frozen|attrs|s|BaseModel|NamedTuple|TypedDict|Enum|IntEnum|StrEnum|Model|Schema|Struct)\b", RegexOptions.Compiled);

    /// <summary>
    /// What Generate… offers where the cursor is: in a class, a constructor from its annotated fields, properties for its `self._x` attributes and the
    /// methods of its bases to override; in a function or class at the top of a module, a test for it in `tests/test_&lt;module&gt;.py` (or beside the module).
    /// </summary>
    public List<GenerateOption> Generate(string uri, Position position)
    {
        var result = new List<GenerateOption>();
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return result;
        int at = Lines(m).OffsetOf(position);
        var cd = AstChildren.Descendants(m.Ast).OfType<ClassDef>().Where(c => c.Start <= at && at <= c.End).OrderBy(c => c.End - c.Start).FirstOrDefault();
        if (cd != null && cd.Body.Count > 0)
        {
            InitOption(m, uri, cd, result);
            PropertyOptions(m, uri, cd, result);
            OverrideOptions(snap, m, uri, cd, result);
        }
        TestOption(snap, m, at, result);
        return result;
    }

    /// <summary>Where a class's new members go: after its last line, at the indentation of its body; null for a body on the `class` line.</summary>
    static (int At, string Indent, string Lead)? ClassEnd(ModuleInfo m, ClassDef cd)
    {
        string text = m.Source;
        var first = cd.Body[0];
        if (text[LineStart(text, first.Start)..first.Start].Any(c => c is not (' ' or '\t'))) return null;
        int last = cd.End;
        while (last > first.Start && text[last - 1] is '\n' or '\r') last--;
        int at = LineEndInclusive(text, last);
        return (at, LineIndent(text, first.Start), at == 0 || text[at - 1] is not ('\n' or '\r') ? "\n" : "");
    }

    static string Newlines(ModuleInfo m, string s) => m.Source.Contains("\r\n") ? s.Replace("\n", "\r\n") : s;

    Dictionary<string, List<TextEdit>> Insert(ModuleInfo m, string uri, int at, string text) =>
        new() { [uri] = new List<TextEdit> { new(Lines(m).RangeOf(at, at), Newlines(m, text)) } };

    static string Indented(string indent, IEnumerable<string> lines) => string.Concat(lines.Select(l => l.Length == 0 ? "\n" : indent + l + "\n"));

    void InitOption(ModuleInfo m, string uri, ClassDef cd, List<GenerateOption> result)
    {
        if (cd.Body.OfType<FunctionDef>().Any(f => f.Name == "__init__")) return;
        string src = m.Source;
        if (cd.Decorators.Any(d => FieldClass.IsMatch(src[d.Start..d.End])) || cd.Bases.Any(b => b.Name == null && FieldClass.IsMatch(src[b.Value.Start..b.Value.End]))) return;
        var fields = cd.Body.OfType<AnnAssignStmt>().Where(a => a.Target is NameExpr n && !n.Name.StartsWith("__") && !src[a.Annotation.Start..a.Annotation.End].StartsWith("ClassVar"))
            .Select(a => (Name: ((NameExpr)a.Target).Name, Type: src[a.Annotation.Start..a.Annotation.End], Default: a.Value is null ? null : src[a.Value.Start..a.Value.End])).ToList();
        if (ClassEnd(m, cd) is not { } end) return;
        // the ones with a default after the ones without, as Python wants them
        var ordered = fields.Where(f => f.Default == null).Concat(fields.Where(f => f.Default != null)).ToList();
        var parameters = string.Concat(ordered.Select(f => $", {f.Name}: {f.Type}" + (f.Default != null ? $" = {f.Default}" : "")));
        var body = new List<string>();
        if (cd.Bases.Any(b => b.Name == null)) body.Add("super().__init__()");
        body.AddRange(fields.Select(f => $"self.{f.Name} = {f.Name}"));
        if (body.Count == 0) body.Add("pass");
        string text = end.Lead + "\n" + Indented(end.Indent, new[] { $"def __init__(self{parameters}):" }.Concat(body.Select(b => "    " + b)));
        result.Add(new GenerateOption(fields.Count > 0 ? $"Constructor (__init__) of {string.Join(", ", fields.Select(f => f.Name))}" : "Constructor (__init__)", "init", Insert(m, uri, end.At, text)));
    }

    void PropertyOptions(ModuleInfo m, string uri, ClassDef cd, List<GenerateOption> result)
    {
        var defined = cd.Body.OfType<FunctionDef>().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var f in cd.Body.OfType<FunctionDef>())
            foreach (var n in AstChildren.Descendants(f))
            {
                Expr? target = n switch { AssignStmt a => null, AnnAssignStmt a => a.Target, _ => null };
                var targets = n is AssignStmt s ? s.Targets : target != null ? new[] { target } : Array.Empty<Expr>();
                foreach (var t in targets)
                    if (t is AttributeExpr { Value: NameExpr { Name: "self" }, Attr: var attr } && attr.StartsWith('_') && !attr.StartsWith("__") && attr.Length > 1
                        && !defined.Contains(attr[1..]) && !names.Contains(attr))
                        names.Add(attr);
            }
        if (names.Count == 0 || ClassEnd(m, cd) is not { } end) return;
        string Getter(string attr) => Indented(end.Indent, new[] { "", "@property", $"def {attr[1..]}(self):", $"    return self.{attr}" });
        string Setter(string attr) => Indented(end.Indent, new[] { "", $"@{attr[1..]}.setter", $"def {attr[1..]}(self, value):", $"    self.{attr} = value" });
        string list = string.Join(", ", names.Select(n => n[1..]));
        result.Add(new GenerateOption($"Getters of {list}", "getters", Insert(m, uri, end.At, end.Lead + string.Concat(names.Select(Getter)))));
        result.Add(new GenerateOption($"Getters and setters of {list}", "properties", Insert(m, uri, end.At, end.Lead + string.Concat(names.Select(n => Getter(n) + Setter(n))))));
    }

    void OverrideOptions(Snapshot snap, ModuleInfo m, string uri, ClassDef cd, List<GenerateOption> result)
    {
        var ev = snap.Workspace.Evaluator;
        if (ClassInfoOf(m, cd, ev) is not { } info || KnownMro(info, ev) is not { } mro || ClassEnd(m, cd) is not { } end) return;
        var own = cd.Body.OfType<FunctionDef>().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(own, StringComparer.Ordinal);
        foreach (var c in mro.Skip(1))
        {
            bool isObject = c.Info.Name == "object";
            foreach (var (name, sym) in c.Info.Scope.Symbols.OrderBy(kv => kv.Value.Declarations.FirstOrDefault()?.Node.Start ?? 0))
            {
                if (sym.Declarations.Count == 0 || sym.Declarations[^1].Node is not FunctionDef fd || seen.Contains(name)) continue;
                if (isObject ? !ObjectOverrides.Contains(name) : name.StartsWith("__") && !name.EndsWith("__") || name == "__init_subclass__" || name == "__class_getitem__") continue;
                string src = c.Info.Module.Source;
                if (fd.Decorators.Any(d => src[d.Start..d.End] is "overload" or "typing.overload")) continue;
                seen.Add(name);
                string text = end.Lead + "\n" + Indented(end.Indent, OverrideOf(fd, src).Split('\n'));
                result.Add(new GenerateOption($"Override {name}() of {c.Info.Name}", "override", Insert(m, uri, end.At, text)));
            }
        }
    }

    /// <summary>`def name(...)` with the base's parameters and a body that calls the base, or raises for an abstract one.</summary>
    static string OverrideOf(FunctionDef f, string src)
    {
        string parameters = f.Params.Count == 0 ? "" : Regex.Replace(src[f.Params[0].Start..f.Params[^1].End], @"\s*\n\s*", " ");
        // a stub's defaults are `...`: the call passes them on, so they stay as written
        string returns = f.Returns != null ? " -> " + src[f.Returns.Start..f.Returns.End] : "";
        var decorators = f.Decorators.Select(d => src[d.Start..d.End]).Where(d => d is "staticmethod" or "classmethod" or "property" || d.EndsWith(".setter")).Select(d => "@" + d);
        bool isStatic = decorators.Contains("@staticmethod");
        bool abstractDef = f.Decorators.Any(d => src[d.Start..d.End].EndsWith("abstractmethod"));
        var args = new List<string>();
        bool keywordOnly = false;
        foreach (var (p, i) in f.Params.Select((p, i) => (p, i)))
        {
            if (i == 0 && !isStatic && p.Kind == ParamKind.Normal) continue;   // self or cls
            switch (p.Kind)
            {
                case ParamKind.VarArgs: args.Add("*" + p.Name); keywordOnly = true; break;
                case ParamKind.KwArgs: args.Add("**" + p.Name); break;
                case ParamKind.KwOnlySeparator: keywordOnly = true; break;
                case ParamKind.Slash: break;
                default: args.Add(keywordOnly ? $"{p.Name}={p.Name}" : p.Name); break;
            }
        }
        string call = decorators.Contains("@property") ? $"super().{f.Name}" : $"super().{f.Name}({string.Join(", ", args)})";
        string body = abstractDef ? "raise NotImplementedError" : f.Returns is NameExpr { Name: "None" } || f.Name == "__init__" ? call : "return " + call;
        if (isStatic) body = abstractDef ? body : "pass";
        return string.Join("\n", decorators.Append($"{(f.IsAsync ? "async " : "")}def {f.Name}({parameters}){returns}:").Append("    " + (f.IsAsync && !abstractDef && !isStatic ? body.Replace("super()", "await super()") : body)));
    }

    /// <summary>A test for the function or class at the top of the module the cursor is in.</summary>
    void TestOption(Snapshot snap, ModuleInfo m, int at, List<GenerateOption> result)
    {
        var top = m.Ast.Body.FirstOrDefault(s => s.Start <= at && at <= s.End);
        string? name = top switch { FunctionDef f => f.Name, ClassDef c => c.Name, _ => null };
        string? path = snap.UriOf.TryGetValue(m, out var u) ? PathOfUri(u) : m.Path;
        if (name == null || name.StartsWith("test", StringComparison.Ordinal) || path == null || m.Name.Length == 0) return;
        string file = System.IO.Path.GetFileNameWithoutExtension(path);
        if (file.StartsWith("test_", StringComparison.Ordinal) || file == "conftest") return;
        // the folder marked as test sources, else the project's tests folder (beside the top package), else beside the module
        string? dir = System.IO.Path.GetDirectoryName(path);
        int up = m.Name.Split('.').Length - (m.IsPackage ? 0 : 1);
        string? root = dir;
        for (int i = 0; i < up && root != null; i++) root = System.IO.Path.GetDirectoryName(root);
        string folder = ProjectFolders.TestRoots.FirstOrDefault(Directory.Exists)
            ?? (root != null && Directory.Exists(System.IO.Path.Combine(root, "tests")) ? System.IO.Path.Combine(root, "tests") : dir!);
        string target = System.IO.Path.Combine(folder, $"test_{(m.IsPackage ? m.Name.Split('.')[^1] : file)}.py");
        string snake = Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
        string test = top is ClassDef ? $"def test_{snake}():\n    obj = {name}()\n    assert obj\n" : $"def test_{snake}():\n    assert {name}()\n";
        string import = $"from {m.Name} import {name}\n";
        string existing = File.Exists(target) ? File.ReadAllText(target) : "";
        if (Regex.IsMatch(existing, $@"^def test_{Regex.Escape(snake)}\(", RegexOptions.Multiline)) return;
        // the whole file as it becomes: the import goes after the imports it has, the test at the end
        string text;
        if (existing.Length == 0) text = import + "\n\n" + test;
        else
        {
            string nl = existing.Contains("\r\n") ? "\r\n" : "\n";
            var lines = existing.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
            if (!lines.Contains(import.TrimEnd()))
            {
                int last = lines.FindLastIndex(l => l.StartsWith("import ", StringComparison.Ordinal) || l.StartsWith("from ", StringComparison.Ordinal));
                lines.Insert(last + 1, import.TrimEnd());
            }
            text = (string.Join("\n", lines) + "\n\n\n" + test).Replace("\n", nl);
        }
        result.Add(new GenerateOption($"Test of {name} in {System.IO.Path.GetRelativePath(root ?? folder, target).Replace('\\', '/')}", "test", null, UriOfPath(target), text));
    }
}
