using System.Text;
using System.Text.RegularExpressions;
using PyModule = PyMagic.Core.Syntax.Module;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>What a refactoring offers in the lightbulb menu.</summary>
/// <param name="Kind">extractVariable, extractConstant, extractMethod, inlineVariable or safeDelete.</param>
/// <param name="DefaultName">Suggested name; null when the refactoring needs no name.</param>
public sealed record RefactorOption(string Kind, string Title, string? DefaultName, bool All = false);

/// <summary>
/// Refactorings that rewrite code: extract variable / constant / method, inline variable, safe delete. Each works from the
/// syntax tree and the scope and flow tables (what is read, what is assigned, what reaches what), and every request either
/// returns a complete set of text edits or fails with the reason (a <see cref="ServiceException"/>), never a half-done change.
/// </summary>
public sealed partial class LanguageService
{
    public const string RefactorExtractKind = "refactor.extract";
    public const string RefactorInlineKind = "refactor.inline";
    public const string RefactorKind = "refactor";

    static string RefactorActionKind(string kind) => kind switch
    {
        "extractVariable" => "refactor.extract.variable",
        "extractConstant" => "refactor.extract.constant",
        "extractMethod" => "refactor.extract.function",
        "inlineVariable" => "refactor.inline.variable",
        "inlineMethod" or "inlineMethodAll" => "refactor.inline.method",
        "changeSignature" => "refactor.rewrite.signature",
        _ => "refactor.rewrite",
    };

    /// <summary>The text the user selected, understood as an expression or as whole statements of one block.</summary>
    sealed record Selection(int Start, int End, Expr? Expression, IReadOnlyList<Stmt>? Statements, IReadOnlyList<Stmt>? Block);

    // ------------------------------------------------------------ entry points

    /// <summary>The refactorings that apply to a selection (or, with an empty selection, to the name at the cursor).</summary>
    public IReadOnlyList<RefactorOption> RefactorOptions(string uri, Range range)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m) || m.SyntaxErrors.Count > 0) return Array.Empty<RefactorOption>();
        var lines = Lines(m);
        int start = lines.OffsetOf(range.Start), end = lines.OffsetOf(range.End);
        var options = new List<RefactorOption>();

        bool Feasible(string kind, string? name, bool all = false)
        {
            try { Plan(snap, m, kind, start, end, name, all); return true; }
            catch (ServiceException) { return false; }
        }

        if (end > start)
        {
            var sel = FindSelection(m, start, end);
            if (sel?.Expression != null)
            {
                string variable = SuggestVariableName(m, sel.Expression);
                if (Feasible("extractVariable", variable))
                {
                    options.Add(new RefactorOption("extractVariable", "Extract variable", variable));
                    int count = Occurrences(m, sel.Expression).Count;
                    if (count > 1) options.Add(new RefactorOption("extractVariable", $"Extract variable (replace all {count} occurrences)", variable, All: true));
                }
                string constant = SuggestConstantName(m, sel.Expression);
                if (Feasible("extractConstant", constant))
                    options.Add(new RefactorOption("extractConstant", "Extract constant", constant));
            }
            if (sel != null)
            {
                string method = SuggestMethodName(m, sel);
                if (Feasible("extractMethod", method)) options.Add(new RefactorOption("extractMethod", sel.Statements != null ? "Extract method" : "Extract method from expression", method));
            }
        }
        else
        {
            if (Feasible("inlineVariable", null)) options.Add(new RefactorOption("inlineVariable", "Inline variable", null));
            if (Feasible("safeDelete", null)) options.Add(new RefactorOption("safeDelete", "Safe delete (no usages)", null));
            if (CanChangeSignature(snap, m, start)) options.Add(new RefactorOption("changeSignature", "Change signature…", null));
            if (Feasible("inlineMethod", null)) options.Add(new RefactorOption("inlineMethod", "Inline method (this call)", null));
            if (CanInlineAll(snap, m, start)) options.Add(new RefactorOption("inlineMethodAll", "Inline method and remove it", null));
        }
        return options;
    }

    /// <exception cref="ServiceException">The refactoring cannot be done here; the message says why.</exception>
    public WorkspaceEdit Refactor(string uri, string kind, Range range, string? name, bool all = false)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) throw new ServiceException("unknown document");
        var lines = Lines(m);
        var edits = Plan(snap, m, kind, lines.OffsetOf(range.Start), lines.OffsetOf(range.End), name, all);

        // edits may land in several files (safe delete of a name does not, inline of a project-wide name does not either, but keep it general)
        var changes = new Dictionary<string, List<TextEdit>>();
        foreach (var (module, list) in edits)
        {
            string target = UriOfModule(module) ?? throw new ServiceException("a file that has to change cannot be edited");
            var ml = Lines(module);
            changes[target] = list.OrderBy(e => e.Start).ThenBy(e => e.End).Select(e => new TextEdit(ml.RangeOf(e.Start, e.End), e.Text)).ToList();
        }
        return new WorkspaceEdit(changes);
    }

    Dictionary<ModuleInfo, List<Edit>> Plan(Snapshot snap, ModuleInfo m, string kind, int start, int end, string? name, bool all)
    {
        if (m.SyntaxErrors.Count > 0) throw new ServiceException("Fix the syntax errors in this file first.");
        var one = (List<Edit> list) => new Dictionary<ModuleInfo, List<Edit>> { [m] = list };
        switch (kind)
        {
            case "extractVariable": return one(ExtractVariable(m, RequireSelection(m, start, end), name, all));
            case "extractConstant": return one(ExtractConstant(m, RequireSelection(m, start, end), name, all));
            case "extractMethod": return one(ExtractMethod(m, RequireSelection(m, start, end), name));
            case "inlineVariable": return InlineVariable(snap, m, start);
            case "inlineMethod": return InlineMethod(snap, m, start, all: false);
            case "inlineMethodAll": return InlineMethod(snap, m, start, all: true);
            case "safeDelete": return SafeDelete(snap, m, start);
            default: throw new ServiceException($"unknown refactoring '{kind}'");
        }
    }

    // ------------------------------------------------------------ selection

    Selection RequireSelection(ModuleInfo m, int start, int end) =>
        FindSelection(m, start, end) ?? throw new ServiceException("Select a complete expression, or whole statements.");

    static Selection? FindSelection(ModuleInfo m, int s, int e)
    {
        string t = m.Source;
        e = Math.Min(e, t.Length);
        while (s < e && char.IsWhiteSpace(t[s])) s++;
        while (e > s && char.IsWhiteSpace(t[e - 1])) e--;
        if (s >= e) return null;
        int s0 = s, e0 = e;

        // an expression: a node with exactly this range (also when the selection includes redundant parentheses)
        Expr? expr = null;
        for (int attempt = 0; attempt < 3 && expr == null; attempt++)
        {
            foreach (var n in AstChildren.Descendants(m.Ast))
                if (n is Expr x && x is not ErrorExpr && x.Start == s && x.End == e) { expr = x; break; }
            if (expr == null && t[s] == '(' && t[e - 1] == ')')
            {
                s++; e--;
                while (s < e && char.IsWhiteSpace(t[s])) s++;
                while (e > s && char.IsWhiteSpace(t[e - 1])) e--;
            }
            else break;
        }

        // whole statements of one block
        IReadOnlyList<Stmt>? statements = null, block = null;
        var first = AstChildren.Descendants(m.Ast).OfType<Stmt>().FirstOrDefault(x => x.Start == s0);
        if (first != null && m.ParentOf(first) is { } parent && BodyContaining(parent, first) is { } body)
        {
            int i = body.ToList().FindIndex(x => ReferenceEquals(x, first));
            for (int j = i; j >= 0 && j < body.Count; j++)
                if (TrueEnd(t, body[j]) == e0) { statements = body.Skip(i).Take(j - i + 1).ToList(); block = body; break; }
        }

        return expr == null && statements == null ? null : new Selection(s0, e0, expr, statements, block);
    }

    /// <summary>Where a statement's own text ends (compound statements are recorded up to the end of their last line break).</summary>
    static int TrueEnd(string text, Node n)
    {
        int e = Math.Min(n.End, text.Length);
        while (e > n.Start && (text[e - 1] == '\n' || text[e - 1] == '\r')) e--;
        return e;
    }

    /// <summary>The nearest statement around a node (not the node itself).</summary>
    static Stmt? EnclosingStatement(ModuleInfo m, Node n)
    {
        for (var p = m.ParentOf(n); p != null; p = m.ParentOf(p)) if (p is Stmt s) return s;
        return null;
    }

    /// <summary>
    /// The def, class, module, lambda or comprehension whose scope the node belongs to. Decorators, default values, annotations
    /// and base classes of a def or class are evaluated in the scope around it.
    /// </summary>
    static Node EnclosingScopeNode(ModuleInfo m, Node n)
    {
        Node child = n;
        for (var p = m.ParentOf(n); p != null; child = p, p = m.ParentOf(p))
        {
            switch (p)
            {
                case FunctionDef f when f.Body.Count > 0 && child.Start >= f.Body[0].Start: return f;
                case ClassDef c when c.Body.Count > 0 && child.Start >= c.Body[0].Start: return c;
                case LambdaExpr l when ReferenceEquals(child, l.Body): return l;
                case CompExpr: return p;
                case PyModule: return p;
            }
        }
        return m.Ast;
    }

    /// <summary>The statement text starts its line (only indentation before it), so a whole line can be inserted above.</summary>
    static bool StartsLine(string text, int offset) => text[LineStart(text, offset)..offset].All(c => c is ' ' or '\t');

    static string NewLine(ModuleInfo m) => m.Source.Contains("\r\n") ? "\r\n" : "\n";

    // ------------------------------------------------------------ names

    static readonly HashSet<string> PrefixesToDrop = new() { "get", "make", "create", "compute", "calculate", "build", "load", "read", "find", "fetch", "new" };

    static string Snake(string name)
    {
        var s = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "_").ToLowerInvariant();
        s = Regex.Replace(s, @"[^a-z0-9_]", "_").Trim('_');
        return s.Length == 0 || char.IsDigit(s[0]) ? "value" : s;
    }

    /// <summary>Names already visible from the scope: they must not be reused.</summary>
    static HashSet<string> NamesVisibleAt(ModuleInfo m, Node n)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var scopeNode = EnclosingScopeNode(m, n);
        for (var s = m.Semantics.ScopeOf(scopeNode); s != null; s = s.Parent)
            foreach (var sym in s.Symbols.Values) if (sym.Declarations.Count > 0 || sym.Redirect != null) names.Add(sym.Name);
        foreach (var b in Builtins.Names) names.Add(b);
        foreach (var k in Lexer.Keywords) names.Add(k);
        return names;
    }

    static string Unique(string name, HashSet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++) if (!taken.Contains(name + i)) return name + i;
    }

    static string SuggestVariableName(ModuleInfo m, Expr e)
    {
        static string? LastName(Expr f) => f switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
        string suggestion = e switch
        {
            CallExpr c when LastName(c.Func) is { } fn =>
                char.IsUpper(fn[0]) ? Snake(fn) : DropPrefix(Snake(fn)),
            AttributeExpr a => Snake(a.Attr),
            ConstExpr { Kind: ConstKind.Str } => "text",
            ConstExpr { Kind: ConstKind.Int or ConstKind.Float or ConstKind.Imaginary } => "number",
            ConstExpr { Kind: ConstKind.True or ConstKind.False } => "flag",
            CompareExpr => "condition",
            BinaryExpr { Op: "and" or "or" } => "condition",
            UnaryExpr { Op: "not" } => "condition",
            ListExpr or CompExpr { Kind: CompKind.List or CompKind.Generator } => "items",
            DictExpr or CompExpr { Kind: CompKind.Dict } => "mapping",
            SetExpr or CompExpr { Kind: CompKind.Set } => "unique_items",
            TupleExpr => "values",
            SubscriptExpr => "item",
            LambdaExpr => "func",
            _ => "value",
        };
        return Unique(suggestion, NamesVisibleAt(m, e));
    }

    static string DropPrefix(string name)
    {
        int us = name.IndexOf('_');
        return us > 0 && us < name.Length - 1 && PrefixesToDrop.Contains(name[..us]) ? name[(us + 1)..] : name;
    }

    static string SuggestConstantName(ModuleInfo m, Expr e)
    {
        string name = "CONSTANT";
        if (e is ConstExpr { Kind: ConstKind.Str } s)
        {
            string body = Regex.Replace(s.Text.Trim('\'', '"', 'f', 'r', 'b', 'u', 'F', 'R', 'B', 'U'), @"[^A-Za-z0-9]+", "_").Trim('_').ToUpperInvariant();
            if (body.Length is > 0 and <= 24 && !char.IsDigit(body[0])) name = body;
        }
        else if (e is ConstExpr { Kind: ConstKind.Int or ConstKind.Float }) name = "VALUE";
        return Unique(name, NamesVisibleAt(m, m.Ast));
    }

    static string SuggestMethodName(ModuleInfo m, Selection sel)
    {
        Node anchor = sel.Statements != null ? sel.Statements[0] : sel.Expression!;
        bool method = EnclosingScopeNode(m, anchor) is FunctionDef f && m.ParentOf(f) is ClassDef;
        string baseName = sel.Expression != null && sel.Statements == null ? Snake("compute_" + SuggestVariableName(m, sel.Expression)) : method ? "extracted_method" : "extracted_function";
        var taken = NamesVisibleAt(m, anchor);
        if (EnclosingScopeNode(m, anchor) is FunctionDef { } owner && m.ParentOf(owner) is ClassDef cls && m.Semantics.ScopeOf(cls) is { } cs)
            foreach (var k in cs.Symbols.Keys) taken.Add(k);
        return Unique(baseName, taken);
    }

    /// <exception cref="ServiceException">Not an identifier, a keyword, or a name that already means something here.</exception>
    static string CheckName(ModuleInfo m, Node where, string? name, bool constant = false)
    {
        if (name == null || !Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}_]*$") || Lexer.Keywords.Contains(name))
            throw new ServiceException($"'{name}' is not a valid name");
        var scopeNode = EnclosingScopeNode(m, where);
        var scope = m.Semantics.ScopeOf(constant ? m.Ast : scopeNode);
        for (var s = scope; s != null; s = s.Parent)
            if (s.Symbols.TryGetValue(name, out var sym) && sym.Declarations.Count > 0 && (s == scope || s.Kind == ScopeKind.Module))
                throw new ServiceException($"'{name}' is already defined here");
        return name;
    }

    // ------------------------------------------------------------ extract variable / constant

    /// <summary>Pure expressions have no side effects, so one evaluation can stand for several.</summary>
    static bool IsPure(Expr e) => !AstChildren.Descendants(e).Any(n => n is CallExpr or AwaitExpr or YieldExpr or NamedExpr or LambdaExpr or CompExpr);

    static string Normalised(ModuleInfo m, Node n) => Regex.Replace(m.Source[n.Start..n.End], @"\s+", "");

    /// <summary>Expressions equal to this one (same shape, same text) in the same scope after it, when replacing all of them is safe.</summary>
    List<Expr> Occurrences(ModuleInfo m, Expr e)
    {
        var found = new List<Expr> { e };
        if (!IsPure(e) || e is NameExpr) return found;
        var stmt = EnclosingStatement(m, e);
        if (stmt == null) return found;
        var scopeNode = EnclosingScopeNode(m, e);
        string key = Normalised(m, e);

        // a name that changes later makes later copies mean something else
        foreach (var n in AstChildren.Descendants(e).OfType<NameExpr>())
            if (m.Semantics.Resolve(n) is { } sym && Canon(sym).Declarations.Any(d => d.Node.Start > stmt.Start)) return found;

        foreach (var n in AstChildren.Descendants(scopeNode))
        {
            if (n is not Expr x || ReferenceEquals(x, e) || x.GetType() != e.GetType() || x.Start < stmt.Start) continue;
            if (Normalised(m, x) != key || !ReferenceEquals(EnclosingScopeNode(m, x), scopeNode)) continue;
            if (IsAssignmentTarget(m, x) || found.Any(f => f.Start <= x.Start && x.End <= f.End)) continue;
            found.Add(x);
        }
        return found;
    }

    static bool IsAssignmentTarget(ModuleInfo m, Expr e)
    {
        var p = m.ParentOf(e);
        return p switch
        {
            AssignStmt a => a.Targets.Any(t => ReferenceEquals(t, e)),
            AugAssignStmt a => ReferenceEquals(a.Target, e),
            AnnAssignStmt a => ReferenceEquals(a.Target, e),
            ForStmt f => ReferenceEquals(f.Target, e),
            Comprehension c => ReferenceEquals(c.Target, e),
            WithItem w => w.Target != null && ReferenceEquals(w.Target, e),
            NamedExpr n => ReferenceEquals(n.Target, e),
            DelStmt => true,
            TupleExpr or ListExpr or StarExpr => m.ParentOf(p) is { } pp && IsAssignmentTarget(m, (Expr)p),
            _ => false,
        };
    }

    static string AsStatementValue(ModuleInfo m, Expr e)
    {
        string text = m.Source[e.Start..e.End];
        return text.Contains('\n') && !(text.StartsWith('(') && text.EndsWith(')')) ? "(" + text + ")" : text;
    }

    List<Edit> ExtractVariable(ModuleInfo m, Selection sel, string? name, bool all)
    {
        var expr = sel.Expression ?? throw new ServiceException("Select an expression to extract.");
        if (expr is YieldExpr or ErrorExpr or StarExpr) throw new ServiceException("This kind of expression cannot be moved into a variable.");
        if (IsAssignmentTarget(m, expr)) throw new ServiceException("An assignment target cannot be extracted.");
        if (m.ParentOf(expr) is Parameter) throw new ServiceException("A parameter cannot be extracted.");

        // the statement the variable goes in front of; expressions that are evaluated later or repeatedly cannot be hoisted out of it
        Node child = expr;
        Stmt? stmt = null;
        for (var p = m.ParentOf(expr); p != null; child = p, p = m.ParentOf(p))
        {
            if (p is LambdaExpr or CompExpr or Comprehension) throw new ServiceException("An expression inside a lambda or comprehension depends on its variables and cannot be extracted.");
            if (p is ExceptHandler or MatchCase) throw new ServiceException("Cannot extract from a handler or case header.");
            if (p is Stmt s) { stmt = s; break; }
        }
        if (stmt == null) throw new ServiceException("Cannot find the statement around the selection.");
        if (stmt is WhileStmt w && ReferenceEquals(w.Test, child)) throw new ServiceException("The condition of a while loop is evaluated on every pass: it cannot be extracted to before the loop.");
        if (stmt is IfStmt && Regex.IsMatch(m.Source[stmt.Start..Math.Min(stmt.Start + 5, m.Source.Length)], @"^elif\b"))
            throw new ServiceException("An elif condition cannot be moved in front of the whole if chain.");
        if (!StartsLine(m.Source, stmt.Start)) throw new ServiceException("The statement shares its line with other code; put it on its own line first.");

        name = CheckName(m, expr, name);
        var targets = all ? Occurrences(m, expr) : new List<Expr> { expr };

        string nl = NewLine(m);
        int lineStart = LineStart(m.Source, stmt.Start);
        string indent = m.Source[lineStart..stmt.Start];
        var edits = new List<Edit> { new(lineStart, lineStart, indent + name + " = " + AsStatementValue(m, expr) + nl) };
        foreach (var t in targets) edits.Add(ReferenceEquals(t, expr) ? new Edit(sel.Start, sel.End, name) : new Edit(t.Start, t.End, name));
        return edits;
    }

    List<Edit> ExtractConstant(ModuleInfo m, Selection sel, string? name, bool all)
    {
        var expr = sel.Expression ?? throw new ServiceException("Select an expression to extract.");
        if (expr is YieldExpr or ErrorExpr or StarExpr or AwaitExpr) throw new ServiceException("This kind of expression cannot become a constant.");
        if (IsAssignmentTarget(m, expr)) throw new ServiceException("An assignment target cannot be extracted.");

        // everything the expression reads must exist at module level
        bool usesModuleNames = false;
        foreach (var n in AstChildren.Descendants(expr).OfType<NameExpr>())
        {
            var r = m.Semantics.ReferenceOf(n);
            if (r == null || r.IsBuiltin) continue;
            var sym = r.Symbol is { } rs ? Canon(rs) : null;
            if (sym == null) continue;
            if (sym.Scope.Kind != ScopeKind.Module) throw new ServiceException($"'{n.Name}' is a local name: the expression cannot move to module level.");
            usesModuleNames = true;
        }
        if (!IsPure(expr)) throw new ServiceException("An expression with calls can give a different value each time: only constants can be extracted.");

        name = CheckName(m, expr, name, constant: true);
        string nl = NewLine(m);
        var targets = new List<Expr> { expr };
        if (all && !usesModuleNames)
            foreach (var n in AstChildren.Descendants(m.Ast))
                if (n is Expr x && !ReferenceEquals(x, expr) && x.GetType() == expr.GetType() && Normalised(m, x) == Normalised(m, expr)
                    && !IsAssignmentTarget(m, x) && !targets.Any(f => f.Start <= x.Start && x.End <= f.End)) targets.Add(x);

        string line = name + " = " + AsStatementValue(m, expr) + nl;
        int at;
        string text;
        if (usesModuleNames)
        {
            // it reads other module-level names: it has to come after them, so directly before the statement that uses it
            Node top = expr;
            for (var p = m.ParentOf(top); p != null && p is not PyModule; p = m.ParentOf(top)) top = p;
            at = LineStart(m.Source, top.Start);
            text = line + nl;
        }
        else
        {
            (at, text) = ModuleConstantPlace(m, line, nl);
        }

        var edits = new List<Edit> { new(at, at, text) };
        foreach (var t in targets) edits.Add(ReferenceEquals(t, expr) ? new Edit(sel.Start, sel.End, name) : new Edit(t.Start, t.End, name));
        return edits;
    }

    /// <summary>After the docstring and the imports at the top of the file.</summary>
    static (int At, string Text) ModuleConstantPlace(ModuleInfo m, string line, string nl)
    {
        var body = m.Ast.Body;
        string text = m.Source;
        int idx = 0;
        Stmt? last = null;
        if (body.Count > 0 && body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) { last = body[0]; idx = 1; }
        bool imports = false;
        for (; idx < body.Count && body[idx] is ImportStmt or ImportFromStmt; idx++) { last = body[idx]; imports = true; }
        if (last == null) return (0, line + nl);

        int at = LineEndInclusive(text, last.End);
        bool endsWithNewline = at > 0 && (text[at - 1] == '\n' || text[at - 1] == '\r');
        string lead = (endsWithNewline ? "" : nl) + nl;
        bool nextBlank = at >= text.Length || text[at] is '\n' or '\r';
        return (at, lead + line + (nextBlank ? "" : nl));
    }
}
