using System.Text;
using PyModule = PyMagic.Core.Syntax.Module;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Code actions: fixes for the diagnostics above, and "optimize imports".</summary>
public sealed partial class LanguageService
{
    public const string QuickFixKind = "quickfix";
    public const string OrganizeImportsKind = "source.organizeImports";

    /// <summary>Modules whose public names are offered as imports for an unresolved name, after the project's own modules.</summary>
    static readonly string[] CommonModules =
    {
        "typing", "collections", "collections.abc", "dataclasses", "enum", "pathlib", "functools", "itertools", "datetime",
        "abc", "re", "json", "os", "sys", "math", "random", "time", "logging", "subprocess", "argparse", "contextlib",
    };

    sealed record UnusedImport(string Name, int Start, int End, ImportAlias Alias, Stmt Statement);

    // ------------------------------------------------------------ public API

    public IReadOnlyList<CodeAction> CodeActions(string uri, Range range)
    {
        if (_templateDocs.ContainsKey(uri)) return CreateTemplateActions(uri, range).ToList();
        if (_featureDocs.ContainsKey(uri)) return FeatureActions(uri, range);
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<CodeAction>();
        var lines = Lines(m);
        int rs = lines.OffsetOf(range.Start), re = lines.OffsetOf(range.End);
        var ev = snap.Workspace.Evaluator;
        var actions = new List<CodeAction>();

        WorkspaceEdit One(params Edit[] edits) =>
            new(new Dictionary<string, List<TextEdit>> { [uri] = edits.Select(e => new TextEdit(lines.RangeOf(e.Start, e.End), e.Text)).ToList() });
        void Add(string title, bool preferred, params Edit[] edits) => actions.Add(new CodeAction(title, QuickFixKind, One(edits), preferred));

        foreach (var d in Collect(m, ev))
        {
            int ds = d.D.Start, de = d.D.Start + Math.Max(d.D.Length, 1);
            if (ds > re || rs > de) continue;

            switch (d.Code)
            {
                case "unresolved-reference":
                    UnresolvedFixes(snap, m, ds, Add);
                    break;
                case "unbound-local":
                {
                    // `count += 1` inside a method makes `count` local; `self.count` was probably meant
                    var local = AstChildren.Descendants(m.Ast).OfType<NameExpr>().FirstOrDefault(n => n.Start == ds);
                    if (local != null) SelfFix(snap, m, local, Add);
                    break;
                }
                case "missing-attribute":
                    MissingAttributeFixes(snap, m, ds, de, Add, actions);
                    break;
                case "unresolved-import" or "unresolved-import-name" or "package-not-installed":
                    ImportFixes(snap, m, ev, ds, actions, Add);
                    break;
                case "unreachable":
                {
                    var stmt = AstChildren.Descendants(m.Ast).OfType<Stmt>().FirstOrDefault(s => s.Start == ds);
                    if (stmt != null) Add("Remove unreachable code", false, DeleteStatement(m, stmt));
                    break;
                }
                case "unused-import":
                {
                    var u = UnusedImports(m).FirstOrDefault(x => x.Start == ds);
                    if (u != null) Add($"Remove unused import '{u.Name}'", true, RemoveAliases(m, u.Statement, new[] { u.Alias }));
                    break;
                }
                default:
                    if (d.Code != null) InspectionFixes(snap, m, uri, d.Code, ds, de, Add, actions);
                    break;
            }
        }


        // intentions: rewrites at the cursor that need no diagnostic
        try { IntentionActions(snap.Workspace, snap.ByUri.Values, m, ev, rs, re, (title, edits) => actions.Add(new CodeAction(title, RewriteKind, One(edits.OrderBy(e => e.Start).ToArray()), false))); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { /* the other actions still work */ }

        // refactorings: the page asks for a name where one is needed and then calls pymagic/refactor
        try
        {
            foreach (var option in RefactorOptions(uri, range))
                actions.Add(new CodeAction(option.Title, RefactorActionKind(option.Kind), null, false,
                    new LspCommand(option.Title, "pymagic.refactor", new object[] { option.Kind, uri, range, option.DefaultName ?? "", option.All, option.Members ?? (object)Array.Empty<string>(), option.Chosen ?? (object)Array.Empty<string>() })));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { /* the quick fixes above still work */ }
        actions.AddRange(CreateTemplateActions(uri, range));
        actions.AddRange(ClassBasedViewActions(snap, m, uri, rs));
        var organized = OrganizedText(snap, m);
        if (organized != null && organized != m.Source)
            actions.Add(new CodeAction("Optimize imports", OrganizeImportsKind, new WorkspaceEdit(new Dictionary<string, List<TextEdit>> { [uri] = DiffEdit(m, organized) })));
        return actions;
    }

    // ------------------------------------------------------------ edits

    sealed record Edit(int Start, int End, string Text);

    static string LineIndent(string text, int offset)
    {
        int ls = offset;
        while (ls > 0 && text[ls - 1] != '\n' && text[ls - 1] != '\r') ls--;
        int i = ls;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return text[ls..i];
    }

    static int LineStart(string text, int offset)
    {
        while (offset > 0 && text[offset - 1] != '\n' && text[offset - 1] != '\r') offset--;
        return offset;
    }

    static int LineEndInclusive(string text, int offset)
    {
        while (offset < text.Length && text[offset] != '\n' && text[offset] != '\r') offset++;
        if (offset < text.Length && text[offset] == '\r') offset++;
        if (offset < text.Length && text[offset] == '\n') offset++;
        return offset;
    }

    static IReadOnlyList<Stmt>? BodyContaining(Node parent, Stmt s)
    {
        bool In(IReadOnlyList<Stmt> l) => l.Any(x => ReferenceEquals(x, s));
        IReadOnlyList<Stmt>? Pick(params IReadOnlyList<Stmt>[] lists) => lists.FirstOrDefault(In);
        return parent switch
        {
            PyModule x => x.Body,
            IfStmt x => Pick(x.Body, x.Orelse),
            WhileStmt x => Pick(x.Body, x.Orelse),
            ForStmt x => Pick(x.Body, x.Orelse),
            TryStmt x => Pick(x.Body, x.Orelse, x.Finally),
            ExceptHandler x => x.Body,
            WithStmt x => x.Body,
            FunctionDef x => x.Body,
            ClassDef x => x.Body,
            MatchCase x => x.Body,
            _ => null,
        };
    }

    /// <summary>Deletes a statement together with its line(s); a statement that is the whole body of a block becomes `pass`.</summary>
    static Edit DeleteStatement(ModuleInfo m, Stmt s)
    {
        string text = m.Source;
        var parent = m.ParentOf(s);
        var body = parent == null ? null : BodyContaining(parent, s);
        if (body is { Count: 1 } && parent is not PyModule) return new Edit(s.Start, s.End, "pass");

        int ls = LineStart(text, s.Start);
        int le = LineEndInclusive(text, s.End);
        bool wholeLines = text[ls..s.Start].All(c => c is ' ' or '\t') && text[s.End..le].All(c => c is ' ' or '\t' or '\r' or '\n');
        return wholeLines ? new Edit(ls, le, "") : new Edit(s.Start, s.End, "");
    }

    static string AliasText(ImportAlias a) => a.AsName != null ? $"{a.Name} as {a.AsName}" : a.Name;

    /// <summary>The import statement without these names; the statement itself goes when nothing is left.</summary>
    static Edit RemoveAliases(ModuleInfo m, Stmt stmt, IReadOnlyCollection<ImportAlias> remove)
    {
        var names = stmt switch { ImportStmt i => i.Names, ImportFromStmt f => f.Names, _ => Array.Empty<ImportAlias>() };
        var keep = names.Where(a => !remove.Any(r => ReferenceEquals(r, a))).ToList();
        if (keep.Count == 0) return DeleteStatement(m, stmt);
        string list = string.Join(", ", keep.Select(AliasText));
        string text = stmt is ImportFromStmt from
            ? $"from {new string('.', from.Level)}{from.Module} import {list}"
            : $"import {list}";
        return new Edit(stmt.Start, stmt.End, text);
    }

    // ------------------------------------------------------------ unused imports

    IEnumerable<UnusedImport> UnusedImports(ModuleInfo m)
    {
        if (m.IsStub || m.IsPackage) yield break;   // an __init__ module re-exports what it imports

        // names mentioned in string constants (string annotations, __all__) count as used
        var inStrings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in AstChildren.Descendants(m.Ast))
            if (n is ConstExpr { Kind: ConstKind.Str } c)
                foreach (Match w in Regex.Matches(c.Text, @"[\p{L}_][\p{L}\p{N}_]*")) inStrings.Add(w.Value);

        foreach (var scope in AllScopes(m.Semantics.ModuleScope))
            foreach (var sym in scope.Symbols.Values)
            {
                if (sym.Redirect != null || sym.Declarations.Count != 1 || sym.References.Count > 0) continue;
                var decl = sym.Declarations[0];
                if (decl.Kind != DeclKind.Import || decl.Node is not ImportAlias alias) continue;
                if (inStrings.Contains(sym.Name)) continue;
                if (m.ParentOf(alias) is not Stmt stmt) continue;
                if (stmt is ImportFromStmt { IsStar: true } or ImportFromStmt { Module: "__future__" }) continue;
                var (s, e) = DeclSpan(m, decl);
                yield return new UnusedImport(sym.Name, s, e, alias, stmt);
            }
    }

    // ------------------------------------------------------------ unresolved names

    void UnresolvedFixes(Snapshot snap, ModuleInfo m, int offset, Action<string, bool, Edit[]> add)
    {
        var name = AstChildren.Descendants(m.Ast).OfType<NameExpr>().FirstOrDefault(n => n.Start == offset);
        if (name == null) return;
        var ev = snap.Workspace.Evaluator;

        SelfFix(snap, m, name, add);

        // typo of a name that is in scope
        foreach (var near in Closest(name.Name, NamesInScope(m, name.Start), 2))
            add($"Change to '{near}'", false, new[] { new Edit(name.Start, name.End, near) });

        // import it
        int offered = 0;
        if (snap.Workspace.GetModule(name.Name) is { } mod && !mod.Name.Contains('.') && offered < 5)
        {
            add($"Import module '{name.Name}'", true, new[] { ImportEdit(m, null, name.Name) });
            offered++;
        }
        var seen = new HashSet<string>();
        foreach (var candidate in ImportCandidates(snap, m, name.Name))
        {
            if (offered >= 5 || !seen.Add(candidate)) continue;
            add($"Import '{name.Name}' from '{candidate}'", offered == 0, new[] { ImportEdit(m, candidate, name.Name) });
            offered++;
        }

        // create it: a class when it is written like one or used as one, else a function when it is called
        var parent = m.ParentOf(name);
        var call = parent is CallExpr c && ReferenceEquals(c.Func, name) ? c : null;
        var grand = parent == null ? null : m.ParentOf(parent);
        bool usedAsClass = parent is Argument { Name: null, Stars: 0 } && grand is ClassDef
            || parent is ExceptHandler
            || parent is TupleExpr && grand is ExceptHandler
            || parent is Argument arg && grand is CallExpr { Func: NameExpr { Name: "isinstance" or "issubclass" } } ic && ic.Args.Count == 2 && ReferenceEquals(ic.Args[1], arg);
        if (usedAsClass || char.IsUpper(name.Name[0]) && name.Name.Any(char.IsLower))
            add($"Create class '{name.Name}'", false, new[] { CreateClassEdit(m, name, call, parent is ExceptHandler || parent is TupleExpr) });
        if (call != null)
            add($"Create function '{name.Name}'", false, new[] { CreateFunctionEdit(m, name, call) });
    }

    /// <summary>The parameter names a call suggests: keyword names, plain names passed, else arg0, arg1...</summary>
    static List<string> ParamsFromCall(CallExpr call)
    {
        var args = new List<string>();
        int n = 0;
        foreach (var a in call.Args)
        {
            string p = a.Stars switch
            {
                2 => "**kwargs",
                1 => "*args",
                _ => a.Name ?? (a.Value is NameExpr ne && !args.Contains(ne.Name) ? ne.Name : $"arg{n}"),
            };
            if (!args.Contains(p)) args.Add(p);
            n++;
        }
        return args;
    }

    /// <summary>Text put above the top-level statement that holds a node, with blank lines around it.</summary>
    static Edit TopLevelInsert(ModuleInfo m, Node inner, string definition)
    {
        Node top = inner;
        for (var p = m.ParentOf(top); p != null && p is not PyModule; p = m.ParentOf(top)) top = p;
        int at = LineStart(m.Source, top.Start);
        bool afterCode = at > 0 && m.Source[LineStart(m.Source, at - 1)..at].Trim().Length > 0;
        return new Edit(at, at, (afterCode ? "\n\n" : "") + definition + "\n\n\n");
    }

    static Edit CreateClassEdit(ModuleInfo m, NameExpr name, CallExpr? call, bool exception)
    {
        var args = call == null ? new List<string>() : ParamsFromCall(call);
        string body = args.Count == 0 ? "    pass"
            : $"    def __init__(self, {string.Join(", ", args)}):\n" + string.Join("\n", args.Where(a => !a.StartsWith('*')).Select(a => $"        self.{a} = {a}"))
              + (args.All(a => a.StartsWith('*')) ? "        pass" : "");
        return TopLevelInsert(m, name, $"class {name.Name}{(exception ? "(Exception)" : "")}:\n{body}");
    }

    IEnumerable<string> ImportCandidates(Snapshot snap, ModuleInfo current, string name)
    {
        var ev = snap.Workspace.Evaluator;
        foreach (var other in snap.Project)
        {
            if (ReferenceEquals(other, current) || other.Name.StartsWith("__untitled__") || other.IsPackage && other.Source.Length == 0) continue;
            if (other.Semantics.ModuleScope.Symbols.TryGetValue(name, out var sym) && sym.Redirect == null
                && sym.Declarations.Any(d => d.Kind != DeclKind.Import))
                yield return other.Name;
        }
        foreach (var modName in CommonModules)
        {
            var mod = snap.Workspace.GetModule(modName);
            if (mod == null || name.StartsWith('_')) continue;
            var sym = ev.FindModuleSymbol(mod, name);
            if (sym != null && sym.Declarations.Count > 0) yield return modName;
        }
    }

    /// <summary>`import x` or `from module import x`, merged into an existing `from module import ...` when there is one.</summary>
    Edit ImportEdit(ModuleInfo m, string? module, string name)
    {
        string text = m.Source;
        var body = m.Ast.Body;

        if (module != null)
        {
            foreach (var st in body)
                if (st is ImportFromStmt { Level: 0, IsStar: false } f && f.Module == module && f.Names.Count > 0)
                    return new Edit(f.Names[^1].End, f.Names[^1].End, ", " + name);
        }

        string line = module == null ? $"import {name}" : $"from {module} import {name}";

        // after the last import of the leading import block (and a module docstring); else at the top
        int idx = 0;
        if (body.Count > 0 && body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) idx = 1;
        Stmt? last = idx == 1 ? body[0] : null;
        bool sawImport = false;
        for (; idx < body.Count && body[idx] is ImportStmt or ImportFromStmt; idx++) { last = body[idx]; sawImport = true; }

        if (last == null) return new Edit(0, 0, line + "\n" + (text.Length > 0 && text[0] != '\n' ? "\n" : ""));
        int at = LineEndInclusive(text, last.End);
        bool needsNewline = at > 0 && at <= text.Length && text[at - 1] != '\n' && text[at - 1] != '\r';
        string trailer = !sawImport && at < text.Length && text[at] != '\n' && text[at] != '\r' ? "\n" : "";
        return new Edit(at, at, (needsNewline ? "\n" : "") + (sawImport ? "" : "\n") + line + "\n" + trailer);
    }

    static Edit CreateFunctionEdit(ModuleInfo m, NameExpr name, CallExpr call) =>
        TopLevelInsert(m, call, $"def {name.Name}({string.Join(", ", ParamsFromCall(call))}):\n    raise NotImplementedError");

    /// <summary>`foo` inside a method where `self.foo` exists.</summary>
    void SelfFix(Snapshot snap, ModuleInfo m, NameExpr name, Action<string, bool, Edit[]> add)
    {
        FunctionDef? method = null;
        for (var p = m.ParentOf(name); p != null; p = m.ParentOf(p))
            if (p is FunctionDef f && m.ParentOf(f) is ClassDef) { method = f; break; }
        if (method == null || m.ParentOf(method) is not ClassDef cls) return;
        if (method.Decorators.Any(d => d is NameExpr { Name: "staticmethod" })) return;
        var first = method.Params.FirstOrDefault(p => p.Kind == ParamKind.Normal);
        if (first == null) return;

        var ev = snap.Workspace.Evaluator;
        var classScope = m.Semantics.ScopeOf(cls);
        var sym = classScope?.Parent?.Symbols.GetValueOrDefault(cls.Name);
        if (sym == null || ev.TypeOfSymbol(sym) is not ClassType { IsInstance: false } ct) return;
        if (ev.MemberNames(ct.AsInstance()).Any(x => x.Name == name.Name))
            add($"Add '{first.Name}.'", true, new[] { new Edit(name.Start, name.Start, first.Name + ".") });
    }

    // ------------------------------------------------------------ misspelled attributes

    void MissingAttributeFixes(Snapshot snap, ModuleInfo m, int start, int end, Action<string, bool, Edit[]> add, List<CodeAction> actions)
    {
        var access = AstChildren.Descendants(m.Ast).OfType<AttributeExpr>()
            .FirstOrDefault(a => a.End - a.Attr.Length == start && a.End == end);
        if (access == null) return;
        var ev = snap.Workspace.Evaluator;
        var recv = ev.TypeOf(m, access.Value);
        var names = ev.MemberNames(recv).Select(x => x.Name)
            .Where(x => access.Attr.StartsWith("__") || !x.StartsWith("__")).ToList();
        foreach (var near in Closest(access.Attr, names, 3))
            add($"Change to '{near}'", false, new[] { new Edit(start, end, near) });

        // create it in the project's class: a method when it is called, else an attribute set in __init__
        if (recv is not ClassType { Info: var info } owner || !snap.UriOf.TryGetValue(info.Module, out var ownerUri)
            || !snap.Project.Contains(info.Module) || info.Module.IsStub) return;
        var cls = info.Node;
        string indent = cls.Body.Count > 0 ? LineIndent(info.Module.Source, cls.Body[0].Start) : LineIndent(info.Module.Source, cls.Start) + "    ";
        string inner = indent + "    ";
        var call = m.ParentOf(access) is CallExpr c && ReferenceEquals(c.Func, access) ? c : null;
        bool onlyPass = cls.Body.Count == 1 && cls.Body[0] is PassStmt;
        Edit edit;
        string title;
        if (call != null)
        {
            var args = ParamsFromCall(call);
            string head = owner.IsInstance ? "self" : "cls";
            string def = (owner.IsInstance ? "" : indent + "@classmethod\n") + $"{indent}def {access.Attr}({string.Join(", ", args.Prepend(head))}):\n{inner}raise NotImplementedError\n";
            edit = AtClassEnd(info.Module, cls, def, onlyPass);
            title = $"Create method '{access.Attr}' in '{info.Name}'";
        }
        else
        {
            var init = cls.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
            if (owner.IsInstance && init != null && init.Params.FirstOrDefault()?.Name is { } self)
            {
                int at = LineEndInclusive(info.Module.Source, CodeEnd(info.Module.Source, init.Body[^1].End));
                string bodyIndent = LineIndent(info.Module.Source, init.Body[^1].Start);
                bool atEof = at == info.Module.Source.Length && at > 0 && info.Module.Source[at - 1] != '\n';
                edit = new Edit(at, at, (atEof ? "\n" : "") + $"{bodyIndent}{self}.{access.Attr} = None\n");
            }
            else edit = AtClassEnd(info.Module, cls, $"{indent}{access.Attr} = None\n", onlyPass);
            title = $"Create attribute '{access.Attr}' in '{info.Name}'";
        }
        var ownerLines = Lines(info.Module);
        actions.Add(new CodeAction(title, QuickFixKind, new WorkspaceEdit(new Dictionary<string, List<TextEdit>>
        {
            [ownerUri] = new() { new TextEdit(ownerLines.RangeOf(edit.Start, edit.End), edit.Text) },
        })));
    }

    /// <summary>Where a statement's own text ends: its End can run on over the blank lines after it.</summary>
    static int CodeEnd(string text, int end)
    {
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    /// <summary>Text added as the last member of a class (in place of a lone `pass`), a blank line before a method.</summary>
    static Edit AtClassEnd(ModuleInfo m, ClassDef cls, string member, bool replacePass)
    {
        if (replacePass)
        {
            var pass = cls.Body[0];
            return new Edit(LineStart(m.Source, pass.Start), LineEndInclusive(m.Source, pass.End), member);
        }
        int at = LineEndInclusive(m.Source, CodeEnd(m.Source, cls.Body[^1].End));
        bool atEof = at == m.Source.Length && at > 0 && m.Source[at - 1] != '\n';
        bool method = member.TrimStart().StartsWith("def ") || member.TrimStart().StartsWith('@');
        return new Edit(at, at, (atEof ? "\n" : "") + (method ? "\n" : "") + member);
    }

    // ------------------------------------------------------------ helpers

    static IEnumerable<string> NamesInScope(ModuleInfo m, int offset)
    {
        var names = new HashSet<string>();
        bool innermost = true;
        for (var s = m.Semantics.InnermostScopeAt(offset); s != null; s = s.Parent)
        {
            if (s.Kind != ScopeKind.Class || innermost)
                foreach (var sym in s.Symbols.Values)
                    if (sym.Declarations.Count > 0) names.Add(sym.Name);
            innermost = false;
        }
        foreach (var b in Builtins.Names) names.Add(b);
        return names;
    }

    /// <summary>Candidates within a small edit distance (scaled by length), nearest first.</summary>
    static IEnumerable<string> Closest(string word, IEnumerable<string> candidates, int take)
    {
        int limit = word.Length <= 3 ? 1 : word.Length <= 7 ? 2 : 3;
        return candidates.Where(c => c != word && !(c.StartsWith('_') && !word.StartsWith('_')))
            .Select(c => (Name: c, Dist: Distance(word, c, limit)))
            .Where(x => x.Dist <= limit)
            .OrderBy(x => x.Dist).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(take).Select(x => x.Name);
    }

    /// <summary>Damerau-Levenshtein distance, giving up (returning limit + 1) when it must exceed the limit.</summary>
    static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
