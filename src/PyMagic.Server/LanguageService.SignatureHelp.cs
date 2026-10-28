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

    /// <summary>Parameter info for the call the cursor is in, or null when there is none or nothing is known about the callee.</summary>
    public SignatureHelp? SignatureHelp(string uri, Position pos)
    {
        if (!_docs.TryGetValue(uri, out var doc)) return null;
        string text = doc.Text;
        int offset = new LineMap(text).OffsetOf(pos);
        if (SiteAt(text, offset) == CursorSite.Nothing) return null;
        if (OpenCallAt(text, offset) is not { } open) return null;

        // what is written so far rarely parses: analyse a copy where the unfinished argument is a name and the brackets are closed
        var (ws, m) = PatchedModule(doc, offset, offset);
        CallExpr? call = null;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is not CallExpr c) continue;
            int p = c.Func.End;
            while (p < m.Source.Length && char.IsWhiteSpace(m.Source[p])) p++;
            if (p == open.Paren && (call == null || c.Start >= call.Start)) call = c;
        }
        if (call == null) return null;

        var ev = ws.Evaluator;
        var sigs = ev.CallSignatures(ev.TypeOf(m, call.Func));
        if (sigs.Count == 0) return null;
        string name = call.Func switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => SourceText(m, call.Func) };

        var infos = sigs.Select(s => DescribeCall(ev, name, s)).ToList();
        int chosen = Enumerable.Range(0, sigs.Count).FirstOrDefault(i => Accepts(sigs[i].Params, open), 0);
        return new SignatureHelp(infos.Select(i => i.Info).ToList(), chosen, infos[chosen].Index(open));
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

    static Described DescribeCall(TypeEvaluator ev, string name, CallSignature s)
    {
        var sb = new StringBuilder(name).Append('(');
        var labels = new List<int[]>();
        bool first = true;
        foreach (var (text, param) in ParamParts(ev, s.Params))
        {
            if (!first) sb.Append(", ");
            first = false;
            int from = sb.Length;
            sb.Append(text);
            if (param != null) labels.Add(new[] { from, sb.Length });
        }
        sb.Append(')');
        if (s.Returns is { } r && r is not UnknownType { Explicit: false }) sb.Append(" -> ").Append(FormatType(ev, r));
        return new Described(new SignatureInformation(sb.ToString(), labels.Select(l => new ParameterInformation(l)).ToList()), s.Params);
    }
}
