using System.Text;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Parameter info while a call is typed (`textDocument/signatureHelp`): what the callee takes and which parameter the cursor is on.</summary>
public sealed partial class LanguageService
{
    /// <summary>The call the cursor is inside: where its `(` is, how many commas came before the cursor, and the keyword when the argument is `name=...`.</summary>
    sealed record OpenCall(int Paren, int Commas, string? Keyword);

    sealed class Frame(char open, int at, bool call)
    {
        public char Open = open;
        public int At = at;
        public bool Call = call;
        public int Commas;
        public int ArgumentStart; // index of the first token of the argument being written
    }

    /// <summary>The innermost call that is open at <paramref name="offset"/>: a `(` that follows a name, `)` or `]`, not a grouping like `(a + b)`.</summary>
    static OpenCall? OpenCallAt(string text, int offset)
    {
        var tokens = Lexer.Tokenize(text[..offset]).Where(t => t.Kind is not (TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent or TokenKind.EndOfFile or TokenKind.Comment)).ToList();
        var frames = new List<Frame>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Operator) continue;
            string op = text.Substring(t.Start, t.Length);
            switch (op)
            {
                case "(" or "[" or "{":
                {
                    bool call = op == "(" && i > 0 && (tokens[i - 1].Kind == TokenKind.Name || tokens[i - 1].Kind == TokenKind.Operator && text[tokens[i - 1].Start] is ')' or ']' && tokens[i - 1].Length == 1);
                    frames.Add(new Frame(op[0], t.Start, call) { ArgumentStart = i + 1 });
                    break;
                }
                case ")" or "]" or "}":
                    if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
                    break;
                case "," when frames.Count > 0:
                    frames[^1].Commas++;
                    frames[^1].ArgumentStart = i + 1;
                    break;
            }
        }
        var call2 = frames.LastOrDefault(f => f.Call);
        if (call2 == null) return null;
        // everything between the call's own brackets: a comma inside a nested (...) or [...] belongs to that one, which is why the frames are separate
        string? keyword = null;
        int a = call2.ArgumentStart;
        if (a + 1 < tokens.Count && tokens[a].Kind == TokenKind.Name && tokens[a + 1].Kind == TokenKind.Operator && text.Substring(tokens[a + 1].Start, tokens[a + 1].Length) == "=" && tokens[a].Start > call2.At)
            keyword = text.Substring(tokens[a].Start, tokens[a].Length);
        return new OpenCall(call2.At, call2.Commas, keyword);
    }

    /// <summary>
    /// The open call: its callee (null for a class's bases, `class A(Base, |`, whose keywords go to `__init_subclass__`), the arguments written,
    /// what the callee takes, and the class being called when it is one (its docstring counts).
    /// </summary>
    sealed record CallContext(TypeEvaluator Ev, ModuleInfo Module, Expr? Func, IReadOnlyList<Argument> Args, IReadOnlyList<CallSignature> Sigs, string Name, OpenCall Open, ClassInfo? Class);

    /// <summary>
    /// The call that is open where text[start..end) is being typed, with what its callee takes; null when the cursor is in no call or nothing is known
    /// about the callee. What is written so far rarely parses, so a copy is analysed where that text is a name and the brackets are closed.
    /// </summary>
    CallContext? CallAt(Doc doc, int start, int end)
    {
        string text = doc.Text;
        if (SiteAt(text, end) == CursorSite.Nothing) return null;
        if (OpenCallAt(text, start) is not { } open) return null;
        var (ws, m) = PatchedModule(doc, start, end);
        CallExpr? call = null;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not CallExpr c) continue;
            int p = c.Func.End;
            while (p < m.Source.Length && char.IsWhiteSpace(m.Source[p])) p++;
            if (p == open.Paren && (call == null || c.Start >= call.Start)) call = c;
        }
        var ev = ws.Evaluator;
        if (call == null) return ClassHeaderAt(ev, m, open);
        var calleeType = ev.TypeOf(m, call.Func);
        var sigs = ev.CallSignatures(calleeType);
        if (sigs.Count == 0) return null;
        string name = call.Func switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => SourceText(m, call.Func) };
        return new CallContext(ev, m, call.Func, call.Args, sigs, name, open, calleeType is ClassType { IsInstance: false } cls ? cls.Info : null);
    }

    /// <summary>
    /// The bases of a class being written (`class A(Base, |`): any number of bases, `metaclass=`, and the keywords the bases' `__init_subclass__`
    /// takes (`class A(Plugin, tag='x')`); null when the bracket is not a class's.
    /// </summary>
    static CallContext? ClassHeaderAt(TypeEvaluator ev, ModuleInfo m, OpenCall open)
    {
        var cd = AstChildren.Descendants(m.Ast).OfType<ClassDef>().FirstOrDefault(c =>
        {
            int p = c.NameStart + c.Name.Length;
            if (p < m.Source.Length && m.Source[p] == '[') p = m.Source.IndexOf(']', p) + 1;   // class A[T](...)
            while (p > 0 && p < m.Source.Length && char.IsWhiteSpace(m.Source[p])) p++;
            return p == open.Paren;
        });
        if (cd == null) return null;
        var unknown = new UnknownType(false, false);
        var ps = new List<ParamInfo> { new("bases", unknown, ParamCategory.VarPositional, false), new("metaclass", unknown, ParamCategory.KeywordOnly, true) };
        FunctionInfo? hook = null;
        foreach (var b in cd.Bases.Where(a => a.Name == null && a.Stars == 0))
        {
            if (ev.TypeOf(m, b.Value) is not ClassType { IsInstance: false } bc) continue;
            foreach (var t in ev.MroOf(bc.Info))
            {
                if (t is not ClassType { Info: var c } || c.Name == "object" && c.Module.Name == "builtins") continue;
                if (c.Scope.Symbols.GetValueOrDefault("__init_subclass__") is not { } sym) continue;
                hook = ev.TypeOfSymbol(sym) switch { FunctionType f => f.Info, SignatureType { Origin: { } o } => o.Info, _ => null };
                break;
            }
            if (hook != null) break;
        }
        if (hook != null)
            foreach (var p in ev.ParametersOf(hook).Skip(1))   // cls
                ps.Add(p.Category is ParamCategory.Normal or ParamCategory.PositionalOnly ? p with { Category = ParamCategory.KeywordOnly } : p);
        return new CallContext(ev, m, null, cd.Bases, new[] { new CallSignature(ps, null, hook) }, "class " + cd.Name, open, null);
    }

    /// <summary>Parameter info for the call the cursor is in, or null when there is none or nothing is known about the callee.</summary>
    public SignatureHelp? SignatureHelp(string uri, Position pos)
    {
        if (!_docs.TryGetValue(uri, out var doc)) return null;
        int offset = new LineMap(doc.Text).OffsetOf(pos);
        if (CallAt(doc, offset, offset) is not { } cx) return null;
        // calling a class: its docstring, and what it says about the parameters, count as much as __init__'s
        var infos = cx.Sigs.Select(s => DescribeCall(cx.Ev, cx.Name, s, cx.Class)).ToList();
        int chosen = Enumerable.Range(0, cx.Sigs.Count).FirstOrDefault(i => Accepts(cx.Sigs[i].Params, cx.Open), 0);
        return new SignatureHelp(infos.Select(i => i.Info).ToList(), chosen, infos[chosen].Index(cx.Open));
    }

    /// <summary>
    /// `name=` for each parameter the call can still be given by keyword, where an argument begins (after the bracket or a comma): not those that are
    /// filled by the arguments written before, not those already given, not positional-only ones.
    /// </summary>
    IReadOnlyList<CompletionItem> KeywordArgumentItems(Doc doc, int start, int end, string prefix)
    {
        string text = doc.Text;
        int i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0 || text[i] is not ('(' or ',') || CallAt(doc, start, end) is not { } cx) return Array.Empty<CompletionItem>();
        var given = cx.Args.Where(a => a.Name != null).Select(a => a.Name!).ToHashSet();
        int positional = cx.Args.Count(a => a.Name == null && a.Stars == 0 && a.Value is not NameExpr { Name: CursorMark });
        var found = new Dictionary<string, (PyType Type, CallSignature Sig)>();
        foreach (var sig in cx.Sigs)
        {
            var byPosition = sig.Params.Where(p => p.Category is ParamCategory.PositionalOnly or ParamCategory.Normal).ToList();
            if (positional > byPosition.Count && !sig.Params.Any(p => p.Category == ParamCategory.VarPositional)) continue;   // this overload takes fewer arguments than are written
            foreach (var p in sig.Params)
            {
                if (p.Category is not (ParamCategory.Normal or ParamCategory.KeywordOnly) || given.Contains(p.Name) || !p.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (p.Category == ParamCategory.Normal && byPosition.IndexOf(p) < positional) continue;
                found.TryAdd(p.Name, (p.Type, sig));
            }
        }
        // in a class's bases the base names come first: the keywords are rarer there
        bool bases = cx.Func == null;
        return found.Select(kv => new CompletionItem(kv.Key + "=", CompletionKind.Variable,
            kv.Value.Type is UnknownType { Explicit: false } ? null : FormatType(cx.Ev, kv.Value.Type), SortText: (bases ? "zz" : "00") + kv.Key.ToLowerInvariant(),
            InsertText: kv.Key + "=", FilterText: kv.Key, Documentation: ParameterDoc(cx.Ev, kv.Value.Sig.Function, cx.Class, kv.Key) is { } d ? DocMarkdown(d) : null)).ToList();
    }

    /// <summary>Whether a signature could take the argument the cursor is on (a keyword it has or a position it reaches), to pick among overloads.</summary>
    static bool Accepts(IReadOnlyList<ParamInfo> ps, OpenCall open)
    {
        if (open.Keyword != null) return ps.Any(p => p.Name == open.Keyword && p.Category != ParamCategory.PositionalOnly || p.Category == ParamCategory.VarKeyword);
        int positional = ps.Count(p => p.Category is ParamCategory.PositionalOnly or ParamCategory.Normal);
        return open.Commas < positional || ps.Any(p => p.Category == ParamCategory.VarPositional);
    }

    sealed record Described(SignatureInformation Info, IReadOnlyList<ParamInfo> Params)
    {
        /// <summary>The parameter the cursor is on: by keyword, by position, or past the end (nothing is lit).</summary>
        public int Index(OpenCall open)
        {
            if (open.Keyword != null)
            {
                int k = Params.ToList().FindIndex(p => p.Name == open.Keyword && p.Category != ParamCategory.PositionalOnly);
                if (k >= 0) return k;
                int rest = Params.ToList().FindIndex(p => p.Category == ParamCategory.VarKeyword);
                return rest >= 0 ? rest : Params.Count;
            }
            int seen = 0;
            for (int i = 0; i < Params.Count; i++)
            {
                if (Params[i].Category == ParamCategory.VarPositional) return i;
                if (Params[i].Category is ParamCategory.PositionalOnly or ParamCategory.Normal && seen++ == open.Commas) return i;
            }
            return Params.Count;
        }
    }

    Described DescribeCall(TypeEvaluator ev, string name, CallSignature s, ClassInfo? owner)
    {
        // a class has no def of its own to point at: its own __init__ is the one whose docstring counts
        string? functionDoc = s.Function is { } fn ? DocOf(ev, fn.Module, fn.Node, fn.Owner)
            : owner?.Node.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__") is { } init ? DocOf(ev, owner.Module, init, owner) : null;
        string? classDoc = owner != null ? DocOf(ev, owner.Module, owner.Node, null) : null;
        var parameterDocs = Docstrings.Parameters(functionDoc);
        var classParameterDocs = Docstrings.Parameters(classDoc);
        var sb = new StringBuilder(name).Append('(');
        var labels = new List<ParameterInformation>();
        // a parameter's place in the label counts what positions count: UTF-8 bytes for a client that chose them
        int At() => LineMap.Utf8.Value ? Encoding.UTF8.GetByteCount(sb.ToString()) : sb.Length;
        bool first = true;
        foreach (var (text, param) in ParamParts(ev, s.Params))
        {
            if (!first) sb.Append(", ");
            first = false;
            int from = At();
            sb.Append(text);
            if (param != null) labels.Add(new ParameterInformation(new[] { from, At() }, parameterDocs.GetValueOrDefault(param.Name) ?? classParameterDocs.GetValueOrDefault(param.Name)));
        }
        sb.Append(')');
        if (s.Returns is { } r && r is not UnknownType { Explicit: false }) sb.Append(" -> ").Append(FormatType(ev, r));
        string? doc = owner != null ? Docstrings.Summary(classDoc) ?? Docstrings.Summary(functionDoc) : Docstrings.Summary(functionDoc);
        return new Described(new SignatureInformation(sb.ToString(), labels, doc), s.Params);
    }
}
