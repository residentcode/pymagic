using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

public sealed partial class LanguageService
{
    /// <summary>What a function body looks like for inlining.</summary>
    sealed record BodyShape(IReadOnlyList<Stmt> Statements, Expr? ReturnValue, bool ReturnsNothing);

    /// <summary>The statements of the body without its docstring, and its single final return; null with the reason when it has any other shape.</summary>
    static BodyShape? ShapeOf(FunctionTarget fn, out string? why)
    {
        why = null;
        var def = fn.Def;
        if (def.IsAsync) { why = "is async"; return null; }
        if (fn.IsConstructor) { why = "is a constructor"; return null; }
        if (fn.Params.Any(p => p.Category is "varArgs" or "kwArgs")) { why = "takes *args or **kwargs"; return null; }

        var body = def.Body.ToList();
        if (body.Count > 0 && body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) body.RemoveAt(0);
        body = body.Where(s => s is not PassStmt).ToList();

        foreach (var n in Shallow(body))
        {
            switch (n)
            {
                case YieldExpr: why = "is a generator"; return null;
                case AwaitExpr: why = "awaits"; return null;
                case GlobalStmt or NonlocalStmt: why = "declares global or nonlocal names"; return null;
                case FunctionDef or ClassDef:
                    why = "defines a nested function or class"; return null;
            }
        }
        int returns = body.SelectMany(b => Shallow(new[] { (Node)b })).Count(n => n is ReturnStmt);
        if (returns == 0) return new BodyShape(body, null, ReturnsNothing: true);
        if (returns == 1 && body.Count > 0 && body[^1] is ReturnStmt last) return new BodyShape(body.Take(body.Count - 1).ToList(), last.Value, ReturnsNothing: last.Value == null);
        why = "has more than one return, or returns before its last statement";
        return null;
    }

    static bool IsConstantLike(Expr e) => e is ConstExpr || e is UnaryExpr { Operand: ConstExpr } || e is NameExpr { Name: "None" or "True" or "False" };

    /// <summary>Binding strength of an expression (higher binds tighter); atoms are 100.</summary>
    static int Precedence(Expr e) => e switch
    {
        LambdaExpr or NamedExpr or YieldExpr or IfExpr => 0,
        BinaryExpr { Op: "or" } => 1,
        BinaryExpr { Op: "and" } => 2,
        UnaryExpr { Op: "not" } => 3,
        CompareExpr => 4,
        BinaryExpr { Op: "|" } => 5,
        BinaryExpr { Op: "^" } => 6,
        BinaryExpr { Op: "&" } => 7,
        BinaryExpr { Op: "<<" or ">>" } => 8,
        BinaryExpr { Op: "+" or "-" } => 9,
        BinaryExpr { Op: "*" or "/" or "//" or "%" or "@" } => 10,
        UnaryExpr => 11,
        BinaryExpr { Op: "**" } => 12,
        TupleExpr { Parenthesized: false } or StarExpr => -1,
        _ => 100,
    };

    /// <summary>Wraps text in parentheses when the expression it stands for needs them where the use is.</summary>
    static string WrapForContext(ModuleInfo ctx, Expr value, string text, Node use)
    {
        if (IsAtom(value)) return text;
        int p = Precedence(value);
        switch (ctx.ParentOf(use))
        {
            case BinaryExpr b when p >= 0 && p < 100:
            {
                int outer = Precedence(b);
                bool left = ReferenceEquals(b.Left, use);
                bool rightAssociative = b.Op == "**";
                bool needs = p < outer || (p == outer && (rightAssociative ? left : !left));
                return needs ? "(" + text + ")" : text;
            }
            case CompareExpr when p >= 5 && p < 100: return text;
            case UnaryExpr u when p >= 0 && p < 100: return p >= Precedence(u) ? text : "(" + text + ")";
        }
        bool exempt = ctx.ParentOf(use) switch
        {
            AssignStmt a => a.Value is Expr v && ReferenceEquals(v, use),
            ReturnStmt => true,
            ExprStmt => true,
            IfStmt i => ReferenceEquals(i.Test, use),
            WhileStmt w => ReferenceEquals(w.Test, use),
            Argument { Stars: 0 } => value is not (TupleExpr or StarExpr or NamedExpr or YieldExpr),
            _ => false,
        };
        return exempt ? text : "(" + text + ")";
    }

    Dictionary<ModuleInfo, List<Edit>> InlineMethod(Snapshot snap, ModuleInfo m, int offset, bool all)
    {
        var fn = ResolveFunction(snap, m, offset);
        var shape = ShapeOf(fn, out string? shapeWhy) ?? throw new ServiceException($"'{fn.Def.Name}' cannot be inlined: it {shapeWhy}.");
        var ev = snap.Workspace.Evaluator;

        // a function that calls itself would never be finished
        var defScope = fn.Module.Semantics.ScopeOf(fn.Def)!;
        foreach (var n in AstChildren.Descendants(fn.Def).OfType<NameExpr>())
            if (n.Name == fn.Def.Name && fn.Module.Semantics.Resolve(n) is { } rs && Canon(rs).Declarations.Any(d => ReferenceEquals(d.Node, fn.Def)))
                throw new ServiceException($"'{fn.Def.Name}' calls itself.");
        if (AstChildren.Descendants(fn.Def).OfType<AttributeExpr>().Any(a => a.Attr == fn.Def.Name && fn.Class != null && a.Value is NameExpr { Name: var recv } && recv == fn.Implicit?.Name))
            throw new ServiceException($"'{fn.Def.Name}' calls itself.");

        var sites = new List<CallSite>();
        int other = 0;
        if (all)
        {
            (var found, other) = FindCalls(snap, fn);
            sites.AddRange(found);
            if (other > 0) throw new ServiceException($"'{fn.Def.Name}' is also used as a value ({other} reference{(other == 1 ? "" : "s")}), so it cannot be removed. Inline single calls instead.");
            if (sites.Count == 0) throw new ServiceException($"'{fn.Def.Name}' is never called: use safe delete.");
        }
        else
        {
            // the call under the cursor
            CallExpr? call = null;
            foreach (var c in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
                if (offset >= c.Func.Start && offset <= c.End && (call == null || c.End - c.Start < call.End - call.Start))
                {
                    if (!CallsFunction(snap, m, c, fn)) continue;
                    call = c;
                }
            if (call == null) throw new ServiceException("Put the cursor on a call of the function.");
            bool explicitSelf = call.Func is AttributeExpr ae && fn.Class != null && !fn.IsStatic && !fn.IsClassMethod && ev.TypeOf(m, ae.Value) is ClassType { IsInstance: false };
            sites.Add(new CallSite(m, call, explicitSelf));
        }

        var edits = new Dictionary<ModuleInfo, List<Edit>>(ReferenceEqualityComparer.Instance);
        var failures = new List<string>();
        foreach (var site in sites)
        {
            try
            {
                var list = InlineCall(snap, fn, shape, site);
                if (!edits.TryGetValue(site.Module, out var l)) edits[site.Module] = l = new List<Edit>();
                l.AddRange(list);
            }
            catch (ServiceException e) when (all)
            {
                failures.Add($"{System.IO.Path.GetFileName(site.Module.Path ?? site.Module.Name)}:{Lines(site.Module).PositionOf(site.Call.Start).Line + 1} {e.Message}");
            }
        }
        if (failures.Count > 0) throw new ServiceException($"{failures.Count} call{(failures.Count == 1 ? "" : "s")} cannot be inlined, so the function stays: " + string.Join(" | ", failures.Take(3)));

        foreach (var (module, list) in edits)
        {
            var ordered = list.OrderBy(e => e.Start).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].Start < ordered[i - 1].End) throw new ServiceException("Some calls are nested in each other: inline them one at a time.");
        }
        if (all)
        {
            if (!edits.TryGetValue(fn.Module, out var defEdits)) edits[fn.Module] = defEdits = new List<Edit>();
            var delete = DeleteStatement(fn.Module, fn.Def);
            if (defEdits.Any(e => e.Start < delete.End && delete.Start < e.End)) throw new ServiceException("A call lies inside the function itself.");
            defEdits.Add(delete);
        }
        return edits;
    }

    bool CallsFunction(Snapshot snap, ModuleInfo m, CallExpr call, FunctionTarget fn)
    {
        var ev = snap.Workspace.Evaluator;
        var type = ev.TypeOf(m, call.Func);
        foreach (var t in type.Members)
        {
            if (t is FunctionType ft && ReferenceEquals(ft.Info.Node, fn.Def)) return true;
            if (t is OverloadedType o && o.Overloads.Any(f => ReferenceEquals(f.Info.Node, fn.Def))) return true;
        }
        return false;
    }

    /// <summary>The edits that replace one call by the body of the function.</summary>
    List<Edit> InlineCall(Snapshot snap, FunctionTarget fn, BodyShape shape, CallSite site)
    {
        var def = fn.Def;
        var defModule = fn.Module;
        var callModule = site.Module;
        var call = site.Call;
        var map = MapArguments(fn, site);
        if (map.Unsupported != null) throw new ServiceException($"the call {map.Unsupported}.");

        var defScope = defModule.Semantics.ScopeOf(def)!;
        var bodyRoots = def.Body.Cast<Node>().ToList();
        string NL = NewLine(callModule);

        // ---- the call's context
        Node callNode = call;
        var parent = callModule.ParentOf(call);
        Stmt? statement = parent as Stmt;
        bool statementLevel = parent is ExprStmt || parent is ReturnStmt || (parent is AssignStmt a && a.Targets.Count == 1 && ReferenceEquals(a.Value, call));
        bool needsStatements = shape.Statements.Count > 0;
        if (needsStatements && !statementLevel)
            throw new ServiceException("the function has several statements, so its call must be a whole statement (`f(...)`, `x = f(...)` or `return f(...)`).");


        // ---- values for the parameters (and the receiver)
        var receiverName = fn.Implicit?.Name;
        string? receiverText = map.SelfArgument;
        Expr? receiverExpr = null;
        if (receiverName != null && receiverText == null)
        {
            if (call.Func is AttributeExpr callee && !(callee.Value is CallExpr { Func: NameExpr { Name: "super" } }))
            {
                receiverText = callModule.Source[callee.Value.Start..callee.Value.End];
                receiverExpr = callee.Value;
            }
            else if (fn.IsStatic) { /* no receiver */ }
            else throw new ServiceException("the receiver of the method call is not visible here.");
        }

        if (fn.IsClassMethod && receiverName != null && defScope.Symbols.TryGetValue(receiverName, out var clsSym) && clsSym.References.Count > 0
            && !(receiverExpr != null && snap.Workspace.Evaluator.TypeOf(callModule, receiverExpr) is ClassType { IsInstance: false }))
            throw new ServiceException("the body uses 'cls', and the call is not made on the class itself.");
        var values = new Dictionary<string, (string Text, Expr? Node, bool Pure, bool Atom)>();
        foreach (var p in fn.Params)
        {
            if (map.ByParam.TryGetValue(p.Index, out var given))
            {
                // find the argument expression to judge purity and shape
                Expr? node = FindArgument(site, fn, p.Index);
                values[p.Name] = (given.Text, node, node == null || IsPure(node), node != null && IsAtom(node));
            }
            else if (p.Default != null)
            {
                var dnode = p.Node.Default!;
                if (!IsConstantLike(dnode) || dnode is ListExpr or DictExpr or SetExpr)
                    throw new ServiceException($"the default value of '{p.Name}' is not a plain constant, so it cannot be copied into the caller.");
                values[p.Name] = (p.Default, dnode, true, IsAtom(dnode));
            }
            else throw new ServiceException($"the call does not pass '{p.Name}'.");
        }

        // ---- names the body uses from outside must mean the same where the call is
        var callScopeNode = EnclosingScopeNode(callModule, call);
        var callScope = callModule.Semantics.ScopeOf(callScopeNode);
        var wordPattern = new Regex(@"(?<![\w.])[A-Za-z_]\w*");
        foreach (var n in bodyRoots.SelectMany(AstChildren.Descendants).OfType<NameExpr>())
        {
            var r = defModule.Semantics.ReferenceOf(n);
            if (r == null) continue;
            if (r.IsBuiltin)
            {
                if (callScope != null && callScope.Kind != ScopeKind.Module && callScope.Symbols.TryGetValue(n.Name, out var shadow) && shadow.Declarations.Count > 0)
                    throw new ServiceException($"the body uses the builtin '{n.Name}', which is a different variable where the call is.");
                continue;
            }
            var sym = r.Symbol != null ? Canon(r.Symbol) : null;
            if (sym == null || sym.Scope == defScope || sym.Scope.Node.Start >= def.Start && sym.Scope.Node.End <= def.End) continue;   // its own names
            if (sym.Scope.Kind != ScopeKind.Module) throw new ServiceException($"the body uses '{n.Name}' from an enclosing function.");
            if (!ReferenceEquals(defModule, callModule) && !callModule.Semantics.ModuleScope.Symbols.ContainsKey(n.Name))
                throw new ServiceException($"the body uses '{n.Name}' from {defModule.Name}, which is not available in {callModule.Name}.");
            if (callScope != null && callScope.Kind != ScopeKind.Module && callScope.Symbols.TryGetValue(n.Name, out var local) && local.Declarations.Count > 0)
                throw new ServiceException($"the body uses the module-level '{n.Name}', which is a local variable where the call is.");
        }
        foreach (var c in FStrings(bodyRoots))
            foreach (Match w in wordPattern.Matches(c.Text))
                if (defScope.Symbols.ContainsKey(w.Value) || w.Value == receiverName)
                    throw new ServiceException($"the body uses '{w.Value}' inside an f-string, which cannot be rewritten yet.");

        // ---- which parameters need a variable of their own
        var taken = NamesVisibleAt(callModule, call);
        var bindings = new List<(string Name, string Text)>();
        var substitutions = new Dictionary<Symbol, string>();
        var renames = new Dictionary<Symbol, string>();

        int impureCount = values.Values.Count(v => !v.Pure) + (receiverText != null && receiverExpr != null && !IsPure(receiverExpr) ? 1 : 0);

        void Decide(Symbol sym, string preferred, string text, Expr? node, bool pure, bool atom)
        {
            var refs = sym.References.ToList();
            bool inNested = refs.Any(r => r.UseScope != defScope);
            bool assigned = sym.Declarations.Count(d => d.Kind != DeclKind.Parameter) > 0 || refs.Any(r => r.Access.HasFlag(Access.Write));
            bool constant = node != null && IsConstantLike(node);
            bool bind = assigned || (inNested && !constant) || (!pure && (refs.Count != 1 || impureCount > 1));
            if (!bind) { substitutions[sym] = text; return; }
            if (!needsStatements && statement == null)
                throw new ServiceException("the call is inside an expression that cannot take an extra statement (lambda or comprehension).");
            string name = Unique(preferred, taken);
            taken.Add(name);
            bindings.Add((name, text));
            renames[sym] = name;
        }

        if (!needsStatements)
        {
            // an expression body: any extra statement goes in front of the statement that contains the call
            if (statement == null)
            {
                for (var p = callModule.ParentOf(call); p != null; p = callModule.ParentOf(p)) if (p is Stmt s) { statement = s; break; }
            }
        }

        foreach (var p in fn.Params)
        {
            var sym = defScope.Symbols[p.Name];
            var v = values[p.Name];
            Decide(sym, p.Name, v.Text, v.Node, v.Pure, v.Atom);
        }
        if (receiverName != null && receiverText != null && defScope.Symbols.TryGetValue(receiverName, out var selfSym))
            Decide(selfSym, "receiver", receiverText, receiverExpr, receiverExpr == null || IsPure(receiverExpr), receiverExpr != null && IsAtom(receiverExpr));
        else if (receiverName != null && receiverText == null && defScope.Symbols.TryGetValue(receiverName, out var s2) && s2.References.Count > 0 && !fn.IsStatic)
            throw new ServiceException("the body uses the receiver but the call has none.");

        // local variables of the body must not collide with names of the caller
        foreach (var local in defScope.Symbols.Values.Where(s => s.Redirect == null && s.Declarations.Count > 0 && s.Declarations.All(d => d.Kind != DeclKind.Parameter)))
        {
            if (taken.Contains(local.Name) || (callScope != null && callScope.Symbols.TryGetValue(local.Name, out var clash) && clash.Declarations.Count > 0))
            {
                string name = Unique(local.Name, taken);
                taken.Add(name);
                renames[local] = name;
            }
            else taken.Add(local.Name);
        }

        // ---- text of the inlined code
        var replaceEdits = new List<(int Start, int End, string Text)>();
        foreach (var (sym, text) in substitutions)
        {
            foreach (var r in sym.References)
            {
                var value = ValueNodeFor(sym, fn, values, receiverName, receiverExpr);
                string wrapped = value == null ? (text.Contains(' ') || text.Contains('\n') ? "(" + text + ")" : text) : WrapForContext(defModule, value, text, r.Node);
                replaceEdits.Add((r.Node.Start, r.Node.End, wrapped));
            }
        }
        foreach (var (sym, name) in renames)
            foreach (var r in sym.References) replaceEdits.Add((r.Node.Start, r.Node.End, name));
        foreach (var (sym, name) in renames.Where(x => x.Key.Declarations.Any(d => d.Kind != DeclKind.Parameter)))
            foreach (var d in sym.Declarations)
                if (d.Node is NameExpr dn && !replaceEdits.Any(e => e.Start == dn.Start)) replaceEdits.Add((dn.Start, dn.End, name));

        string Apply(int s, int e)
        {
            var sb = new StringBuilder();
            int pos = s;
            foreach (var ed in replaceEdits.Where(x => x.Start >= s && x.End <= e).OrderBy(x => x.Start))
            {
                if (ed.Start < pos) continue;
                sb.Append(defModule.Source, pos, ed.Start - pos).Append(ed.Text);
                pos = ed.End;
            }
            sb.Append(defModule.Source, pos, e - pos);
            return sb.ToString();
        }

        string? returnText = shape.ReturnValue != null ? Apply(shape.ReturnValue.Start, shape.ReturnValue.End) : null;
        bool returnPure = shape.ReturnValue == null || IsPure(shape.ReturnValue);

        var edits = new List<Edit>();
        if (needsStatements)
        {
            var stmt = statement!;
            if (!StartsLine(callModule.Source, stmt.Start)) throw new ServiceException("the statement that contains the call shares its line with other code.");
            string indent = callModule.Source[LineStart(callModule.Source, stmt.Start)..stmt.Start];
            var lines = new List<string>();
            foreach (var (name, text) in bindings) lines.Add(name + " = " + text);

            var first = shape.Statements[0];
            var lastStmt = shape.Statements[^1];
            string bodyText = DedentWithEdits(defModule, first.Start, TrueEnd(defModule.Source, lastStmt), LineIndent(defModule.Source, first.Start), indent, shape.Statements.Cast<Node>(), replaceEdits);
            lines.Add(bodyText);

            string? last = stmt switch
            {
                ExprStmt => returnText != null && !returnPure ? returnText : null,
                ReturnStmt => "return " + (returnText ?? "None"),
                AssignStmt asg => callModule.Source[asg.Targets[0].Start..asg.Targets[0].End] + " = " + (returnText ?? "None"),
                _ => null,
            };
            if (last != null) lines.Add(last);
            edits.Add(new Edit(stmt.Start, TrueEnd(callModule.Source, stmt), string.Join(NL + indent, lines.SelectMany(l => new[] { l }))));
        }
        else
        {
            string expression = returnText ?? "None";
            if (bindings.Count > 0)
            {
                var stmt = statement ?? throw new ServiceException("the call is not inside a statement.");
                HoistCheck(callModule, call, stmt);
                int lineStart = LineStart(callModule.Source, stmt.Start);
                string indent = callModule.Source[lineStart..stmt.Start];
                edits.Add(new Edit(lineStart, lineStart, string.Concat(bindings.Select(b => indent + b.Name + " = " + b.Text + NL))));
            }
            var returnNode = shape.ReturnValue;
            string wrapped = returnNode == null ? expression : WrapForContext(callModule, returnNode, expression, call);
            edits.Add(new Edit(call.Start, call.End, wrapped));
        }
        return edits;
    }

    /// <summary>The syntax node of the value for a parameter or the receiver (for its shape), when the call gave one.</summary>
    Expr? ValueNodeFor(Symbol sym, FunctionTarget fn, Dictionary<string, (string Text, Expr? Node, bool Pure, bool Atom)> values, string? receiverName, Expr? receiverExpr)
    {
        if (receiverName != null && sym.Name == receiverName) return receiverExpr;
        return values.TryGetValue(sym.Name, out var v) ? v.Node : null;
    }

    /// <summary>The argument expression the call gives for a parameter.</summary>
    static Expr? FindArgument(CallSite site, FunctionTarget fn, int paramIndex)
    {
        var positional = fn.Params.Where(p => IsPositional(p.Category)).ToList();
        int next = 0;
        bool selfPending = site.ExplicitSelf;
        foreach (var arg in site.Call.Args)
        {
            if (arg.Name == null)
            {
                if (selfPending) { selfPending = false; continue; }
                if (next < positional.Count && positional[next++].Index == paramIndex) return arg.Value;
            }
            else if (fn.Params.FirstOrDefault(p => p.Name == arg.Name) is { } p && p.Index == paramIndex) return arg.Value;
        }
        return null;
    }

    /// <summary>The checks that make it safe to put a statement in front of the one that contains this expression.</summary>
    static void HoistCheck(ModuleInfo m, Expr expr, Stmt stmt)
    {
        Node child = expr;
        for (var p = m.ParentOf(expr); p != null && !ReferenceEquals(p, stmt); child = p, p = m.ParentOf(p))
            if (p is LambdaExpr or CompExpr or Comprehension or ExceptHandler or MatchCase)
                throw new ServiceException("the call is inside a lambda, comprehension or handler, where an extra statement cannot be placed.");
        if (stmt is WhileStmt w && ReferenceEquals(w.Test, child)) throw new ServiceException("the call is in a while condition, which runs again on every pass.");
        if (stmt is IfStmt && Regex.IsMatch(m.Source[stmt.Start..Math.Min(stmt.Start + 5, m.Source.Length)], @"^elif\b")) throw new ServiceException("the call is in an elif condition.");
        if (!StartsLine(m.Source, stmt.Start)) throw new ServiceException("the statement that contains the call shares its line with other code.");
    }
}
