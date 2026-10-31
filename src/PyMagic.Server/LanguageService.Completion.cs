using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Formatting;

namespace PyMagic.Server;

/// <summary>Completion: names, members, import statements, auto-import, templates, ranked by where the cursor is and what is expected there.</summary>
public sealed partial class LanguageService
{
    const string CursorMark = "__pymagic_cursor__";

    public IReadOnlyList<CompletionItem> Completion(string uri, Position pos)
    {
        if (_templateDocs.ContainsKey(uri)) return TemplateCompletion(uri, pos);
        if (!_docs.TryGetValue(uri, out var doc)) return Array.Empty<CompletionItem>();
        string text = doc.Text;
        int offset = new LineMap(text).OffsetOf(pos);

        // the identifier fragment being typed, and whether a dot precedes it
        int start = offset;
        while (start > 0 && IsIdentChar(text[start - 1])) start--;
        string prefix = text[start..offset];
        bool afterDot = start > 0 && text[start - 1] == '.' && !(start > 1 && text[start - 2] == '.');
        int lineStart = LineStart(text, start);
        string lineBefore = text[lineStart..start];

        switch (SiteAt(text, offset))
        {
            case CursorSite.Nothing:
                return FixtureCompletion(doc, start, offset, prefix) ?? DjangoStringCompletion(doc, start, offset) ?? WebStringCompletion(doc, start, offset) ?? MetaStringCompletion(doc, start, offset) ?? TypedDictKeyCompletion(doc, start, offset) ?? FlaskFormFieldCompletion(doc, start, offset) ?? Array.Empty<CompletionItem>();   // a few strings name things: a fixture, a URL, a template, a field
            case CursorSite.FStringField:
                break;
        }
        if (ImportCompletion(doc, text, offset, prefix) is { } importItems) return importItems;   // `import a.b.c|` has a dot too
        if (!afterDot && FixtureCompletion(doc, start, offset, prefix) is { } fixtureItems) return fixtureItems;

        // places where a new name is being made up, not looked up
        if (!afterDot && Regex.IsMatch(lineBefore, @"^\s*(async\s+)?(def|class)\s+$|\bas\s+$|^\s*(async\s+)?for\s+[^:]*$") && !Regex.IsMatch(lineBefore, @"\sin\s"))
            return Array.Empty<CompletionItem>();
        if (afterDot && start >= 2 && char.IsDigit(text[start - 2]) && IsNumberBefore(text, start - 1)) return Array.Empty<CompletionItem>();   // 3.|

        if (afterDot) return MemberCompletion(doc, start, offset, prefix);
        var names = NameCompletion(doc, start, offset, prefix, lineBefore);
        var keywords = KeywordArgumentItems(doc, start, offset, prefix);
        return keywords.Count == 0 ? names : keywords.Concat(names).ToList();
    }

    // ------------------------------------------------------------ where the cursor is

    enum CursorSite { Code, Nothing, FStringField }

    /// <summary>Comments and strings take no completions, except inside the fields of an f-string.</summary>
    static CursorSite SiteAt(string text, int offset)
    {
        foreach (var t in Lexer.Tokenize(text))
        {
            if (t.Start >= offset) break;
            if (offset > t.End) continue;
            switch (t.Kind)
            {
                case TokenKind.Comment: return CursorSite.Nothing;
                case TokenKind.String when offset < t.End || offset == t.End && !EndsWithQuote(text, t):
                    return InFStringField(text, t, offset) ? CursorSite.FStringField : CursorSite.Nothing;
                case TokenKind.Error when text[t.Start] is '"' or '\'' || t.Length > 1 && text[t.Start + 1] is '"' or '\'':
                    return InFStringField(text, t, offset) ? CursorSite.FStringField : CursorSite.Nothing;
            }
        }
        return CursorSite.Code;
    }

    static bool EndsWithQuote(string text, Token t) => t.Length > 0 && text[t.End - 1] is '"' or '\'';

    /// <summary>True when the cursor is inside {braces} of an f-string token.</summary>
    static bool InFStringField(string text, Token t, int offset)
    {
        int q = t.Start;
        bool formatted = false;
        for (; q < t.End && text[q] != '"' && text[q] != '\''; q++)
            if (char.ToLowerInvariant(text[q]) is 'f' or 't') formatted = true;
        if (!formatted || q >= t.End) return false;
        int depth = 0;
        for (int i = q; i < offset; i++)
        {
            if (text[i] == '{')
            {
                if (depth == 0 && i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
                depth++;
            }
            else if (text[i] == '}' && depth > 0) depth--;
        }
        return depth > 0;
    }

    /// <summary>The dot at <paramref name="dot"/> follows a number literal (1.5, 3.), not a name.</summary>
    static bool IsNumberBefore(string text, int dot)
    {
        int i = dot - 1;
        while (i >= 0 && (char.IsDigit(text[i]) || text[i] == '_')) i--;
        return i < 0 || !(char.IsLetter(text[i]) || text[i] == '_' || text[i] == ')' || text[i] == ']');
    }

    // ------------------------------------------------------------ expected types

    /// <summary>The analysis of a copy of the document where what is being typed is a complete name, for completion that depends on the surroundings.</summary>
    (Workspace Workspace, ModuleInfo Module) PatchedModule(Doc doc, int start, int end)
    {
        string patched = doc.Text[..start] + CursorMark + doc.Text[end..];
        // a call that is still being typed has no closing bracket yet: add what is missing right after the name, so it parses
        string closers = MissingClosers(patched);
        if (closers.Length > 0) patched = patched[..(start + CursorMark.Length)] + closers + patched[(start + CursorMark.Length)..];
        var (ws, fixtures) = NewWorkspace();
        foreach (var d in _docs.Values)
            if (d.Uri != doc.Uri) fixtures.Register(ws.AddModule(d.ModuleName, d.Text, d.IsPackage), d.Uri, d.Path);
        var m = ws.AddModule(doc.ModuleName, patched, doc.IsPackage, cached: false);
        fixtures.Register(m, doc.Uri, doc.Path);
        return (ws, m);
    }

    /// <summary>The closing brackets, innermost first, for the brackets the text opens and never closes.</summary>
    static string MissingClosers(string text)
    {
        var open = new Stack<char>();
        foreach (var t in Lexer.Tokenize(text))
        {
            if (t.Kind != TokenKind.Operator || t.Length != 1) continue;
            char c = text[t.Start];
            if (c is '(' or '[' or '{') open.Push(c);
            else if (c is ')' or ']' or '}' && open.Count > 0 && open.Peek() == (c == ')' ? '(' : c == ']' ? '[' : '{')) open.Pop();
        }
        return string.Concat(open.Select(c => c == '(' ? ')' : c == '[' ? ']' : '}'));
    }

    /// <summary>The type wanted where an expression stands: the parameter it is passed to, the annotation it is assigned to, the declared return type.</summary>
    static PyType? ExpectedTypeOf(ModuleInfo m, TypeEvaluator ev, Expr placeholder)
    {
        switch (m.ParentOf(placeholder))
        {
            case Argument a when ReferenceEquals(a.Value, placeholder) && m.ParentOf(a) is CallExpr call:
                foreach (var target in ev.ResolveCall(m, call).Targets)
                    foreach (var map in target.Mappings)
                        if (ReferenceEquals(map.Arg, a) && map.Param is { Type: not UnknownType } p) return p.Type;
                return null;
            case AnnAssignStmt aa when ReferenceEquals(aa.Value, placeholder):
                return ev.AnnotatedType(m, aa.Annotation);
            case ReturnStmt r when ReferenceEquals(r.Value, placeholder):
                for (var p = m.ParentOf(r); p != null; p = m.ParentOf(p))
                {
                    if (p is FunctionDef { Returns: { } returns }) return ev.AnnotatedType(m, returns);
                    if (p is FunctionDef or LambdaExpr or ClassDef) break;
                }
                return null;
        }
        return null;
    }

    /// <summary>A cheap look at the text before the cursor: could something there say what type is wanted?</summary>
    static bool MayHaveExpectation(string text, int start)
    {
        int i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0) return false;
        if (text[i] is '(' or ',' or '=') return true;
        return i >= 5 && text.AsSpan(i - 5, 6).SequenceEqual("return");
    }

    // ------------------------------------------------------------ members

    IReadOnlyList<CompletionItem> MemberCompletion(Doc doc, int start, int end, string prefix)
    {
        // `recv.pre|` is usually a syntax error while typing, so analyse a copy with a complete attribute access
        var (ws, m) = PatchedModule(doc, start, end);
        AttributeExpr? access = null;
        foreach (var n in AstChildren.Descendants(m.Ast))
            if (n is AttributeExpr a && a.Attr == CursorMark) { access = a; break; }
        if (access == null) return Array.Empty<CompletionItem>();

        var ev = ws.Evaluator;
        var recv = ev.TypeOf(m, access.Value);
        var expected = ExpectedTypeOf(m, ev, access);
        string text = m.Source;
        bool nextIsCall = end < doc.Text.Length && doc.Text[end] == '(';
        bool decorator = Regex.IsMatch(text[LineStart(text, start)..start], @"^\s*@");

        // how far up the class chain a member comes from decides the order: own members first, object's last
        var chain = new List<string>();
        foreach (var t in recv.Members)
            if (t is ClassType ct) { foreach (var anc in ev.MroOf(ct.Info)) if (anc is ClassType ac) chain.Add(ac.Info.Name); break; }

        var items = new List<CompletionItem>();
        foreach (var member in ev.MemberNames(recv))
        {
            if (!member.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (member.Name.StartsWith("__") && member.Name.EndsWith("__") && !prefix.StartsWith('_')) continue;
            if (member.Name.StartsWith('_') && !prefix.StartsWith('_')) continue;
            var attr = ev.AttributeType(recv, member.Name);
            int rank = member.Owner == "object" ? 9 : Math.Max(0, chain.IndexOf(member.Owner));
            bool fits = expected != null && ev.Fits(expected, attr.Type is FunctionType or OverloadedType ? ev.ReturnOf(attr.Type) : attr.Type);
            string? insert = null;
            bool snippet = false;
            if (!nextIsCall && !decorator && CallSnippet(attr.Type, member.Name, boundToInstance: !recv.Members.Any(x => x is ClassType { IsInstance: false })) is { } call)
            { insert = call; snippet = true; }
            items.Add(new CompletionItem(member.Name, KindOf(attr.Type, member.IsInstanceAttribute),
                FormatType(ev, attr.Type) is var t && t != "Unknown" ? t : null,
                SortText: (fits ? "0" : "1") + Math.Min(rank, 9) + member.Name.ToLowerInvariant(), InsertText: insert, IsSnippet: snippet));
        }

        // postfix templates: `items.for`, `x.if`, `value.print`
        if (prefix.Length > 0)
        {
            int dot = start - 1;
            items.AddRange(PostfixItems(new LineMap(text), ev, text, ReceiverStart(text, access, dot), dot, recv, prefix));
        }
        return items;
    }

    /// <summary>`name($0)` for a function or method that takes arguments, `name()` for one that takes none; null for anything that is not called.</summary>
    static string? CallSnippet(PyType type, string name, bool boundToInstance)
    {
        FunctionType? f = type as FunctionType ?? (type as OverloadedType)?.Overloads.FirstOrDefault();
        if (f?.Info.Node is not FunctionDef def || (f.Info.Flags & (FuncFlags.Property | FuncFlags.PropertySetter)) != 0) return null;
        bool skipFirst = f.Info.Owner != null && boundToInstance && (f.Info.Flags & FuncFlags.Static) == 0;
        var required = def.Params.Where(p => p.Kind == ParamKind.Normal && p.Default == null).Skip(skipFirst ? 1 : 0).Count();
        bool takesAny = def.Params.Any(p => p.Kind is ParamKind.VarArgs or ParamKind.KwArgs);
        return required > 0 || (takesAny && !def.Params.Any(p => p.Kind == ParamKind.Normal)) ? $"{name}($0)" : $"{name}()";
    }

    // ------------------------------------------------------------ names

    IReadOnlyList<CompletionItem> NameCompletion(Doc doc, int start, int end, string prefix, string lineBefore)
    {
        bool statementStart = lineBefore.All(char.IsWhiteSpace);
        bool decorator = Regex.IsMatch(lineBefore, @"^\s*@\s*[\w.]*$");
        bool nextIsCall = end < doc.Text.Length && doc.Text[end] == '(';

        // what is expected here, if the text before the cursor suggests anything: the analysis then runs on a copy with a complete name
        ModuleInfo m;
        TypeEvaluator ev;
        PyType? expected = null;
        NameExpr? placeholder = null;
        string text = doc.Text;
        if (!decorator && MayHaveExpectation(text, start))
        {
            var (ws, patched) = PatchedModule(doc, start, end);
            m = patched; ev = ws.Evaluator; text = patched.Source;
            foreach (var n in AstChildren.Descendants(m.Ast))
                if (n is NameExpr ne && ne.Name == CursorMark) { expected = ExpectedTypeOf(m, ev, ne); placeholder = ne; break; }
        }
        else
        {
            var snap = Current();
            m = snap.ByUri[doc.Uri]; ev = snap.Workspace.Evaluator;
        }

        var lines = new LineMap(text);   // not Lines(m): copies analysed for one completion must not pile up in the cache
        var items = new Dictionary<string, CompletionItem>();
        void Add(string name, int kind, string? detail, string tier, PyType? type = null, string? insert = null, bool snippet = false)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (name.StartsWith('_') && !prefix.StartsWith('_')) return;
            bool fits = expected != null && type != null && ev.Fits(expected, type);
            items.TryAdd(name, new CompletionItem(name, kind, detail, (fits ? "0" : tier) + name.ToLowerInvariant(), insert, snippet));
        }

        // enclosing scopes, innermost first; class scopes are only visible from their own body
        var scope = ScopeAtCursor(m, text, start);
        bool innermost = true;
        int depth = 0;
        bool inClassBody = scope.Kind == ScopeKind.Class;
        for (var s = scope; s != null; s = s.Parent)
        {
            if (s.Kind == ScopeKind.Class && !innermost) continue;
            string tier = depth == 0 ? "1" : depth == 1 && s.Kind != ScopeKind.Module ? "2" : "3";
            foreach (var sym in s.Symbols.Values)
            {
                if (sym.Declarations.Count == 0) continue;
                var type = ev.TypeOfSymbol(sym);
                int kind = sym.Declarations[0].Kind switch
                {
                    DeclKind.Function => s.Kind == ScopeKind.Class ? CompletionKind.Method : CompletionKind.Function,
                    DeclKind.Class => CompletionKind.Class,
                    DeclKind.Import => type is ModuleType ? CompletionKind.Module : KindOf(type, false),
                    DeclKind.TypeParam => CompletionKind.TypeParameter,
                    _ => CompletionKind.Variable,
                };
                string? insert = null;
                bool snippet = false;
                if (!decorator && !nextIsCall && type is FunctionType or OverloadedType && CallSnippet(type, sym.Name, boundToInstance: false) is { } call)
                { insert = call; snippet = true; }
                PyType? fitType = expected == null ? null : type is FunctionType or OverloadedType ? ev.ReturnOf(type) : type;
                Add(sym.Name, kind, type is UnknownType ? null : FormatType(ev, type), tier, fitType, insert, snippet);
            }
            innermost = false;
            depth++;
        }

        // the keywords of a query: filter(ti| offers title=, author__name=
        if (placeholder != null)
            foreach (var item in DjangoKeywordItems(m, ev, placeholder, prefix)) items.TryAdd("" + item.Label, item);

        // keywords: those that start statements only where one can start
        if (!decorator)
        {
            var keywords = statementStart ? StatementKeywords.Concat(new[] { "case" }) : ExpressionKeywords.AsEnumerable();
            foreach (var kw in keywords.Distinct())
            {
                PyType? kwType = kw is "True" or "False" ? ev.BuiltinInstance("bool") : null;
                Add(kw, CompletionKind.Keyword, null, "6", kwType);
            }
            if (statementStart)
                foreach (var item in LiveTemplateItems(m, lines, prefix, inClassBody)) items.TryAdd("\u0001" + item.Label, item);
        }

        // builtins
        var builtinScope = ev.Workspace.Builtins.Semantics.ModuleScope;
        foreach (var name in Builtins.Names)
        {
            int kind = char.IsUpper(name[0]) ? CompletionKind.Class : CompletionKind.Function;
            string? insert = null;
            bool snippet = false;
            if (builtinScope.Symbols.TryGetValue(name, out var bs) && bs.Declarations.Count > 0)
            {
                kind = bs.Declarations[0].Kind switch { DeclKind.Class => CompletionKind.Class, DeclKind.Function => CompletionKind.Function, _ => CompletionKind.Constant };
                if (!decorator && !nextIsCall && bs.Declarations[0].Kind == DeclKind.Function && name.StartsWith(prefix, StringComparison.Ordinal)
                    && CallSnippet(ev.TypeOfSymbol(bs), name, boundToInstance: false) is { } call)
                { insert = call; snippet = true; }
            }
            Add(name, kind, null, "5", null, insert, snippet);
        }

        // names from other modules, with the import that makes them usable
        if (!decorator && prefix.Length >= 2)
            foreach (var item in AutoImportItems(doc, m, lines, prefix, items))
                items.TryAdd("\u0002" + item.Label + item.Detail, item);

        return items.Values.ToList();
    }

    // ------------------------------------------------------------ auto-import

    sealed record ImportCandidate(string Name, string Module, int Kind, bool FromProject);

    /// <summary>Stdlib modules whose public names are offered for import (the rest of the library is reached through import statements).</summary>
    static readonly string[] AutoImportModules =
    {
        "typing", "collections", "collections.abc", "dataclasses", "enum", "pathlib", "functools", "itertools", "datetime", "abc", "re", "json",
        "os", "sys", "math", "random", "time", "logging", "subprocess", "argparse", "contextlib", "shutil", "tempfile", "threading", "asyncio",
        "uuid", "hashlib", "base64", "copy", "csv", "decimal", "fractions", "heapq", "bisect", "operator", "statistics", "string", "textwrap",
        "struct", "socket", "urllib.request", "urllib.parse", "http.client", "unittest", "traceback", "warnings", "inspect", "io", "glob",
        "pickle", "queue", "weakref", "types", "secrets", "zlib", "gzip", "zipfile", "sqlite3", "concurrent.futures", "multiprocessing",
        "platform", "signal", "typing_extensions", "itertools", "unittest.mock", "xml.etree.ElementTree", "email.message", "html", "configparser",
    };

    (string Key, List<ImportCandidate> Items)? _libraryCandidates;
    (string Stamp, List<ImportCandidate> Items)? _projectCandidates;

    /// <summary>Public functions and classes of a module (and the typing special forms), without evaluating anything.</summary>
    static IEnumerable<ImportCandidate> ModuleCandidates(ModuleInfo module, bool fromProject)
    {
        bool typing = module.Name is "typing" or "typing_extensions";
        foreach (var sym in module.Semantics.ModuleScope.Symbols.Values)
        {
            if (sym.Redirect != null || sym.Name.StartsWith('_') || sym.Declarations.Count == 0) continue;
            var decl = sym.Declarations[^1];
            int kind = decl.Kind switch
            {
                DeclKind.Function => CompletionKind.Function,
                DeclKind.Class => CompletionKind.Class,
                DeclKind.Variable when typing && char.IsUpper(sym.Name[0]) => CompletionKind.Class,
                DeclKind.Variable when sym.Name.Length > 1 && sym.Name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_') => CompletionKind.Constant,
                _ => 0,
            };
            if (kind != 0) yield return new ImportCandidate(sym.Name, module.Name, kind, fromProject);
        }
    }

    List<ImportCandidate> LibraryCandidates(Snapshot snap)
    {
        string key = (TypeshedPath ?? "") + "|" + (Interpreter?.Path ?? "");
        if (_libraryCandidates is { } cached && cached.Key == key) return cached.Items;
        var list = new List<ImportCandidate>();
        foreach (var name in AutoImportModules.Distinct())
        {
            var module = snap.Workspace.GetModule(name);
            if (module != null) list.AddRange(ModuleCandidates(module, false));
        }
        foreach (var name in StdlibNames.All)
            if (!name.StartsWith('_')) list.Add(new ImportCandidate(name, "", CompletionKind.Module, false));
        _libraryCandidates = (key, list);
        return list;
    }

    List<ImportCandidate> ProjectCandidates(Doc doc)
    {
        // rebuilt when another open file changed, and every so often for files that change on disk
        string stamp = string.Join("|", _docs.Values.Where(d => d.Uri != doc.Uri).Select(d => d.Uri + "@" + d.Version)) + "|" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond / 20;
        if (_projectCandidates is { } cached && cached.Stamp == stamp) return cached.Items;
        var snap = Full();
        var list = new List<ImportCandidate>();
        snap.ByUri.TryGetValue(doc.Uri, out var current);
        foreach (var module in snap.Project)
        {
            if (ReferenceEquals(module, current) || module.IsStub || module.Name.StartsWith("__untitled__") || module.Name.Split('.').Any(p => p.StartsWith('_') && p != "__init__")) continue;
            list.AddRange(ModuleCandidates(module, true));
            // the module itself: `import tools` or `from pkg import tools`
            int dot = module.Name.LastIndexOf('.');
            list.Add(new ImportCandidate(dot < 0 ? module.Name : module.Name[(dot + 1)..], dot < 0 ? "" : module.Name[..dot], CompletionKind.Module, true));
        }
        _projectCandidates = (stamp, list);
        return list;
    }

    IEnumerable<CompletionItem> AutoImportItems(Doc doc, ModuleInfo m, LineMap lines, string prefix, Dictionary<string, CompletionItem> inScope)
    {
        var snap = Current();
        var seen = new HashSet<(string, string)>();
        int count = 0;
        foreach (var c in ProjectCandidates(doc).Concat(LibraryCandidates(snap)))
        {
            if (count >= 60) break;
            if (!c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || c.Name == prefix && inScope.ContainsKey(c.Name)) continue;
            if (inScope.ContainsKey(c.Name) || !seen.Add((c.Name, c.Module))) continue;
            bool isModule = c.Kind == CompletionKind.Module;
            string detail = isModule ? (c.Module == "" ? $"import {c.Name}" : $"from {c.Module} import {c.Name}") : $"from {c.Module} import {c.Name}";
            var edit = ImportEdit(m, c.Module == "" ? null : c.Module, c.Name);
            yield return new CompletionItem(c.Name, c.Kind, detail, (c.FromProject ? "8" : "9") + c.Name.ToLowerInvariant(),
                AdditionalTextEdits: new List<TextEdit> { ToTextEdit(lines, edit) });
            count++;
        }
    }

    // ------------------------------------------------------------ import statements

    static readonly Regex ImportStatement = new(@"^import\s+(?<list>.*)$", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex FromModule = new(@"^from\s+(?<mod>[\w.]*)$", RegexOptions.Compiled);
    static readonly Regex FromImport = new(@"^from\s+(?<mod>\.*[\w.]*)\s+import(?:\s+|\s*\()(?<names>[^#]*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The statement before the cursor when it is an import: the line, or the lines of a parenthesised `from x import (` list.</summary>
    static string? ImportStatementBefore(string text, int offset)
    {
        int ls = LineStart(text, offset);
        string line = text[ls..offset];
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("import ") || trimmed.StartsWith("from ") || trimmed is "import" or "from") return trimmed;
        // inside `from x import (\n    a,\n    |`
        int cur = ls;
        for (int i = 0; i < 40 && cur > 0; i++)
        {
            cur = LineStart(text, cur - 1);
            string earlier = text[cur..LineEndInclusive(text, cur)].TrimEnd('\r', '\n');
            string t = earlier.TrimStart();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (!t.StartsWith("from ")) { if (t.EndsWith(')') || !(t.EndsWith(',') || t.EndsWith('(') || Regex.IsMatch(t, @"^\w+(\s+as\s+\w+)?,?$"))) return null; continue; }
            if (t.Count(c => c == '(') <= t.Count(c => c == ')')) return null;   // the list has to be opened on this very line
            string joined = Regex.Replace(text[cur..offset], @"\s*[\r\n]+\s*", " ").TrimStart();
            return joined.Count(c => c == '(') > joined.Count(c => c == ')') && joined.Contains(" import") ? joined : null;
        }
        return null;
    }

    IReadOnlyList<CompletionItem>? ImportCompletion(Doc doc, string text, int offset, string prefix)
    {
        string? stmt = ImportStatementBefore(text, offset);
        if (stmt == null) return null;
        var snap = Current();
        if (!snap.ByUri.TryGetValue(doc.Uri, out var m)) return null;
        var ws = snap.Workspace;
        string typed = prefix;

        // import a.b.c
        var plain = ImportStatement.Match(stmt);
        if (plain.Success)
        {
            string segment = plain.Groups["list"].Value;
            segment = segment[(segment.LastIndexOf(',') + 1)..].TrimStart();
            if (Regex.IsMatch(segment, @"\s")) return Array.Empty<CompletionItem>();   // `import os as `: an alias is a new name
            string? parent = segment.Contains('.') ? segment[..segment.LastIndexOf('.')] : null;
            return ModuleChoices(snap, parent, typed);
        }

        // from a.b|  and  from .|
        var fromModule = FromModule.Match(stmt);
        if (fromModule.Success && stmt.Length > 5)
        {
            string mod = fromModule.Groups["mod"].Value;
            int level = mod.TakeWhile(c => c == '.').Count();
            string rest = mod[level..];
            string? parentRest = rest.Contains('.') ? rest[..rest.LastIndexOf('.')] : null;
            string? parent = level == 0 ? parentRest : Workspace.AbsoluteName(m, level, parentRest);
            if (level > 0 && parent == null) return Array.Empty<CompletionItem>();
            return ModuleChoices(snap, parent, typed, relativeBase: level > 0 && parentRest == null);
        }

        // from a.b import |
        var fromImport = FromImport.Match(stmt);
        if (fromImport.Success)
        {
            string mod = fromImport.Groups["mod"].Value;
            int level = mod.TakeWhile(c => c == '.').Count();
            string? rest = mod[level..] is { Length: > 0 } r ? r : null;
            string? absolute = Workspace.AbsoluteName(m, level, rest);
            var module = absolute == null ? null : ws.GetModule(absolute);
            if (module == null) return Array.Empty<CompletionItem>();
            string names = fromImport.Groups["names"].Value;
            string segment = names[(names.LastIndexOf(',') + 1)..].TrimStart().TrimStart('(').TrimStart();
            if (Regex.IsMatch(segment, @"\s")) return Array.Empty<CompletionItem>();
            var already = new HashSet<string>(names.Split(',').Select(x => Regex.Match(x, @"\w+").Value).Where(x => x.Length > 0));
            var items = new List<CompletionItem>();
            foreach (var (name, kind, detail) in ModuleExports(snap, module))
            {
                if (!name.StartsWith(typed, StringComparison.Ordinal) || already.Contains(name)) continue;
                items.Add(new CompletionItem(name, kind, detail, (kind == CompletionKind.Module ? "2" : "1") + name.ToLowerInvariant()));
            }
            return items;
        }
        return stmt.StartsWith("import") || stmt.StartsWith("from") ? Array.Empty<CompletionItem>() : null;
    }

    IReadOnlyList<CompletionItem> ModuleChoices(Snapshot snap, string? parent, string typed, bool relativeBase = false)
    {
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (parent == null && !relativeBase)
        {
            foreach (var n in StdlibNames.All) if (!n.StartsWith('_')) names.TryAdd(n, "standard library");
            foreach (var root in EffectiveLibraryRoots())
                foreach (var n in LibraryTopLevel(root)) names.TryAdd(n, "installed package");
        }
        string dotted = parent == null ? "" : parent + ".";
        foreach (var module in snap.Project)
        {
            if (module.Name.StartsWith("__untitled__") || !module.Name.StartsWith(dotted, StringComparison.Ordinal)) continue;
            string next = module.Name[dotted.Length..].Split('.')[0];
            if (next.Length > 0 && !next.StartsWith('_')) names[next] = "project";
        }
        if (parent != null && snap.Workspace.GetModule(parent) is { } pkg)
            foreach (var n in ChildModuleNames(pkg)) names.TryAdd(n, parent + "." + n);
        return names.Where(kv => kv.Key.StartsWith(typed, StringComparison.Ordinal))
            .Select(kv => new CompletionItem(kv.Key, CompletionKind.Module, kv.Value, "1" + kv.Key.ToLowerInvariant())).ToList();
    }

    /// <summary>What `from module import ...` can name: the public names and the submodules.</summary>
    IEnumerable<(string Name, int Kind, string? Detail)> ModuleExports(Snapshot snap, ModuleInfo module)
    {
        var seen = new HashSet<string>();
        foreach (var sym in module.Semantics.ModuleScope.Symbols.Values)
        {
            if (sym.Redirect != null || sym.Name.StartsWith('_') || sym.Declarations.Count == 0) continue;
            var decl = sym.Declarations[^1];
            // a stub's imports are private unless written `import x as x`
            if (module.IsStub && decl.Kind == DeclKind.Import && !(decl.Node is ImportAlias { AsName: { } a } alias && a == alias.Name)) continue;
            int kind = decl.Kind switch
            {
                DeclKind.Function => CompletionKind.Function,
                DeclKind.Class => CompletionKind.Class,
                DeclKind.Import => CompletionKind.Variable,
                _ => sym.Name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_') ? CompletionKind.Constant : CompletionKind.Variable,
            };
            if (seen.Add(sym.Name)) yield return (sym.Name, kind, null);
        }
        foreach (var child in ChildModuleNames(module).Concat(ProjectSubmodules(snap, module.Name)))
            if (seen.Add(child)) yield return (child, CompletionKind.Module, "submodule");
    }

    /// <summary>The next name of every project module below a package, for packages that exist only as open buffers.</summary>
    static IEnumerable<string> ProjectSubmodules(Snapshot snap, string package) =>
        snap.Project.Where(p => p.Name.StartsWith(package + ".", StringComparison.Ordinal))
            .Select(p => p.Name[(package.Length + 1)..].Split('.')[0]).Where(n => n.Length > 0 && !n.StartsWith('_')).Distinct();

    static IEnumerable<string> ChildModuleNames(ModuleInfo package)
    {
        if (!package.IsPackage || package.Path == null) yield break;
        string? dir = System.IO.Path.GetDirectoryName(package.Path);
        if (dir == null || !Directory.Exists(dir)) yield break;
        foreach (var n in DirectoryModules(dir)) yield return n;
    }

    static readonly Dictionary<string, (DateTime Stamp, List<string> Names)> _directoryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modules and packages directly inside a folder.</summary>
    static List<string> DirectoryModules(string dir)
    {
        DateTime stamp;
        try { stamp = Directory.GetLastWriteTimeUtc(dir); } catch (IOException) { return new List<string>(); }
        if (_directoryCache.TryGetValue(dir, out var cached) && cached.Stamp == stamp) return cached.Names;
        var names = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string ext = System.IO.Path.GetExtension(f);
                string stem = System.IO.Path.GetFileNameWithoutExtension(f);
                if (ext is ".py" or ".pyi" && stem != "__init__" && IsIdentifier(stem) && !stem.StartsWith('_')) names.Add(stem);
                else if (ext is ".pyd" or ".so" && stem.Split('.')[0] is { } bare && IsIdentifier(bare) && !bare.StartsWith('_')) names.Add(bare);
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                string name = System.IO.Path.GetFileName(d);
                if (name.EndsWith("-stubs")) name = name[..^6];
                if (!IsIdentifier(name) || name.StartsWith('_')) continue;
                if (File.Exists(System.IO.Path.Combine(d, "__init__.py")) || File.Exists(System.IO.Path.Combine(d, "__init__.pyi")) || d.EndsWith("-stubs")) names.Add(name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a folder that cannot be read has no modules */ }
        names = names.Distinct().ToList();
        _directoryCache[dir] = (stamp, names);
        return names;
    }

    static List<string> LibraryTopLevel(string root) => Directory.Exists(root) ? DirectoryModules(root) : new List<string>();

    static bool IsIdentifier(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(IsIdentChar);
}
