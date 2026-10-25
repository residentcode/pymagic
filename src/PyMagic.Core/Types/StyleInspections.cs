using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>
/// More of PyCharm's Python inspections that need no type information: code that has no effect, constructs that can be
/// written simpler, methods and attributes laid out oddly, string formats that do not fit their arguments, and the
/// pycodestyle (PEP 8) checks PyCharm reports.
/// </summary>
public static class StyleInspections
{
    /// <summary>The line length PyCharm's PEP 8 inspection allows.</summary>
    public const int MaxLineLength = 120;

    public static void Run(ModuleInfo m, List<Finding> found)
    {
        var ctx = new Context(m);
        Walk(ctx, found);
        Classes(ctx, found);
        Functions(ctx, found);
        StatementLists(ctx, found);
        Tokens(ctx, found);
        Strings(ctx, found);
        Pep8Lines(ctx, found);
        BlankLines(ctx, found);
    }

    sealed class Context
    {
        public readonly ModuleInfo M;
        public readonly string Src;
        public readonly int[] LineStarts;
        List<Token>? _tokens;
        HashSet<int>? _insideString;

        public Context(ModuleInfo m)
        {
            M = m; Src = m.Source;
            var starts = new List<int> { 0 };
            for (int i = 0; i < Src.Length; i++)
                if (Src[i] == '\n') starts.Add(i + 1);
            LineStarts = starts.ToArray();
        }

        public List<Token> Tokens => _tokens ??= Lexer.Tokenize(Src);

        public int LineOf(int offset)
        {
            int i = Array.BinarySearch(LineStarts, offset);
            return i >= 0 ? i : ~i - 1;
        }

        public int LineEnd(int line) => line + 1 < LineStarts.Length ? LineStarts[line + 1] - 1 : Src.Length;
        public string LineText(int line) => Src[LineStarts[line]..LineEnd(line)].TrimEnd('\r');

        /// <summary>Lines that start inside a (multi-line) string token.</summary>
        public HashSet<int> LinesInsideStrings
        {
            get
            {
                if (_insideString != null) return _insideString;
                var set = new HashSet<int>();
                foreach (var t in Tokens)
                    if (t.Kind == TokenKind.String)
                        for (int l = LineOf(t.Start) + 1; l <= LineOf(Math.Max(t.End - 1, t.Start)); l++) set.Add(l);
                return _insideString = set;
            }
        }

        public string Text(Node n) => Src[n.Start..n.End];
    }

    static void Add(List<Finding> found, int start, int length, string message, string code, FindingLevel level = FindingLevel.Weak) =>
        found.Add(new Finding(start, Math.Max(length, 1), message, code, level));

    // ------------------------------------------------------------ expressions and statements anywhere

    static void Walk(Context c, List<Finding> found)
    {
        foreach (var node in AstChildren.Descendants(c.M.Ast))
        {
            switch (node)
            {
                case ExprStmt { Value: var v } when HasNoEffect(v):
                    Add(found, v.Start, v.End - v.Start, "Statement seems to have no effect", "statement-effect");
                    break;
                case BinaryExpr { Op: "and" } conj when conj.Left is CompareExpr { Ops.Count: 1 } l && conj.Right is CompareExpr { Ops.Count: 1 } r && Chainable(c, l, r):
                    Add(found, conj.Start, conj.End - conj.Start, "Simplify chained comparison", "chained-comparison");
                    break;
                case CompareExpr { Ops.Count: 1 } cmp when cmp.Ops[0] is "==" or "!=" && (IsBool(cmp.Left) || IsBool(cmp.Comparators[0])) && !(IsBool(cmp.Left) && IsBool(cmp.Comparators[0])):
                    Add(found, cmp.Start, cmp.End - cmp.Start, "Expression can be simplified: compare the value itself, not to True or False", "simplify-boolean");
                    break;
                case CallExpr { Func: NameExpr { Name: "set" }, Args: [{ Name: null, Stars: 0, Value: ListExpr { Items.Count: > 0 } or TupleExpr { Items.Count: > 0 } }] } call
                    when c.M.Semantics.ReferenceOf((NameExpr)call.Func) is { IsBuiltin: true } or null:
                    Add(found, call.Start, call.End - call.Start, "Function call can be replaced with set literal", "set-literal");
                    break;
                case AssignStmt { Targets: [NameExpr target], Value: BinaryExpr { Left: NameExpr left, Op: var op } } assign
                    when left.Name == target.Name && op is "+" or "-" or "*" or "/" or "//" or "%" or "**" or "<<" or ">>" or "&" or "|" or "^" or "@":
                    Add(found, assign.Start, assign.End - assign.Start, "Assignment can be replaced with augmented assignment", "augment-assignment");
                    break;
                case DictExpr dict:
                    foreach (var key in dict.Keys.OfType<Expr>().Where(IsUnhashable))
                        Add(found, key.Start, key.End - key.Start, $"Unhashable type: '{UnhashableName(key)}'", "unhashable", FindingLevel.Warning);
                    break;
                case SetExpr set:
                    foreach (var item in set.Items.Where(IsUnhashable))
                        Add(found, item.Start, item.End - item.Start, $"Unhashable type: '{UnhashableName(item)}'", "unhashable", FindingLevel.Warning);
                    break;
                case GlobalStmt g when InFunction(c.M, g):
                    foreach (var name in g.Names)
                    {
                        var module = c.M.Semantics.ModuleScope;
                        if (module.Symbols.TryGetValue(name, out var sym) && (sym.Declarations.Count > 0 || sym.Redirect != null)) continue;
                        int at = c.Src.IndexOf(name, g.Start, StringComparison.Ordinal);
                        Add(found, at >= 0 ? at : g.Start, name.Length, $"Global variable '{name}' is undefined at the module level", "global-undefined", FindingLevel.Warning);
                    }
                    break;
                case CallExpr { Func: NameExpr { Name: "super" }, Args: { Count: 2 } sargs } sup when sargs[0].Value is NameExpr first && EnclosingClass(c.M, sup) is { } owner && owner.Name != first.Name:
                    Add(found, first.Start, first.End - first.Start, "Wrong first argument passed to 'super()'", "super-arguments", FindingLevel.Warning);
                    break;
                case ImportStmt { Names.Count: > 1 } imp:
                    Add(found, imp.Start, imp.End - imp.Start, "PEP 8: E401 multiple imports on one line", "pep8-statements");
                    break;
                case AssignStmt { Targets: [NameExpr], Value: LambdaExpr } lam:
                    Add(found, lam.Start, lam.End - lam.Start, "PEP 8: E731 do not assign a lambda expression, use a def", "pep8-statements");
                    break;
            }
            AmbiguousNames(c, node, found);
        }
    }

    static bool InFunction(ModuleInfo m, Node n)
    {
        for (var p = m.ParentOf(n); p != null; p = m.ParentOf(p))
            if (p is FunctionDef or LambdaExpr) return true;
        return false;
    }

    static ClassDef? EnclosingClass(ModuleInfo m, Node n)
    {
        for (var p = m.ParentOf(n); p != null; p = m.ParentOf(p))
            if (p is ClassDef cd) return cd;
        return null;
    }

    static bool HasNoEffect(Expr v) => v switch
    {
        NameExpr => true,
        ConstExpr { Kind: ConstKind.Str or ConstKind.Ellipsis } => false,
        ConstExpr => true,
        UnaryExpr or CompareExpr or LambdaExpr => true,
        BinaryExpr { Op: "and" or "or" } b => !ContainsCall(b),
        BinaryExpr => !ContainsCall(v),
        TupleExpr or ListExpr or SetExpr or DictExpr => !ContainsCall(v),
        _ => false,
    };

    static bool ContainsCall(Expr e) => AstChildren.Descendants(e).Any(n => n is CallExpr or AwaitExpr or YieldExpr or NamedExpr);

    static bool IsBool(Expr e) => e is ConstExpr { Kind: ConstKind.True or ConstKind.False };

    static bool IsUnhashable(Expr e) => e is ListExpr or DictExpr or SetExpr or CompExpr { Kind: CompKind.List or CompKind.Dict or CompKind.Set };

    static string UnhashableName(Expr e) => e switch
    {
        ListExpr or CompExpr { Kind: CompKind.List } => "list",
        DictExpr or CompExpr { Kind: CompKind.Dict } => "dict",
        _ => "set",
    };

    /// <summary>`a &lt; b and b &lt; c`: the right-hand side of the first comparison is the left-hand side of the second, and both point the same way.</summary>
    static bool Chainable(Context c, CompareExpr l, CompareExpr r)
    {
        static int Family(string op) => op switch { "<" or "<=" => 1, ">" or ">=" => 2, "==" => 3, _ => 0 };
        int f = Family(l.Ops[0]);
        if (f == 0 || f != Family(r.Ops[0])) return false;
        return c.Text(l.Comparators[0]) == c.Text(r.Left) && !ContainsCall(l.Comparators[0]);
    }

    static void AmbiguousNames(Context c, Node node, List<Finding> found)
    {
        void Check(string name, int start)
        {
            if (name is "l" or "O" or "I")
                Add(found, start, 1, $"PEP 8: E741 ambiguous variable name '{name}'", "pep8-ambiguous-name");
        }
        switch (node)
        {
            case AssignStmt a:
                foreach (var t in a.Targets.OfType<NameExpr>()) Check(t.Name, t.Start);
                break;
            case ForStmt { Target: NameExpr t }: Check(t.Name, t.Start); break;
            case Parameter p when p.Kind != ParamKind.Slash && p.Kind != ParamKind.KwOnlySeparator:
            {
                int i = c.Src.IndexOf(p.Name, p.Start, StringComparison.Ordinal);
                if (i >= 0 && i < p.End) Check(p.Name, i);
                break;
            }
            case WithItem { Target: NameExpr t }: Check(t.Name, t.Start); break;
        }
    }

    // ------------------------------------------------------------ classes and functions

    static readonly HashSet<string> InitMethods = new(StringComparer.Ordinal)
        { "__init__", "__new__", "__post_init__", "setUp", "setUpClass", "setup", "setup_method", "asyncSetUp", "__init_subclass__", "setUpTestData" };

    static bool HasBases(ClassDef c) => c.Bases.Any(b => b.Name == null && b.Stars == 0 && !(b.Value is NameExpr { Name: "object" }));

    static void Classes(Context c, List<Finding> found)
    {
        foreach (var cd in AstChildren.Descendants(c.M.Ast).OfType<ClassDef>())
        {
            var methods = cd.Body.OfType<FunctionDef>().ToList();
            // attributes defined outside __init__ (a class with bases may get them from a base: left alone)
            if (!HasBases(cd) && cd.Decorators.Count == 0)
            {
                var known = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in cd.Body)
                    foreach (var name in BoundNames(s)) known.Add(name);
                foreach (var init in methods.Where(f => InitMethods.Contains(f.Name)))
                    foreach (var (attr, _) in SelfAttributes(init)) known.Add(attr);
                var reported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in methods.Where(f => !InitMethods.Contains(f.Name) && f.Decorators.Count == 0))
                    foreach (var (attr, at) in SelfAttributes(f))
                        if (!known.Contains(attr) && reported.Add(attr))
                            Add(found, at.End - attr.Length, attr.Length, $"Instance attribute {attr} defined outside __init__", "attribute-outside-init");
            }
            foreach (var f in methods) MethodChecks(c, cd, f, found);
        }
    }

    static IEnumerable<string> BoundNames(Stmt s)
    {
        switch (s)
        {
            case AssignStmt a:
                foreach (var t in a.Targets.OfType<NameExpr>()) yield return t.Name;
                break;
            case AnnAssignStmt { Target: NameExpr t }: yield return t.Name; break;
            case FunctionDef f: yield return f.Name; break;
            case ClassDef k: yield return k.Name; break;
        }
    }

    /// <summary>`self.x = ...` in a method (not inside a nested function): the attribute and the attribute expression.</summary>
    static IEnumerable<(string Attr, AttributeExpr Node)> SelfAttributes(FunctionDef f)
    {
        string? self = f.Params.FirstOrDefault(p => p.Kind == ParamKind.Normal)?.Name;
        if (self == null) yield break;
        foreach (var n in Shallow(f.Body))
        {
            var targets = n switch
            {
                AssignStmt a => a.Targets,
                AugAssignStmt au => new[] { au.Target },
                AnnAssignStmt { Value: not null } an => new[] { an.Target },
                _ => (IReadOnlyList<Expr>)Array.Empty<Expr>(),
            };
            foreach (var t in targets.SelectMany(Flatten))
                if (t is AttributeExpr { Value: NameExpr v } ae && v.Name == self) yield return (ae.Attr, ae);
        }
    }

    static IEnumerable<Expr> Flatten(Expr e) => e switch
    {
        TupleExpr t => t.Items.SelectMany(Flatten),
        ListExpr l => l.Items.SelectMany(Flatten),
        StarExpr s => Flatten(s.Value),
        _ => new[] { e },
    };

    /// <summary>The nodes of a body, without those inside nested functions, classes and lambdas.</summary>
    static IEnumerable<Node> Shallow(IEnumerable<Stmt> body)
    {
        foreach (var s in body)
            foreach (var n in ShallowNode(s)) yield return n;
    }

    static IEnumerable<Node> ShallowNode(Node n)
    {
        yield return n;
        if (n is FunctionDef or ClassDef or LambdaExpr) yield break;
        foreach (var child in AstChildren.Of(n))
            foreach (var x in ShallowNode(child)) yield return x;
    }

    static void MethodChecks(Context c, ClassDef cd, FunctionDef f, List<Finding> found)
    {
        string? first = f.Params.FirstOrDefault(p => p.Kind == ParamKind.Normal)?.Name;
        bool plain = f.Decorators.Count == 0;
        bool dunder = f.Name.StartsWith("__") && f.Name.EndsWith("__");

        if (f.Name == "__init__")
            foreach (var r in Shallow(f.Body).OfType<ReturnStmt>().Where(r => r.Value != null && !(r.Value is ConstExpr { Kind: ConstKind.None })))
                Add(found, r.Start, r.End - r.Start, "__init__ should return None", "return-from-init", FindingLevel.Warning);

        if (first != null && f.Params[0].Kind == ParamKind.Normal && (plain || f.Decorators.All(d => d is NameExpr { Name: "classmethod" })))
            foreach (var a in Shallow(f.Body).OfType<AssignStmt>().Where(a => a.Targets.Any(t => t is NameExpr n && n.Name == first)))
                Add(found, a.Start, a.End - a.Start, $"Assignment to the first argument '{first}' of the method", "first-arg-assignment");

        // a method that never looks at self: it could be static
        if (plain && !dunder && first == "self" && !HasBases(cd) && !IsStub(f.Body)
            && !AstChildren.Descendants(f).Any(n => n is NameExpr { Name: "self" or "super" or "locals" or "vars" } && !ReferenceEquals(n, f)))
            Add(found, f.NameStart, f.Name.Length, $"Method '{f.Name}' may be 'static'", "method-may-be-static");
    }

    static bool IsStub(IReadOnlyList<Stmt> body) => body.All(s => s switch
    {
        PassStmt => true,
        ExprStmt { Value: ConstExpr { Kind: ConstKind.Str or ConstKind.Ellipsis } } => true,
        RaiseStmt { Exc: NameExpr { Name: "NotImplementedError" } } => true,
        RaiseStmt { Exc: CallExpr { Func: NameExpr { Name: "NotImplementedError" } } } => true,
        _ => false,
    });

    static void Functions(Context c, List<Finding> found)
    {
        foreach (var f in AstChildren.Descendants(c.M.Ast).OfType<FunctionDef>())
            InconsistentReturns(f, found);
        foreach (var loop in AstChildren.Descendants(c.M.Ast))
        {
            string? name = loop switch { ForStmt { Target: NameExpr t } => t.Name, _ => null };
            IReadOnlyList<Stmt>? body = loop is ForStmt fs ? fs.Body : null;
            if (loop is WithStmt ws && ws.Items.Count == 1 && ws.Items[0].Target is NameExpr wt) { name = wt.Name; body = ws.Body; }
            if (name == null || body == null) continue;
            foreach (var a in Shallow(body).OfType<AssignStmt>().Where(a => a.Targets.Any(t => t is NameExpr n && n.Name == name)))
                Add(found, a.Start, a.End - a.Start, $"Variable '{name}' overwrites the iteration or context variable", "loop-variable-assignment");
        }
    }

    static void InconsistentReturns(FunctionDef f, List<Finding> found)
    {
        var shallow = Shallow(f.Body).ToList();
        if (shallow.Any(n => n is YieldExpr)) return;
        var returns = shallow.OfType<ReturnStmt>().ToList();
        int valued = returns.Count(r => r.Value != null);
        if (valued == 0) return;
        foreach (var r in returns.Where(r => r.Value == null))
            Add(found, r.Start, r.End - r.Start, "Inconsistent return statements: some return a value, this one does not", "inconsistent-returns");
        if (!Terminates(f.Body) && !IsStub(f.Body))
            Add(found, f.NameStart, f.Name.Length, "Inconsistent return statements: the end of the function returns nothing while other paths return a value", "inconsistent-returns");
    }

    /// <summary>True when control cannot run off the end of the statements.</summary>
    static bool Terminates(IReadOnlyList<Stmt> body)
    {
        if (body.Count == 0) return false;
        switch (body[^1])
        {
            case ReturnStmt or RaiseStmt: return true;
            case AssertStmt { Test: ConstExpr { Kind: ConstKind.False } }: return true;
            case ExprStmt { Value: CallExpr { Func: NameExpr { Name: "exit" or "quit" } } }: return true;
            case ExprStmt { Value: CallExpr { Func: AttributeExpr { Attr: "exit" or "_exit" or "abort" } } }: return true;
            case IfStmt i: return i.Orelse.Count > 0 && Terminates(i.Body) && Terminates(i.Orelse);
            case WithStmt w: return Terminates(w.Body);
            case WhileStmt { Test: ConstExpr { Kind: ConstKind.True } } w: return !Shallow(w.Body).Any(n => n is BreakStmt);
            case TryStmt t:
                if (t.Finally.Count > 0 && Terminates(t.Finally)) return true;
                return Terminates(t.Orelse.Count > 0 ? t.Orelse : t.Body) && t.Handlers.All(h => Terminates(h.Body));
            case MatchStmt ms:
                return ms.Cases.Any(cs => cs.Pattern is MatchCapture && cs.Guard == null) && ms.Cases.All(cs => Terminates(cs.Body));
        }
        return false;
    }

    // ------------------------------------------------------------ neighbours in a statement list

    static IEnumerable<IReadOnlyList<Stmt>> BodiesOf(Node n)
    {
        switch (n)
        {
            case Module mod: yield return mod.Body; break;
            case FunctionDef f: yield return f.Body; break;
            case ClassDef k: yield return k.Body; break;
            case IfStmt i: yield return i.Body; yield return i.Orelse; break;
            case WhileStmt w: yield return w.Body; yield return w.Orelse; break;
            case ForStmt fo: yield return fo.Body; yield return fo.Orelse; break;
            case WithStmt ws: yield return ws.Body; break;
            case TryStmt t:
                yield return t.Body; yield return t.Orelse; yield return t.Finally;
                foreach (var h in t.Handlers) yield return h.Body;
                break;
            case MatchStmt ms:
                foreach (var cs in ms.Cases) yield return cs.Body;
                break;
        }
    }

    static void StatementLists(Context c, List<Finding> found)
    {
        foreach (var node in new Node[] { c.M.Ast }.Concat(AstChildren.Descendants(c.M.Ast).Where(n => !ReferenceEquals(n, c.M.Ast))))
            foreach (var body in BodiesOf(node))
            {
                for (int i = 0; i + 1 < body.Count; i++)
                {
                    if (body[i] is AssignStmt { Targets: [NameExpr name], Value: ListExpr } lst
                        && body[i + 1] is ExprStmt { Value: CallExpr { Func: AttributeExpr { Value: NameExpr recv, Attr: "append" }, Args: [{ Name: null, Stars: 0, Value: var arg }] } }
                        && recv.Name == name.Name && !AstChildren.Descendants(arg).Any(n => n is NameExpr x && x.Name == name.Name))
                        Add(found, lst.Start, lst.End - lst.Start, "This list creation could be rewritten as a list literal", "list-creation");
                    if (body[i] is AssignStmt { Targets: [NameExpr dname], Value: DictExpr } dct
                        && body[i + 1] is AssignStmt { Targets: [SubscriptExpr { Value: NameExpr drecv, Index: var key }], Value: var val }
                        && drecv.Name == dname.Name && !AstChildren.Descendants(key).Concat(AstChildren.Descendants(val)).Any(n => n is NameExpr x && x.Name == dname.Name))
                        Add(found, dct.Start, dct.End - dct.Start, "This dictionary creation could be rewritten as a dictionary literal", "dict-creation");
                }
                // statements on the line of their header: `if x: y`
            }
        foreach (var node in AstChildren.Descendants(c.M.Ast))
        {
            (int HeaderStart, IReadOnlyList<Stmt> Body)? header = node switch
            {
                IfStmt i => (i.Start, i.Body),
                WhileStmt w => (w.Start, w.Body),
                ForStmt f => (f.Start, f.Body),
                WithStmt w => (w.Start, w.Body),
                ClassDef cd => (cd.NameStart, cd.Body),
                _ => null,
            };
            if (header is not { } h || h.Body.Count == 0) continue;
            if (node is FunctionDef or ClassDef && h.Body is [ExprStmt { Value: ConstExpr { Kind: ConstKind.Ellipsis } }]) continue;
            if (c.LineOf(h.HeaderStart) == c.LineOf(h.Body[0].Start))
                Add(found, h.Body[0].Start, h.Body[0].End - h.Body[0].Start, "PEP 8: E701 multiple statements on one line (colon)", "pep8-statements");
        }
    }

    // ------------------------------------------------------------ tokens: semicolons, parentheses, backslashes, comments

    static void Tokens(Context c, List<Finding> found)
    {
        var toks = c.Tokens;
        int depth = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            string text = t.Kind == TokenKind.Operator ? t.Text(c.Src) : "";
            if (text is "(" or "[" or "{") depth++;
            else if (text is ")" or "]" or "}") depth = Math.Max(0, depth - 1);

            if (text == ";")
            {
                int j = i + 1;
                while (j < toks.Count && toks[j].Kind == TokenKind.Comment) j++;
                bool trailing = j >= toks.Count || toks[j].Kind is TokenKind.Newline or TokenKind.EndOfFile;
                if (trailing) Add(found, t.Start, 1, "Trailing semicolon in the statement", "trailing-semicolon");
                else Add(found, t.Start, 1, "PEP 8: E702 multiple statements on one line (semicolon)", "pep8-statements");
            }

            if (depth > 0 && i + 1 < toks.Count)
            {
                int gapStart = t.End, gapEnd = toks[i + 1].Start;
                for (int k = gapStart; k < gapEnd - 1; k++)
                    if (c.Src[k] == '\\' && (c.Src[k + 1] == '\n' || c.Src[k + 1] == '\r'))
                        Add(found, k, 1, "Unnecessary backslash in expression", "unnecessary-backslash");
            }

            if (t.Kind == TokenKind.Comment) CommentStyle(c, i, found);
            if (t.Kind == TokenKind.Keyword && t.Text(c.Src) is "if" or "while" or "elif" or "return") RedundantParentheses(c, i, found);
        }
    }

    static void CommentStyle(Context c, int index, List<Finding> found)
    {
        var t = c.Tokens[index];
        string text = t.Text(c.Src);
        int line = c.LineOf(t.Start);
        int lineStart = c.LineStarts[line];
        bool inline = c.Src[lineStart..t.Start].Trim().Length > 0;
        if (inline)
        {
            int spaces = 0;
            while (t.Start - 1 - spaces >= lineStart && c.Src[t.Start - 1 - spaces] == ' ') spaces++;
            if (spaces < 2) Add(found, t.Start, 1, "PEP 8: E261 at least two spaces before inline comment", "pep8-comments");
            if (text.Length > 1 && !text.StartsWith("# ") && !text.StartsWith("#:") && !text.StartsWith("#!"))
                Add(found, t.Start, 1, "PEP 8: E262 inline comment should start with '# '", "pep8-comments");
        }
        else
        {
            if (text.Length > 1 && text[1] != ' ' && text[1] != '#' && !text.StartsWith("#!") && !text.StartsWith("#:"))
                Add(found, t.Start, 1, "PEP 8: E265 block comment should start with '# '", "pep8-comments");
        }
    }

    /// <summary>`if (x):`, `while (x):`, `return (x)`: the parentheses around the whole expression.</summary>
    static void RedundantParentheses(Context c, int keywordIndex, List<Finding> found)
    {
        var toks = c.Tokens;
        if (keywordIndex + 1 >= toks.Count || toks[keywordIndex + 1].Kind != TokenKind.Operator || toks[keywordIndex + 1].Text(c.Src) != "(") return;
        int depth = 0, close = -1;
        bool comma = false;
        for (int k = keywordIndex + 1; k < toks.Count; k++)
        {
            string text = toks[k].Kind == TokenKind.Operator ? toks[k].Text(c.Src) : toks[k].Kind == TokenKind.Keyword ? toks[k].Text(c.Src) : "";
            if (text is "(" or "[" or "{") depth++;
            else if (text is ")" or "]" or "}") { depth--; if (depth == 0) { close = k; break; } }
            else if (depth == 1 && (text is "," or ":=" or "yield" or "lambda" or "for" or "if" or "else")) comma = true;
            if (toks[k].Kind is TokenKind.Newline or TokenKind.EndOfFile) return;
        }
        if (close < 0 || comma || close + 1 >= toks.Count || close == keywordIndex + 2) return;
        var open = toks[keywordIndex + 1];
        if (c.LineOf(open.Start) != c.LineOf(toks[close].Start)) return;
        var after = toks[close + 1];
        string kw = toks[keywordIndex].Text(c.Src);
        bool ends = kw == "return" ? after.Kind is TokenKind.Newline or TokenKind.EndOfFile or TokenKind.Comment
            : after.Kind == TokenKind.Operator && after.Text(c.Src) == ":";
        if (!ends) return;
        Add(found, open.Start, toks[close].End - open.Start, "Remove redundant parentheses", "redundant-parentheses");
    }

    // ------------------------------------------------------------ strings: escapes and formats

    static readonly Regex PercentFormat = new(@"%(?:\((?<name>[^)]*)\))?[#0\- +]*(?<width>\*|\d+)?(?:\.(?<prec>\*|\d+))?[hlL]?(?<conv>[diouxXeEfFgGcrsa%])", RegexOptions.Compiled);

    static void Strings(Context c, List<Finding> found)
    {
        foreach (var t in c.Tokens)
            if (t.Kind == TokenKind.String) InvalidEscapes(c, t, found);

        foreach (var node in AstChildren.Descendants(c.M.Ast))
        {
            if (node is BinaryExpr { Op: "%", Left: ConstExpr { Kind: ConstKind.Str, IsFString: false } lit } pct) PercentCheck(c, pct, lit, found);
            if (node is CallExpr { Func: AttributeExpr { Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } fmt, Attr: "format" } } call) FormatCheck(c, call, fmt, found);
        }
    }

    static void InvalidEscapes(Context c, Token t, List<Finding> found)
    {
        string text = t.Text(c.Src);
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length) return;
        string prefix = text[..q].ToLowerInvariant();
        if (prefix.Contains('r')) return;
        bool bytes = prefix.Contains('b');
        for (int i = q; i < text.Length - 1; i++)
        {
            if (text[i] != '\\') continue;
            char n = text[i + 1];
            bool valid = n is '\n' or '\r' or '\\' or '\'' or '"' or 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v' or 'x' or (>= '0' and <= '7')
                || !bytes && n is 'N' or 'u' or 'U';
            if (!valid) Add(found, t.Start + i, 2, $"Invalid escape sequence '\\{n}'", "invalid-escape");
            i++;
        }
    }

    static void PercentCheck(Context c, BinaryExpr pct, ConstExpr lit, List<Finding> found)
    {
        string text = c.Src[lit.Start..lit.End];
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length || text[..q].ToLowerInvariant().Contains('b')) return;
        var matches = PercentFormat.Matches(text[q..]).Where(x => x.Groups["conv"].Value != "%").ToList();
        if (text.Contains('\\') && text[q..].Contains("\\\n")) return;
        bool named = matches.Any(x => x.Groups["name"].Success);
        int positional = matches.Count(x => !x.Groups["name"].Success) + matches.Count(x => x.Groups["width"].Value == "*" || x.Groups["prec"].Value == "*") * 1;
        // `%` that is not a conversion at all: left to the interpreter
        if (Regex.Matches(text[q..], "%").Count - 2 * Regex.Matches(text[q..], "%%").Count != matches.Count) return;
        switch (pct.Right)
        {
            case TupleExpr { Items: var items } when !items.Any(i => i is StarExpr):
                if (named) { Add(found, pct.Right.Start, pct.Right.End - pct.Right.Start, "Format requires a mapping", "string-format", FindingLevel.Warning); return; }
                if (items.Count < positional) Add(found, pct.Right.Start, pct.Right.End - pct.Right.Start, "Too few arguments for format string", "string-format", FindingLevel.Warning);
                else if (items.Count > positional) Add(found, pct.Right.Start, pct.Right.End - pct.Right.Start, "Too many arguments for format string", "string-format", FindingLevel.Warning);
                break;
            case DictExpr:
            case NameExpr or AttributeExpr or CallExpr or SubscriptExpr:
                break;      // a mapping or a value we cannot count
            case ConstExpr or BinaryExpr or ListExpr or SetExpr or CompExpr or LambdaExpr:
                if (named) break;
                if (positional > 1 && pct.Right is not BinaryExpr) Add(found, pct.Right.Start, pct.Right.End - pct.Right.Start, "Too few arguments for format string", "string-format", FindingLevel.Warning);
                else if (positional == 0 && matches.Count == 0 && pct.Right is ConstExpr) Add(found, pct.Right.Start, pct.Right.End - pct.Right.Start, "Too many arguments for format string", "string-format", FindingLevel.Warning);
                break;
        }
    }

    static void FormatCheck(Context c, CallExpr call, ConstExpr lit, List<Finding> found)
    {
        if (call.Args.Any(a => a.Stars != 0)) return;
        string text = c.Src[lit.Start..lit.End];
        int q = 0;
        while (q < text.Length && text[q] != '"' && text[q] != '\'') q++;
        if (q >= text.Length) return;
        string body = text[q..];
        // fields: {} {0} {name} {0.attr} {name[key]} {:>{width}}; doubled braces are literal
        body = body.Replace("{{", "").Replace("}}", "");
        int auto = 0, maxIndex = -1;
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool explicitIndex = false;
        foreach (Match f in Regex.Matches(body, @"\{(?<field>[^{}!:]*)(?:![rsa])?(?::(?<spec>(?:[^{}]|\{[^{}]*\})*))?\}"))
        {
            var fields = new List<string> { f.Groups["field"].Value };
            if (f.Groups["spec"].Success)
                foreach (Match nested in Regex.Matches(f.Groups["spec"].Value, @"\{([^{}!:]*)\}")) fields.Add(nested.Groups[1].Value);
            foreach (var field in fields)
            {
                string head = Regex.Match(field, @"^[^.\[]*").Value;
                if (head.Length == 0) auto++;
                else if (head.All(char.IsDigit)) { explicitIndex = true; maxIndex = Math.Max(maxIndex, int.Parse(head)); }
                else names.Add(head);
            }
        }
        int positional = call.Args.Count(a => a.Name == null);
        var keywords = call.Args.Where(a => a.Name != null).Select(a => a.Name!).ToHashSet(StringComparer.Ordinal);
        int needed = explicitIndex ? maxIndex + 1 : auto;
        if (auto > 0 && explicitIndex) return;       // mixing is an error of its own
        if (positional < needed)
            Add(found, call.Start, call.End - call.Start, "Too few arguments for format string", "string-format", FindingLevel.Warning);
        else if (positional > needed && !explicitIndex)
            Add(found, call.Start, call.End - call.Start, "Too many arguments for format string", "string-format", FindingLevel.Warning);
        foreach (var missing in names.Where(n => !keywords.Contains(n)))
            Add(found, call.Start, call.End - call.Start, $"Key '{missing}' has no corresponding argument", "string-format", FindingLevel.Warning);
    }

    // ------------------------------------------------------------ PEP 8: physical lines and blank lines

    static void Pep8Lines(Context c, List<Finding> found)
    {
        var inside = new HashSet<int>(c.LinesInsideStrings);
        foreach (var t in c.Tokens.Where(t => t.Kind == TokenKind.String))
            for (int l = c.LineOf(t.Start); l < c.LineOf(Math.Max(t.End - 1, t.Start)); l++) inside.Add(l);
        int lines = c.LineStarts.Length;
        for (int l = 0; l < lines; l++)
        {
            string text = c.LineText(l);
            int start = c.LineStarts[l];
            if (text.Length > MaxLineLength && text.Trim().Contains(' ') && !(text.TrimStart().StartsWith('#') && !text.TrimStart()[1..].Trim().Contains(' ')))
                Add(found, start + MaxLineLength, text.Length - MaxLineLength, $"PEP 8: E501 line too long ({text.Length} > {MaxLineLength} characters)", "pep8-line-length");
            if (inside.Contains(l) || text.Length == 0 && l == lines - 1) continue;
            if (text.Trim().Length == 0 && text.Length > 0)
                Add(found, start, text.Length, "PEP 8: W293 blank line contains whitespace", "pep8-whitespace");
            else if (text.Length > 0 && (text[^1] == ' ' || text[^1] == '\t'))
                Add(found, start + text.TrimEnd().Length, text.Length - text.TrimEnd().Length, "PEP 8: W291 trailing whitespace", "pep8-whitespace");
        }
        string src = c.Src;
        if (src.Length > 0 && src[^1] != '\n')
            Add(found, src.Length - 1, 1, "PEP 8: W292 no newline at end of file", "pep8-whitespace");
        else if (src.Length > 1 && src.EndsWith("\n\n"))
            Add(found, src.TrimEnd('\n', '\r').Length, src.Length - src.TrimEnd('\n', '\r').Length, "PEP 8: W391 blank line at end of file", "pep8-whitespace");
    }

    /// <summary>Blank lines directly above a line, and whether comments sit between them and the line (the comment lines are skipped).</summary>
    static int BlankLinesAbove(Context c, int line, bool skipComments)
    {
        int l = line - 1;
        if (skipComments)
            while (l >= 0 && c.LineText(l).TrimStart().StartsWith('#') && !c.LinesInsideStrings.Contains(l)) l--;
        int blank = 0;
        while (l >= 0 && c.LineText(l).Trim().Length == 0) { blank++; l--; }
        return l < 0 ? -1 : blank;
    }

    static void BlankLines(Context c, List<Finding> found)
    {
        void Statement(Stmt s, bool topLevel, bool inClass, Stmt? previous, int index)
        {
            int first = s switch { FunctionDef f when f.Decorators.Count > 0 => f.Decorators[0].Start, ClassDef k when k.Decorators.Count > 0 => k.Decorators[0].Start, _ => s.Start };
            int line = c.LineOf(first);
            // the '@' of a decorator starts one position before its expression
            string lineText = c.LineText(line);
            int atSign = lineText.IndexOf('@');
            if (first != s.Start && atSign >= 0 && atSign < first - c.LineStarts[line]) { }
            int directBlank = BlankLinesAbove(c, line, skipComments: false);
            int blank = BlankLinesAbove(c, line, skipComments: true);
            bool isDef = s is FunctionDef or ClassDef;
            if (directBlank > (topLevel ? 2 : 1) && index > 0)
                Add(found, c.LineStarts[line], Math.Max(1, lineText.Length), $"PEP 8: E303 too many blank lines ({directBlank})", "pep8-blank-lines");
            else if (topLevel && isDef && index > 0 && blank >= 0 && blank < 2 && !OneLinerGroup(c, previous, s))
                Add(found, c.LineStarts[line], Math.Max(1, lineText.Length), $"PEP 8: E302 expected 2 blank lines, found {Math.Max(blank, 0)}", "pep8-blank-lines");
            else if (topLevel && !isDef && index > 0 && previous is FunctionDef or ClassDef && blank >= 0 && blank < 2)
                Add(found, c.LineStarts[line], Math.Max(1, lineText.Length), $"PEP 8: E305 expected 2 blank lines after class or function definition, found {Math.Max(blank, 0)}", "pep8-blank-lines");
            else if (inClass && s is FunctionDef && index > 0 && blank >= 0 && blank < 1 && !OneLinerGroup(c, previous, s) && !(previous is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }))
                Add(found, c.LineStarts[line], Math.Max(1, lineText.Length), "PEP 8: E301 expected 1 blank line, found 0", "pep8-blank-lines");
        }

        void Body(IReadOnlyList<Stmt> body, bool topLevel, bool inClass)
        {
            for (int i = 0; i < body.Count; i++)
            {
                Statement(body[i], topLevel, inClass, i > 0 ? body[i - 1] : null, i);
                foreach (var nested in BodiesOf(body[i]))
                    Body(nested, false, body[i] is ClassDef);
            }
        }
        Body(c.M.Ast.Body, true, false);
    }

    /// <summary>Consecutive one-line definitions (overload stubs, `def f(): ...`) may stand together.</summary>
    static bool OneLinerGroup(Context c, Stmt? previous, Stmt s) =>
        previous is FunctionDef pf && s is FunctionDef && c.LineOf(pf.Start) == c.LineOf(pf.End > pf.Start ? pf.End - 1 : pf.Start);
}
