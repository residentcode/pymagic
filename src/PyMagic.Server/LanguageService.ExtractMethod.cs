using System.Text;
using System.Text.RegularExpressions;
using PyModule = PyMagic.Core.Syntax.Module;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

public sealed partial class LanguageService
{
    /// <summary>Descendants without entering nested defs, classes and lambdas (their return, yield and loops are their own).</summary>
    static IEnumerable<Node> Shallow(IEnumerable<Node> roots)
    {
        var stack = new Stack<Node>(roots.Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n is FunctionDef or ClassDef or LambdaExpr) continue;
            foreach (var c in AstChildren.Of(n).Reverse()) stack.Push(c);
        }
    }

    List<Edit> ExtractMethod(ModuleInfo m, Selection sel, string? requestedName)
    {
        bool statements = sel.Statements != null;
        Node first = statements ? sel.Statements![0] : sel.Expression!;
        Node last = statements ? sel.Statements![^1] : sel.Expression!;
        int s = statements ? first.Start : sel.Start;
        int e = statements ? TrueEnd(m.Source, last) : sel.End;
        string text = m.Source;
        string nl = NewLine(m);
        var roots = statements ? sel.Statements!.Cast<Node>().ToList() : new List<Node> { sel.Expression! };
        bool Inside(Node n) => n.Start >= s && n.End <= e;

        // ---- where the code lives
        var ownerNode = EnclosingScopeNode(m, first);
        if (ownerNode is ClassDef) throw new ServiceException("Code in a class body cannot be extracted into a method.");
        if (ownerNode is LambdaExpr or CompExpr) throw new ServiceException("Code inside a lambda or comprehension cannot be extracted.");
        var owner = ownerNode as FunctionDef;
        var scope = m.Semantics.ScopeOf(ownerNode) ?? throw new ServiceException("Cannot analyse the surrounding scope.");
        bool isModule = owner == null;
        if (statements && !StartsLine(text, s)) throw new ServiceException("The selection starts in the middle of a line.");
        if (!statements && IsAssignmentTarget(m, sel.Expression!)) throw new ServiceException("An assignment target cannot be extracted.");

        // ---- control flow that cannot move into another function
        bool needsAsync = false;
        foreach (var n in Shallow(roots))
        {
            switch (n)
            {
                case ReturnStmt: throw new ServiceException("The selection contains 'return': extracting it would change what the function returns.");
                case YieldExpr: throw new ServiceException("The selection contains 'yield'.");
                case GlobalStmt or NonlocalStmt: throw new ServiceException("The selection contains a 'global' or 'nonlocal' declaration.");
                case AwaitExpr or ForStmt { IsAsync: true } or WithStmt { IsAsync: true } or Comprehension { IsAsync: true }: needsAsync = true; break;
                case NamedExpr when !statements: throw new ServiceException("An assignment expression cannot be extracted.");
                case BreakStmt or ContinueStmt:
                    if (!LoopInside(m, n, s, e)) throw new ServiceException("'break' or 'continue' belongs to a loop that is not part of the selection.");
                    break;
            }
        }
        if (needsAsync && owner is not { IsAsync: true }) throw new ServiceException("The selection awaits, but the surrounding code is not an async function.");

        // ---- names: what is read from outside (parameters) and what is assigned and used afterwards (results)
        var nameNodes = roots.SelectMany(AstChildren.Descendants).OfType<NameExpr>().OrderBy(n => n.Start).ToList();
        string? receiver = null;
        bool isStatic = false, isClassMethod = false;
        ClassDef? ownerClass = owner != null ? m.ParentOf(owner) as ClassDef : null;
        if (owner != null && ownerClass != null)
        {
            isStatic = owner.Decorators.Any(d => d is NameExpr { Name: "staticmethod" });
            isClassMethod = owner.Decorators.Any(d => d is NameExpr { Name: "classmethod" });
            var firstParam = owner.Params.FirstOrDefault(p => p.Kind == ParamKind.Normal);
            if (!isStatic && firstParam != null) receiver = firstParam.Name;
            else if (!isStatic) isStatic = true;
        }

        var paramSymbols = new List<Symbol>();
        foreach (var n in nameNodes)
        {
            var r = m.Semantics.ReferenceOf(n);
            if (r?.Symbol == null) continue;
            var sym = Canon(r.Symbol);
            if (sym.Scope != scope) continue;
            if (!r.Access.HasFlag(Access.Read) || paramSymbols.Contains(sym) || sym.Name == receiver) continue;

            bool outside;
            var rr = r.UseScope == scope ? m.Flow.ReachingDefinitions(n) : null;
            if (rr != null) outside = rr.Definitions.Any(d => !Inside(d.Node));
            else outside = sym.Declarations.Any(d => !Inside(d.Node));
            if (!outside) continue;
            // module-level names stay reachable from a function; only ones assigned in the selection would turn into locals
            if (isModule && !sym.Declarations.Any(d => Inside(d.Node))) continue;
            paramSymbols.Add(sym);
        }

        // assignments to names of outer scopes (global / nonlocal) cannot be moved
        foreach (var n in nameNodes)
        {
            var r = m.Semantics.ReferenceOf(n);
            if (r?.Symbol == null || !r.Access.HasFlag(Access.Write)) continue;
            var sym = Canon(r.Symbol);
            bool nested = sym.Scope.Node.Start >= s && sym.Scope.Node.End <= e && !ReferenceEquals(sym.Scope, scope);
            if (sym.Scope != scope && !nested) throw new ServiceException($"'{n.Name}' belongs to an outer scope and is assigned in the selection.");
        }

        // names used inside f-strings are not references, so look at the text: reading a name there makes it a parameter too
        var wordPattern = new Regex(@"(?<![\w.])[A-Za-z_]\w*");
        foreach (var w in FStrings(roots).SelectMany(c => wordPattern.Matches(c.Text).Select(x => x.Value)).Distinct())
        {
            if (!scope.Symbols.TryGetValue(w, out var fsym) || fsym.Redirect != null || fsym.Declarations.Count == 0 || fsym.Name == receiver || paramSymbols.Contains(fsym)) continue;
            if (!fsym.Declarations.Any(d => !Inside(d.Node))) continue;
            if (isModule && !fsym.Declarations.Any(d => Inside(d.Node))) continue;
            paramSymbols.Add(fsym);
        }

        var results = new List<Symbol>();
        if (statements)
        {
            foreach (var sym in scope.Symbols.Values.Where(x => x.Redirect == null).OrderBy(x => x.Declarations.Where(d => Inside(d.Node)).Select(d => d.Node.Start).DefaultIfEmpty(int.MaxValue).Min()))
            {
                var inside = sym.Declarations.Where(d => Inside(d.Node)).ToList();
                if (inside.Count == 0 || inside.All(d => d.Kind is DeclKind.ExceptionVar or DeclKind.Parameter)) continue;
                bool live = false;
                foreach (var r in sym.References)
                {
                    if (!r.Access.HasFlag(Access.Read) || Inside(r.Node)) continue;
                    if (r.UseScope == scope)
                    {
                        var rr = m.Flow.ReachingDefinitions(r.Node);
                        if (rr == null || rr.Definitions.Any(d => Inside(d.Node))) live = true;
                    }
                    else live = true;   // read from a nested function: assume it needs the value
                    if (live) break;
                }
                if (!live)
                {
                    var word = new Regex(@"(?<![\w.])" + Regex.Escape(sym.Name) + @"(?!\w)");
                    live = FStrings(new[] { ownerNode }).Any(c => !Inside(c) && word.IsMatch(c.Text));
                }
                if (live) results.Add(sym);
            }
        }

        // ---- the new function's name and where it goes
        string name = requestedName ?? "";
        if (!Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}_]*$") || Lexer.Keywords.Contains(name)) throw new ServiceException($"'{name}' is not a valid name");
        Scope? homeScope = ownerClass != null ? m.Semantics.ScopeOf(ownerClass) : isModule ? scope : scope.Parent;
        if (homeScope != null && homeScope.Symbols.TryGetValue(name, out var existing) && existing.Declarations.Count > 0)
            throw new ServiceException($"'{name}' is already defined here");

        // ---- text
        string defIndent = owner != null ? LineIndent(text, owner.Start) : "";
        string bodyIndent = defIndent + "    ";
        string paramList = string.Join(", ", paramSymbols.Select(p => p.Name));
        string callArgs = paramList;
        string head = (receiver != null && !isStatic ? receiver + (paramList.Length > 0 ? ", " : "") : "") + paramList;
        string callee = ownerClass == null ? name : isStatic ? ownerClass.Name + "." + name : receiver + "." + name;
        string call = (needsAsync ? "await " : "") + callee + "(" + callArgs + ")";

        string body;
        if (statements)
        {
            body = bodyIndent + Dedent(m, s, e, LineIndent(text, s), bodyIndent, roots);
            if (results.Count > 0) body += nl + bodyIndent + "return " + string.Join(", ", results.Select(r => r.Name));
        }
        else body = bodyIndent + "return " + AsStatementValue(m, sel.Expression!);

        string decorator = ownerClass != null && isStatic ? defIndent + "@staticmethod" + nl : ownerClass != null && isClassMethod ? defIndent + "@classmethod" + nl : "";
        string def = decorator + defIndent + (needsAsync ? "async " : "") + "def " + name + "(" + head + "):" + nl + body;

        var edits = new List<Edit>();
        string replacement = statements && results.Count > 0 ? string.Join(", ", results.Select(r => r.Name)) + " = " + call : call;
        edits.Add(new Edit(s, e, replacement));

        if (owner != null)
        {
            int at = TrueEnd(text, owner);
            while (at < text.Length && text[at] != '\n' && text[at] != '\r') at++;
            string gap = defIndent.Length == 0 ? nl + nl + nl : nl + nl;
            edits.Add(new Edit(at, at, gap + def));
        }
        else
        {
            Node top = first;
            for (var p = m.ParentOf(top); p != null && p is not PyModule; p = m.ParentOf(top)) top = p;
            int at = LineStart(text, top.Start);
            bool afterCode = at > 0 && text[LineStart(text, at - 1)..at].Trim().Length > 0;
            edits.Add(new Edit(at, at, (afterCode ? nl + nl : "") + def + nl + nl + nl));
        }
        return edits;
    }

    /// <summary>True when the break/continue sits in a loop that lies wholly inside the selection.</summary>
    static bool LoopInside(ModuleInfo m, Node jump, int s, int e)
    {
        Node child = jump;
        for (var p = m.ParentOf(jump); p != null; child = p, p = m.ParentOf(p))
        {
            if (p is FunctionDef or ClassDef) return false;
            bool inBody = p switch
            {
                ForStmt f => f.Body.Any(b => ReferenceEquals(b, child)),
                WhileStmt w => w.Body.Any(b => ReferenceEquals(b, child)),
                _ => false,
            };
            if (inBody) return p.Start >= s && p.End <= e;
        }
        return false;
    }

    /// <summary>
    /// The selected statements' text moved to a new indentation. Lines inside multi-line string literals are left alone
    /// (changing them would change the string).
    /// </summary>
    static string Dedent(ModuleInfo m, int s, int e, string baseIndent, string newIndent, IEnumerable<Node> roots) =>
        DedentWithEdits(m, s, e, baseIndent, newIndent, roots, null);

    /// <summary>
    /// <see cref="Dedent"/> with replacements applied on the way: each replacement lies within one line, so they are applied
    /// to the line before it is re-indented.
    /// </summary>
    static string DedentWithEdits(ModuleInfo m, int s, int e, string baseIndent, string newIndent, IEnumerable<Node> roots, List<(int Start, int End, string Text)>? replacements)
    {
        string text = m.Source;
        var rawLineStarts = new HashSet<int>();
        foreach (var c in roots.SelectMany(AstChildren.Descendants).OfType<ConstExpr>())
        {
            if (c.Kind is not (ConstKind.Str or ConstKind.Bytes)) continue;
            for (int i = c.Start; i < c.End && i < text.Length; i++)
                if (text[i] == '\n') rawLineStarts.Add(i + 1);
        }

        string WithReplacements(int from, int to)
        {
            if (replacements == null) return text[from..to];
            var sb2 = new StringBuilder();
            int p = from;
            foreach (var r in replacements.Where(x => x.Start >= from && x.End <= to).OrderBy(x => x.Start))
            {
                if (r.Start < p) continue;
                sb2.Append(text, p, r.Start - p).Append(r.Text);
                p = r.End;
            }
            sb2.Append(text, p, to - p);
            return sb2.ToString();
        }

        var sb = new StringBuilder();
        int pos = s;
        bool firstLine = true;
        while (pos <= e)
        {
            int nlPos = text.IndexOf('\n', pos);
            int lineEnd = nlPos < 0 || nlPos >= e ? e : nlPos;
            bool hasCr = lineEnd > pos && text[lineEnd - 1] == '\r';
            string core = WithReplacements(pos, hasCr ? lineEnd - 1 : lineEnd);
            if (firstLine) sb.Append(core);
            else if (rawLineStarts.Contains(pos)) sb.Append(core);
            else if (core.Trim().Length == 0) { /* blank line stays blank */ }
            else if (core.StartsWith(baseIndent, StringComparison.Ordinal)) sb.Append(newIndent).Append(core[baseIndent.Length..]);
            else sb.Append(newIndent).Append(core.TrimStart());
            if (lineEnd >= e) break;
            sb.Append(hasCr ? "\r\n" : "\n");
            pos = lineEnd + 1;
            firstLine = false;
        }
        return sb.ToString();
    }
}
