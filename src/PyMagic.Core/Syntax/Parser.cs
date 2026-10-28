namespace PyMagic.Core.Syntax;

/// <summary>
/// Recursive-descent parser for Python 3.13 syntax. Never throws: errors are collected as diagnostics,
/// the offending logical line is skipped, and parsing resumes at the next statement.
/// Soft keywords (match/case/type) are resolved speculatively with rollback.
/// </summary>
public sealed class Parser
{
    sealed class SyntaxError(int pos, string msg) : Exception(msg) { public int Pos { get; } = pos; }

    readonly string _src;
    readonly List<Token> _toks;
    readonly List<Diagnostic> _diags = new();
    int _i;
    int _prevEnd;
    bool _sawWalrus, _sawScopeDeclaration;

    Parser(string src, List<Token> toks) { _src = src; _toks = toks; }

    public static ParseResult Parse(string source)
    {
        var all = Lexer.Tokenize(source);
        var toks = new List<Token>(all.Count);
        foreach (var t in all)
            if (t.Kind != TokenKind.Comment) toks.Add(t);
        var p = new Parser(source, toks);
        var module = p.ParseModule();
        return new ParseResult(module, p._diags, all);
    }

    // ------------------------------------------------------------ token helpers

    Token Cur => _toks[_i];
    Token PeekAt(int n) => _toks[Math.Min(_i + n, _toks.Count - 1)];
    /// <summary>The text of a token. Names, keywords and operators are shared strings (the same word is one string in every module), so reading a file does not allocate one per use.</summary>
    string Txt(Token t) => t.Kind is TokenKind.Name or TokenKind.Keyword or TokenKind.Operator
        ? WordCache.Get(_src.AsSpan(t.Start, t.Length))
        : _src.Substring(t.Start, t.Length);
    bool AtEnd => Cur.Kind == TokenKind.EndOfFile;

    Token Advance()
    {
        var t = _toks[_i];
        if (t.Kind != TokenKind.EndOfFile) _i++;
        if (t.Length > 0) _prevEnd = t.End;
        return t;
    }

    bool IsOp(string op) => Cur.Kind == TokenKind.Operator && Cur.Length == op.Length && string.CompareOrdinal(_src, Cur.Start, op, 0, op.Length) == 0;
    bool IsOpAt(int n, string op) { var t = PeekAt(n); return t.Kind == TokenKind.Operator && t.Length == op.Length && string.CompareOrdinal(_src, t.Start, op, 0, op.Length) == 0; }
    bool IsKw(string kw) => Cur.Kind == TokenKind.Keyword && Cur.Length == kw.Length && string.CompareOrdinal(_src, Cur.Start, kw, 0, kw.Length) == 0;
    bool IsKwAt(int n, string kw) { var t = PeekAt(n); return t.Kind == TokenKind.Keyword && t.Length == kw.Length && string.CompareOrdinal(_src, t.Start, kw, 0, kw.Length) == 0; }
    bool IsSoft(string word) => Cur.Kind == TokenKind.Name && Cur.Length == word.Length && string.CompareOrdinal(_src, Cur.Start, word, 0, word.Length) == 0;

    bool AcceptOp(string op) { if (!IsOp(op)) return false; Advance(); return true; }
    bool AcceptKw(string kw) { if (!IsKw(kw)) return false; Advance(); return true; }

    void ExpectOp(string op) { if (!AcceptOp(op)) throw Err($"expected '{op}'"); }
    void ExpectKw(string kw) { if (!AcceptKw(kw)) throw Err($"expected '{kw}'"); }

    string ExpectName()
    {
        if (Cur.Kind != TokenKind.Name) throw Err("expected identifier");
        return Txt(Advance());
    }

    SyntaxError Err(string msg) => new(Cur.Start, msg);

    /// <summary>Gives a node it has just created its place in the source (every caller passes a new node, so it is changed in place).</summary>
    T At<T>(T node, int start) where T : Node
    {
        node.Start = start;
        node.End = Math.Max(_prevEnd, start);
        return node;
    }

    void Report(SyntaxError e)
    {
        int len = Cur.Start == e.Pos ? Math.Max(Cur.Length, 1) : 1;
        _diags.Add(new Diagnostic(e.Pos, len, e.Message));
    }

    // ------------------------------------------------------------ module & blocks

    Module ParseModule()
    {
        var body = new List<Stmt>();
        while (!AtEnd)
        {
            if (Cur.Kind == TokenKind.Newline) { Advance(); continue; }
            ParseStatementInto(body);
        }
        return new Module(body) { Start = 0, End = _src.Length, HasWalrus = _sawWalrus, HasScopeDeclarations = _sawScopeDeclaration };
    }

    /// <summary>Parses one statement line (possibly several simple statements) with error recovery.</summary>
    void ParseStatementInto(List<Stmt> into)
    {
        int startIdx = _i;
        try { ParseStatement(into); }
        catch (SyntaxError e)
        {
            Report(e);
            Recover();
            if (_i == startIdx && !AtEnd) Advance(); // guarantee progress
        }
    }

    /// <summary>Skip to the end of the current logical line, including an indented block that follows it.</summary>
    void Recover()
    {
        int depth = 0;
        while (!AtEnd)
        {
            var k = Cur.Kind;
            if (k == TokenKind.Indent) depth++;
            else if (k == TokenKind.Dedent)
            {
                if (depth == 0) return; // belongs to the enclosing block
                depth--;
                Advance();
                if (depth == 0) return;
                continue;
            }
            else if (k == TokenKind.Newline)
            {
                Advance();
                if (depth == 0 && Cur.Kind != TokenKind.Indent) return;
                continue;
            }
            Advance();
        }
    }

    List<Stmt> ParseBlock()
    {
        ExpectOp(":");
        var body = new List<Stmt>();
        if (Cur.Kind != TokenKind.Newline)
        {
            ParseSimpleLine(body);
            return body;
        }
        Advance();
        if (Cur.Kind != TokenKind.Indent)
        {
            _diags.Add(new Diagnostic(Cur.Start, 1, "expected an indented block"));
            return body;
        }
        Advance();
        while (Cur.Kind != TokenKind.Dedent && !AtEnd)
        {
            if (Cur.Kind == TokenKind.Newline) { Advance(); continue; }
            ParseStatementInto(body);
        }
        if (Cur.Kind == TokenKind.Dedent) Advance();
        return body;
    }

    // ------------------------------------------------------------ statements

    void ParseStatement(List<Stmt> into)
    {
        if (Cur.Kind == TokenKind.Error)
        {
            var bad = Advance();
            _diags.Add(new Diagnostic(bad.Start, Math.Max(bad.Length, 1), "invalid token"));
            return;
        }
        if (Cur.Kind == TokenKind.Indent)
        {
            _diags.Add(new Diagnostic(Cur.Start, 1, "unexpected indent"));
            Advance();
            return;
        }
        if (Cur.Kind == TokenKind.Dedent) { Advance(); return; }

        int s = Cur.Start;
        if (Cur.Kind == TokenKind.Keyword)
        {
            switch (Txt(Cur))
            {
                case "if": into.Add(ParseIf()); return;
                case "while": into.Add(ParseWhile()); return;
                case "for": into.Add(ParseFor(false, s)); return;
                case "try": into.Add(ParseTry()); return;
                case "with": into.Add(ParseWith(false, s)); return;
                case "def": into.Add(ParseFuncDef(new List<Expr>(), false, s)); return;
                case "class": into.Add(ParseClassDef(new List<Expr>(), s)); return;
                case "async": into.Add(ParseAsync(new List<Expr>(), s)); return;
            }
        }
        else if (IsOp("@")) { into.Add(ParseDecorated()); return; }
        else if (IsSoft("match") && TryParseMatch(into)) return;
        else if (IsSoft("type") && PeekAt(1).Kind == TokenKind.Name && (IsOpAt(2, "=") || IsOpAt(2, "[")))
        {
            into.Add(ParseTypeAlias());
            return;
        }

        ParseSimpleLine(into);
    }

    /// <summary>simple_stmt (';' simple_stmt)* NEWLINE</summary>
    void ParseSimpleLine(List<Stmt> into)
    {
        while (true)
        {
            into.Add(ParseSmallStmt());
            if (AcceptOp(";"))
            {
                if (Cur.Kind == TokenKind.Newline || AtEnd) break;
                continue;
            }
            break;
        }
        if (Cur.Kind == TokenKind.Newline) Advance();
        else if (!AtEnd && Cur.Kind != TokenKind.Dedent) throw Err("invalid syntax");
    }

    Stmt ParseSmallStmt()
    {
        int s = Cur.Start;
        if (Cur.Kind == TokenKind.Keyword)
        {
            switch (Txt(Cur))
            {
                case "pass": Advance(); return At(new PassStmt(), s);
                case "break": Advance(); return At(new BreakStmt(), s);
                case "continue": Advance(); return At(new ContinueStmt(), s);
                case "return":
                {
                    Advance();
                    Expr? v = AtStmtEnd() ? null : ParseStarExpressions();
                    return At(new ReturnStmt(v), s);
                }
                case "raise":
                {
                    Advance();
                    Expr? exc = null, cause = null;
                    if (!AtStmtEnd())
                    {
                        exc = ParseTest();
                        if (AcceptKw("from")) cause = ParseTest();
                    }
                    return At(new RaiseStmt(exc, cause), s);
                }
                case "global": { Advance(); _sawScopeDeclaration = true; return At(new GlobalStmt(ParseNameList()), s); }
                case "nonlocal": { Advance(); _sawScopeDeclaration = true; return At(new NonlocalStmt(ParseNameList()), s); }
                case "del":
                {
                    Advance();
                    var targets = new List<Expr> { ParseStarTarget() };
                    while (AcceptOp(",")) { if (AtStmtEnd()) break; targets.Add(ParseStarTarget()); }
                    return At(new DelStmt(targets), s);
                }
                case "assert":
                {
                    Advance();
                    var t = ParseTest();
                    Expr? m = AcceptOp(",") ? ParseTest() : null;
                    return At(new AssertStmt(t, m), s);
                }
                case "import": return ParseImport();
                case "from": return ParseFromImport();
            }
        }
        return ParseExprStatement();
    }

    bool AtStmtEnd() => Cur.Kind is TokenKind.Newline or TokenKind.EndOfFile or TokenKind.Dedent || IsOp(";");

    List<string> ParseNameList()
    {
        var names = new List<string> { ExpectName() };
        while (AcceptOp(",")) names.Add(ExpectName());
        return names;
    }

    Stmt ParseExprStatement()
    {
        int s = Cur.Start;
        var first = ParseStarExpressionsOrYield();

        if (IsOp(":") )
        {
            Advance();
            var ann = ParseTest();
            Expr? val = null;
            if (AcceptOp("=")) val = ParseStarExpressionsOrYield();
            return At(new AnnAssignStmt(first, ann, val), s);
        }

        if (Cur.Kind == TokenKind.Operator && IsAugAssign(Txt(Cur), out var op))
        {
            Advance();
            var val = ParseStarExpressionsOrYield();
            return At(new AugAssignStmt(first, op, val), s);
        }

        if (IsOp("="))
        {
            var targets = new List<Expr> { first };
            Expr value;
            while (true)
            {
                Advance(); // '='
                value = ParseStarExpressionsOrYield();
                if (IsOp("=")) { targets.Add(value); continue; }
                break;
            }
            return At(new AssignStmt(targets, value), s);
        }

        return At(new ExprStmt(first), s);
    }

    static bool IsAugAssign(string t, out string op)
    {
        op = t;
        return t is "+=" or "-=" or "*=" or "/=" or "//=" or "%=" or "@=" or "&=" or "|=" or "^=" or ">>=" or "<<=" or "**=";
    }

    Stmt ParseImport()
    {
        int s = Cur.Start;
        Advance();
        var names = new List<ImportAlias>();
        do
        {
            int a = Cur.Start;
            string dotted = ParseDottedName();
            string? asName = AcceptKw("as") ? ExpectName() : null;
            names.Add(At(new ImportAlias(dotted, asName), a));
        } while (AcceptOp(","));
        return At(new ImportStmt(names), s);
    }

    string ParseDottedName()
    {
        var parts = new List<string> { ExpectName() };
        while (IsOp(".")) { Advance(); parts.Add(ExpectName()); }
        return string.Join(".", parts);
    }

    Stmt ParseFromImport()
    {
        int s = Cur.Start;
        Advance();
        int level = 0;
        while (IsOp(".") || IsOp("..."))
        {
            level += IsOp(".") ? 1 : 3;
            Advance();
        }
        string? module = null;
        if (Cur.Kind == TokenKind.Name) module = ParseDottedName();
        else if (level == 0) throw Err("expected module name");
        ExpectKw("import");

        if (IsOp("*"))
        {
            Advance();
            return At(new ImportFromStmt(level, module, new List<ImportAlias>(), true), s);
        }

        bool paren = AcceptOp("(");
        var names = new List<ImportAlias>();
        while (true)
        {
            int a = Cur.Start;
            string n = ExpectName();
            string? asName = AcceptKw("as") ? ExpectName() : null;
            names.Add(At(new ImportAlias(n, asName), a));
            if (!AcceptOp(",")) break;
            if (paren && IsOp(")")) break; // trailing comma
        }
        if (paren) ExpectOp(")");
        return At(new ImportFromStmt(level, module, names, false), s);
    }

    // ------------------------------------------------------------ compound statements

    Stmt ParseIf()
    {
        int s = Cur.Start;
        Advance(); // if / elif
        var test = ParseNamedExpr();
        var body = ParseBlock();
        var orelse = new List<Stmt>();
        if (IsKw("elif")) orelse.Add(ParseIf());
        else if (IsKw("else")) { Advance(); orelse = ParseBlock(); }
        return At(new IfStmt(test, body, orelse), s);
    }

    Stmt ParseWhile()
    {
        int s = Cur.Start;
        Advance();
        var test = ParseNamedExpr();
        var body = ParseBlock();
        var orelse = new List<Stmt>();
        if (AcceptKw("else")) orelse = ParseBlock();
        return At(new WhileStmt(test, body, orelse), s);
    }

    Stmt ParseFor(bool isAsync, int start)
    {
        Advance(); // for
        var target = ParseTargetList();
        ExpectKw("in");
        var iter = ParseStarExpressions();
        var body = ParseBlock();
        var orelse = new List<Stmt>();
        if (AcceptKw("else")) orelse = ParseBlock();
        return At(new ForStmt(target, iter, body, orelse, isAsync), start);
    }

    /// <summary>Targets of for-loops/comprehensions: stops before the 'in' keyword.</summary>
    Expr ParseTargetList()
    {
        int s = Cur.Start;
        var first = ParseStarTarget();
        if (!IsOp(",")) return first;
        var items = new List<Expr> { first };
        while (AcceptOp(","))
        {
            if (IsKw("in") || IsOp("=")) break;
            items.Add(ParseStarTarget());
        }
        return At(new TupleExpr(items, false), s);
    }

    Expr ParseStarTarget()
    {
        int s = Cur.Start;
        if (AcceptOp("*")) return At(new StarExpr(ParseBitOr()), s);
        return ParseBitOr();
    }

    Stmt ParseTry()
    {
        int s = Cur.Start;
        Advance();
        var body = ParseBlock();
        var handlers = new List<ExceptHandler>();
        var orelse = new List<Stmt>();
        var fin = new List<Stmt>();
        bool star = false;
        while (IsKw("except"))
        {
            int h = Cur.Start;
            Advance();
            if (AcceptOp("*")) star = true;
            Expr? type = null;
            string? name = null;
            if (!IsOp(":"))
            {
                type = ParseTest();
                if (IsOp(","))
                {
                    // Python 3.14 allows "except A, B:" without parentheses
                    int ts = type.Start;
                    var items = new List<Expr> { type };
                    while (AcceptOp(",")) { if (IsOp(":") || IsKw("as")) break; items.Add(ParseTest()); }
                    type = At(new TupleExpr(items, false), ts);
                }
                if (AcceptKw("as")) name = ExpectName();
            }
            var hb = ParseBlock();
            handlers.Add(At(new ExceptHandler(type, name, hb), h));
        }
        if (handlers.Count > 0 && AcceptKw("else")) orelse = ParseBlock();
        if (AcceptKw("finally")) fin = ParseBlock();
        if (handlers.Count == 0 && fin.Count == 0)
            _diags.Add(new Diagnostic(s, 3, "'try' must have at least one 'except' or 'finally' clause"));
        return At(new TryStmt(body, handlers, orelse, fin, star), s);
    }

    Stmt ParseWith(bool isAsync, int start)
    {
        Advance(); // with
        var items = new List<WithItem>();

        // "with (a as b, c as d):" parenthesized form: try it first, fall back to an expression.
        bool parsed = false;
        if (IsOp("("))
        {
            int save = _i, saveEnd = _prevEnd, saveDiags = _diags.Count;
            try
            {
                Advance();
                var tmp = new List<WithItem>();
                while (true)
                {
                    tmp.Add(ParseWithItem());
                    if (!AcceptOp(",")) break;
                    if (IsOp(")")) break;
                }
                ExpectOp(")");
                if (!IsOp(":")) throw Err("not a parenthesized with");
                items = tmp;
                parsed = true;
            }
            catch (SyntaxError)
            {
                _i = save; _prevEnd = saveEnd;
                _diags.RemoveRange(saveDiags, _diags.Count - saveDiags);
            }
        }
        if (!parsed)
        {
            do { items.Add(ParseWithItem()); } while (AcceptOp(","));
        }
        var body = ParseBlock();
        return At(new WithStmt(items, body, isAsync), start);
    }

    WithItem ParseWithItem()
    {
        int s = Cur.Start;
        var ctx = ParseTest();
        Expr? target = AcceptKw("as") ? ParseStarTarget() : null;
        return At(new WithItem(ctx, target), s);
    }

    Stmt ParseAsync(List<Expr> decorators, int start)
    {
        Advance(); // async
        if (IsKw("def")) return ParseFuncDef(decorators, true, start);
        if (IsKw("for")) return ParseFor(true, start);
        if (IsKw("with")) return ParseWith(true, start);
        throw Err("expected 'def', 'for' or 'with' after 'async'");
    }

    Stmt ParseDecorated()
    {
        int s = Cur.Start;
        var decorators = new List<Expr>();
        while (IsOp("@"))
        {
            Advance();
            decorators.Add(ParseNamedExpr());
            if (Cur.Kind != TokenKind.Newline) throw Err("expected newline after decorator");
            Advance();
        }
        if (IsKw("def")) return ParseFuncDef(decorators, false, s);
        if (IsKw("class")) return ParseClassDef(decorators, s);
        if (IsKw("async")) return ParseAsync(decorators, s);
        throw Err("expected function or class after decorator");
    }

    Stmt ParseFuncDef(List<Expr> decorators, bool isAsync, int start)
    {
        ExpectKw("def");
        int nameStart = Cur.Start;
        string name = ExpectName();
        var tps = IsOp("[") ? ParseTypeParams() : new List<TypeParam>();
        ExpectOp("(");
        var ps = ParseParameters(")", allowAnnotations: true);
        ExpectOp(")");
        Expr? returns = AcceptOp("->") ? ParseTest() : null;
        var body = ParseBlock();
        return At(new FunctionDef(name, tps, ps, returns, body, decorators, isAsync) { NameStart = nameStart }, start);
    }

    Stmt ParseClassDef(List<Expr> decorators, int start)
    {
        ExpectKw("class");
        int nameStart = Cur.Start;
        string name = ExpectName();
        var tps = IsOp("[") ? ParseTypeParams() : new List<TypeParam>();
        var bases = new List<Argument>();
        if (AcceptOp("(")) { bases = ParseArguments(); ExpectOp(")"); }
        var body = ParseBlock();
        return At(new ClassDef(name, tps, bases, body, decorators) { NameStart = nameStart }, start);
    }

    List<TypeParam> ParseTypeParams()
    {
        ExpectOp("[");
        var list = new List<TypeParam>();
        while (!IsOp("]"))
        {
            int s = Cur.Start;
            var kind = TypeParamKind.TypeVar;
            if (AcceptOp("*")) kind = TypeParamKind.TypeVarTuple;
            else if (AcceptOp("**")) kind = TypeParamKind.ParamSpec;
            string n = ExpectName();
            Expr? bound = null, def = null;
            if (AcceptOp(":")) bound = ParseTest();
            if (AcceptOp("=")) def = ParseTest();
            list.Add(At(new TypeParam(n, kind, bound, def), s));
            if (!AcceptOp(",")) break;
        }
        ExpectOp("]");
        return list;
    }

    Stmt ParseTypeAlias()
    {
        int s = Cur.Start;
        Advance(); // 'type'
        int nameStart = Cur.Start;
        string name = ExpectName();
        var tps = IsOp("[") ? ParseTypeParams() : new List<TypeParam>();
        ExpectOp("=");
        var value = ParseTest();
        return At(new TypeAliasStmt(name, tps, value) { NameStart = nameStart }, s);
    }

    // ------------------------------------------------------------ parameters

    List<Parameter> ParseParameters(string closer, bool allowAnnotations)
    {
        var list = new List<Parameter>();
        while (!IsOp(closer) && !(closer == ":" && IsOp(":")))
        {
            int s = Cur.Start;
            if (AcceptOp("/")) list.Add(At(new Parameter("", null, null, ParamKind.Slash), s));
            else if (IsOp("*") || IsOp("**"))
            {
                bool dbl = IsOp("**");
                Advance();
                if (!dbl && (IsOp(",") || IsOp(closer)))
                    list.Add(At(new Parameter("", null, null, ParamKind.KwOnlySeparator), s));
                else
                {
                    string n = ExpectName();
                    Expr? ann = allowAnnotations && AcceptOp(":") ? ParseStarOrTest() : null;
                    list.Add(At(new Parameter(n, ann, null, dbl ? ParamKind.KwArgs : ParamKind.VarArgs), s));
                }
            }
            else
            {
                string n = ExpectName();
                Expr? ann = allowAnnotations && AcceptOp(":") ? ParseTest() : null;
                Expr? def = AcceptOp("=") ? ParseTest() : null;
                list.Add(At(new Parameter(n, ann, def, ParamKind.Normal), s));
            }
            if (!AcceptOp(",")) break;
        }
        return list;
    }

    Expr ParseStarOrTest()
    {
        int s = Cur.Start;
        if (AcceptOp("*")) return At(new StarExpr(ParseBitOr()), s);
        return ParseTest();
    }

    // ------------------------------------------------------------ match statement

    /// <summary>Speculative: 'match' is only a keyword if the line parses as "match subject: NEWLINE INDENT case".</summary>
    bool TryParseMatch(List<Stmt> into)
    {
        int save = _i, saveEnd = _prevEnd, saveDiags = _diags.Count;
        bool committed = false;
        try
        {
            int s = Cur.Start;
            Advance();
            var subject = ParseStarNamedExpressions();
            if (!IsOp(":") || PeekAt(1).Kind != TokenKind.Newline || PeekAt(2).Kind != TokenKind.Indent)
                throw Err("not a match statement");
            var next = PeekAt(3);
            if (!(next.Kind == TokenKind.Name && Txt(next) == "case")) throw Err("not a match statement");
            Advance(); Advance(); Advance(); // ':' NEWLINE INDENT
            committed = true; // from here on errors are real syntax errors, not a reason to reparse as an expression

            var cases = new List<MatchCase>();
            while (IsSoft("case"))
            {
                int cs = Cur.Start;
                Advance();
                var pat = ParsePatterns();
                Expr? guard = AcceptKw("if") ? ParseNamedExpr() : null;
                var body = ParseBlock();
                cases.Add(At(new MatchCase(pat, guard, body), cs));
            }
            if (Cur.Kind == TokenKind.Dedent) Advance();
            else if (!AtEnd) throw Err("expected 'case'");
            into.Add(At(new MatchStmt(subject, cases), s));
            return true;
        }
        catch (SyntaxError) when (!committed)
        {
            _i = save; _prevEnd = saveEnd;
            _diags.RemoveRange(saveDiags, _diags.Count - saveDiags);
            return false;
        }
    }

    Pattern ParsePatterns()
    {
        int s = Cur.Start;
        var first = ParseMaybeStarPattern();
        if (!IsOp(",")) return first;
        var items = new List<Pattern> { first };
        while (AcceptOp(","))
        {
            if (IsOp(":") || IsKw("if")) break;
            items.Add(ParseMaybeStarPattern());
        }
        return At(new MatchSequence(items), s);
    }

    Pattern ParseMaybeStarPattern()
    {
        int s = Cur.Start;
        if (AcceptOp("*"))
        {
            string n = ExpectName();
            return At(new MatchStar(n == "_" ? null : n), s);
        }
        return ParseAsPattern();
    }

    Pattern ParseAsPattern()
    {
        int s = Cur.Start;
        var p = ParseOrPattern();
        if (AcceptKw("as")) p = At(new MatchAs(p, ExpectName()), s);
        return p;
    }

    Pattern ParseOrPattern()
    {
        int s = Cur.Start;
        var first = ParseClosedPattern();
        if (!IsOp("|")) return first;
        var alts = new List<Pattern> { first };
        while (AcceptOp("|")) alts.Add(ParseClosedPattern());
        return At(new MatchOr(alts), s);
    }

    Pattern ParseClosedPattern()
    {
        int s = Cur.Start;

        if (Cur.Kind == TokenKind.Name)
        {
            // capture, wildcard, dotted value or class pattern
            if (!IsOpAt(1, ".") && !IsOpAt(1, "("))
            {
                string n = Txt(Advance());
                return At(new MatchCapture(n == "_" ? null : n), s);
            }
            Expr e = At(new NameExpr(Txt(Advance())), s);
            while (AcceptOp("."))
            {
                string a = ExpectName();
                e = At(new AttributeExpr(e, a), s);
            }
            if (!AcceptOp("(")) return At(new MatchValue(e), s);

            var pos = new List<Pattern>();
            var kwNames = new List<string>();
            var kwPats = new List<Pattern>();
            while (!IsOp(")"))
            {
                if (Cur.Kind == TokenKind.Name && IsOpAt(1, "="))
                {
                    kwNames.Add(Txt(Advance())); Advance();
                    kwPats.Add(ParseAsPattern());
                }
                else pos.Add(ParseAsPattern());
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
            return At(new MatchClass(e, pos, kwNames, kwPats), s);
        }

        if (IsOp("("))
        {
            Advance();
            if (AcceptOp(")")) return At(new MatchSequence(new List<Pattern>()), s);
            var first = ParseMaybeStarPattern();
            if (IsOp(","))
            {
                var items = new List<Pattern> { first };
                while (AcceptOp(",")) { if (IsOp(")")) break; items.Add(ParseMaybeStarPattern()); }
                ExpectOp(")");
                return At(new MatchSequence(items), s);
            }
            ExpectOp(")");
            return first is MatchStar ? At(new MatchSequence(new List<Pattern> { first }), s) : first;
        }

        if (IsOp("["))
        {
            Advance();
            var items = new List<Pattern>();
            while (!IsOp("]"))
            {
                items.Add(ParseMaybeStarPattern());
                if (!AcceptOp(",")) break;
            }
            ExpectOp("]");
            return At(new MatchSequence(items), s);
        }

        if (IsOp("{"))
        {
            Advance();
            var keys = new List<Expr>();
            var pats = new List<Pattern>();
            string? rest = null;
            while (!IsOp("}"))
            {
                if (AcceptOp("**")) rest = ExpectName();
                else
                {
                    keys.Add(ParseLiteralOrValueExpr());
                    ExpectOp(":");
                    pats.Add(ParseAsPattern());
                }
                if (!AcceptOp(",")) break;
            }
            ExpectOp("}");
            return At(new MatchMapping(keys, pats, rest), s);
        }

        // literal: numbers, strings, None/True/False, signed/complex numbers
        return At(new MatchValue(ParseLiteralOrValueExpr()), s);
    }

    Expr ParseLiteralOrValueExpr()
    {
        // Patterns allow only literals and dotted names (plus -x and a+bj). Parse as arithmetic expression.
        return ParseArith();
    }

    // ------------------------------------------------------------ expressions

    Expr ParseStarExpressionsOrYield() => IsKw("yield") ? ParseYield() : ParseStarExpressions();

    Expr ParseYield()
    {
        int s = Cur.Start;
        ExpectKw("yield");
        if (AcceptKw("from")) return At(new YieldExpr(ParseTest(), true), s);
        Expr? v = null;
        if (!AtStmtEnd() && !IsOp(")") && !IsOp("]") && !IsOp("}") && !IsOp("=")) v = ParseStarExpressions();
        return At(new YieldExpr(v, false), s);
    }

    /// <summary>star_expressions: a comma-separated list (becomes a tuple) of star_expr | namedexpr</summary>
    Expr ParseStarExpressions()
    {
        int s = Cur.Start;
        var first = ParseStarOrNamed();
        if (!IsOp(",")) return first;
        var items = new List<Expr> { first };
        while (AcceptOp(","))
        {
            if (!StartsExpression()) break;
            items.Add(ParseStarOrNamed());
        }
        return At(new TupleExpr(items, false), s);
    }

    Expr ParseStarNamedExpressions() => ParseStarExpressions();

    Expr ParseStarOrNamed()
    {
        int s = Cur.Start;
        if (AcceptOp("*")) return At(new StarExpr(ParseBitOr()), s);
        return ParseNamedExpr();
    }

    bool StartsExpression()
    {
        switch (Cur.Kind)
        {
            case TokenKind.Name: case TokenKind.Number: case TokenKind.String: return true;
            case TokenKind.Keyword:
                return Txt(Cur) is "None" or "True" or "False" or "not" or "lambda" or "await" or "yield";
            case TokenKind.Operator:
                return Txt(Cur) is "(" or "[" or "{" or "-" or "+" or "~" or "*" or "..." or "**";
            default: return false;
        }
    }

    Expr ParseNamedExpr()
    {
        int s = Cur.Start;
        var e = ParseTest();
        if (e is NameExpr && IsOp(":="))
        {
            Advance();
            var v = ParseTest();
            _sawWalrus = true;
            return At(new NamedExpr(e, v), s);
        }
        return e;
    }

    /// <summary>test: lambda | or_test ['if' or_test 'else' test]</summary>
    Expr ParseTest()
    {
        int s = Cur.Start;
        if (IsKw("lambda")) return ParseLambda();
        var body = ParseOr();
        if (IsKw("if"))
        {
            Advance();
            var test = ParseOr();
            ExpectKw("else");
            var orelse = ParseTest();
            return At(new IfExpr(test, body, orelse), s);
        }
        return body;
    }

    Expr ParseLambda()
    {
        int s = Cur.Start;
        ExpectKw("lambda");
        var ps = ParseParameters(":", allowAnnotations: false);
        ExpectOp(":");
        var body = ParseTest();
        return At(new LambdaExpr(ps, body), s);
    }

    Expr ParseOr()
    {
        int s = Cur.Start;
        var l = ParseAnd();
        while (IsKw("or")) { Advance(); var r = ParseAnd(); l = At(new BinaryExpr(l, "or", r), s); }
        return l;
    }

    Expr ParseAnd()
    {
        int s = Cur.Start;
        var l = ParseNot();
        while (IsKw("and")) { Advance(); var r = ParseNot(); l = At(new BinaryExpr(l, "and", r), s); }
        return l;
    }

    Expr ParseNot()
    {
        int s = Cur.Start;
        if (IsKw("not")) { Advance(); return At(new UnaryExpr("not", ParseNot()), s); }
        return ParseComparison();
    }

    Expr ParseComparison()
    {
        int s = Cur.Start;
        var left = ParseBitOr();
        List<string>? ops = null;
        List<Expr>? comps = null;
        while (true)
        {
            string? op = null;
            if (Cur.Kind == TokenKind.Operator)
            {
                var t = Txt(Cur);
                if (t is "<" or ">" or "==" or ">=" or "<=" or "!=" or "<>") { op = t; Advance(); }
            }
            else if (IsKw("in")) { op = "in"; Advance(); }
            else if (IsKw("not") && IsKwAt(1, "in")) { op = "not in"; Advance(); Advance(); }
            else if (IsKw("is"))
            {
                Advance();
                if (AcceptKw("not")) op = "is not"; else op = "is";
            }
            if (op == null) break;
            (ops ??= new()).Add(op);
            (comps ??= new()).Add(ParseBitOr());
        }
        return ops == null ? left : At(new CompareExpr(left, ops, comps!), s);
    }

    static readonly string[][] BinaryOperators = { new[] { "|" }, new[] { "^" }, new[] { "&" }, new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "//", "%", "@" } };

    /// <summary>The operator of this level at the current token, spelled with the shared literal, or null.</summary>
    string? BinaryOperatorAt(string[] ops)
    {
        if (Cur.Kind != TokenKind.Operator) return null;
        foreach (var op in ops)
            if (Cur.Length == op.Length && string.CompareOrdinal(_src, Cur.Start, op, 0, op.Length) == 0) return op;
        return null;
    }

    /// <summary>Left-associative binary operators: level 0 is `|`, then `^`, `&amp;`, shifts, `+ -`, and level 5 `* / // % @`, whose operands are factors.</summary>
    Expr ParseBinaryLevel(int level)
    {
        int s = Cur.Start;
        var l = level == 5 ? ParseFactor() : ParseBinaryLevel(level + 1);
        var ops = BinaryOperators[level];
        while (BinaryOperatorAt(ops) is { } op)
        {
            Advance();
            var r = level == 5 ? ParseFactor() : ParseBinaryLevel(level + 1);
            l = At(new BinaryExpr(l, op, r), s);
        }
        return l;
    }

    Expr ParseBitOr() => ParseBinaryLevel(0);
    Expr ParseArith() => ParseBinaryLevel(4);

    Expr ParseFactor()
    {
        int s = Cur.Start;
        if (Cur.Kind == TokenKind.Operator && Txt(Cur) is "+" or "-" or "~")
        {
            string op = Txt(Advance());
            return At(new UnaryExpr(op, ParseFactor()), s);
        }
        return ParsePower();
    }

    Expr ParsePower()
    {
        int s = Cur.Start;
        Expr b;
        if (IsKw("await")) { Advance(); b = At(new AwaitExpr(ParsePrimary()), s); }
        else b = ParsePrimary();
        if (IsOp("**"))
        {
            Advance();
            var e = ParseFactor(); // right associative, binds tighter than unary on its left only
            return At(new BinaryExpr(b, "**", e), s);
        }
        return b;
    }

    Expr ParsePrimary()
    {
        int s = Cur.Start;
        var e = ParseAtom();
        while (true)
        {
            if (IsOp("."))
            {
                Advance();
                e = At(new AttributeExpr(e, ExpectName()), s);
            }
            else if (IsOp("("))
            {
                Advance();
                var args = ParseArguments();
                ExpectOp(")");
                e = At(new CallExpr(e, args), s);
            }
            else if (IsOp("["))
            {
                Advance();
                var idx = ParseSubscriptList();
                ExpectOp("]");
                e = At(new SubscriptExpr(e, idx), s);
            }
            else break;
        }
        return e;
    }

    List<Argument> ParseArguments()
    {
        var args = new List<Argument>();
        while (!IsOp(")"))
        {
            int s = Cur.Start;
            if (AcceptOp("**")) args.Add(At(new Argument(null, ParseTest(), 2), s));
            else if (AcceptOp("*")) args.Add(At(new Argument(null, ParseTest(), 1), s));
            else if (Cur.Kind == TokenKind.Name && IsOpAt(1, "=") )
            {
                string n = Txt(Advance()); Advance();
                args.Add(At(new Argument(n, ParseTest(), 0), s));
            }
            else
            {
                var v = ParseNamedExpr();
                if (IsKw("for") || (IsKw("async") && IsKwAt(1, "for")))
                {
                    var gens = ParseCompFor();
                    v = At(new CompExpr(CompKind.Generator, v, null, gens), s);
                }
                args.Add(At(new Argument(null, v, 0), s));
            }
            if (!AcceptOp(",")) break;
        }
        return args;
    }

    Expr ParseSubscriptList()
    {
        int s = Cur.Start;
        var first = ParseSliceItem();
        if (!IsOp(",")) return first;
        var items = new List<Expr> { first };
        while (AcceptOp(","))
        {
            if (IsOp("]")) break;
            items.Add(ParseSliceItem());
        }
        return At(new TupleExpr(items, false), s);
    }

    Expr ParseSliceItem()
    {
        int s = Cur.Start;
        if (IsOp("*")) { Advance(); return At(new StarExpr(ParseBitOr()), s); }
        Expr? lower = null;
        if (!IsOp(":")) lower = ParseNamedExpr();
        if (!IsOp(":")) return lower!;
        Advance();
        Expr? upper = null, step = null;
        if (!IsOp(":") && !IsOp(",") && !IsOp("]")) upper = ParseTest();
        if (AcceptOp(":") && !IsOp(",") && !IsOp("]")) step = ParseTest();
        return At(new SliceExpr(lower, upper, step), s);
    }

    Expr ParseAtom()
    {
        int s = Cur.Start;
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Name:
                Advance();
                return At(new NameExpr(Txt(t)), s);

            case TokenKind.Number:
                Advance();
                return At(new ConstExpr(NumberKind(_src.Substring(t.Start, t.Length)), _src.Substring(t.Start, t.Length)), s);

            case TokenKind.String:
                return ParseStrings();

            case TokenKind.Keyword:
                switch (Txt(t))
                {
                    case "None": Advance(); return At(new ConstExpr(ConstKind.None, "None"), s);
                    case "True": Advance(); return At(new ConstExpr(ConstKind.True, "True"), s);
                    case "False": Advance(); return At(new ConstExpr(ConstKind.False, "False"), s);
                }
                break;

            case TokenKind.Operator:
                switch (Txt(t))
                {
                    case "...": Advance(); return At(new ConstExpr(ConstKind.Ellipsis, "..."), s);
                    case "(": return ParseParen();
                    case "[": return ParseListDisplay();
                    case "{": return ParseBraces();
                }
                break;
        }
        throw Err("invalid syntax");
    }

    static ConstKind NumberKind(string text)
    {
        char last = text[^1];
        if (last is 'j' or 'J') return ConstKind.Imaginary;
        if (text.Length > 1 && text[0] == '0' && char.ToLowerInvariant(text[1]) is 'x' or 'o' or 'b') return ConstKind.Int;
        if (text.Contains('.') || text.Contains('e') || text.Contains('E')) return ConstKind.Float;
        return ConstKind.Int;
    }

    Expr ParseStrings()
    {
        int s = Cur.Start;
        bool bytes = false, fstr = false;
        var parts = new List<Expr>();
        while (Cur.Kind == TokenKind.String)
        {
            var t = Advance();
            bool formatted = false;
            for (int i = t.Start; i < t.End && _src[i] != '"' && _src[i] != '\''; i++)
            {
                char c = char.ToLowerInvariant(_src[i]);
                if (c == 'b') bytes = true;
                else if (c is 'f' or 't') fstr = formatted = true;
            }
            if (formatted) FStringParts(t, parts);
        }
        return At(new ConstExpr(bytes ? ConstKind.Bytes : ConstKind.Str, _src.Substring(s, _prevEnd - s), fstr) { Parts = parts }, s);
    }

    // ------------------------------------------------------------ f-string replacement fields

    /// <summary>Parses the expressions in the replacement fields of an f-string token so names used in them are references like any other.</summary>
    void FStringParts(Token t, List<Expr> parts)
    {
        int q = t.Start;
        bool raw = false;
        for (; q < t.End && _src[q] != '"' && _src[q] != '\''; q++)
            if (char.ToLowerInvariant(_src[q]) == 'r') raw = true;
        if (q >= t.End) return;
        char quote = _src[q];
        int quoteLength = q + 2 < t.End && _src[q + 1] == quote && _src[q + 2] == quote ? 3 : 1;
        int bodyStart = q + quoteLength, bodyEnd = t.End - quoteLength;
        if (bodyEnd <= bodyStart) return;

        int pos = bodyStart;
        while (pos < bodyEnd)
        {
            char c = _src[pos];
            if (c == '{')
            {
                if (pos + 1 < bodyEnd && _src[pos + 1] == '{') { pos += 2; continue; }
                pos = FStringField(pos, bodyEnd, parts);
            }
            else if (!raw && c == '\\' && pos + 2 < bodyEnd && _src[pos + 1] == 'N' && _src[pos + 2] == '{')
            {
                while (pos < bodyEnd && _src[pos] != '}') pos++;   // \N{unicode name} is not a field
                pos++;
            }
            else pos++;
        }
    }

    /// <summary>The field that starts at <paramref name="open"/> (a brace): its expression, then conversion and format spec, which may hold fields of their own. Returns the offset after the closing brace.</summary>
    int FStringField(int open, int end, List<Expr> parts)
    {
        int start = open + 1, p = start, depth = 0;
        while (p < end)
        {
            char c = _src[p];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == '}')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (c is '"' or '\'') { p = SkipQuoted(p, end); continue; }
            else if (depth == 0)
            {
                if (c == '!' && !(p + 1 < end && _src[p + 1] == '=')) break;
                if (c == ':') break;
                if (c == '=' && IsSelfDocumenting(p, end)) break;
            }
            p++;
        }
        ParseFieldExpression(start, p, parts);

        if (p < end && _src[p] == '=') { p++; while (p < end && char.IsWhiteSpace(_src[p])) p++; }
        if (p < end && _src[p] == '!') { while (p < end && _src[p] != ':' && _src[p] != '}') p++; }
        if (p < end && _src[p] == ':')
        {
            p++;
            while (p < end && _src[p] != '}')
                p = _src[p] == '{' ? FStringField(p, end, parts) : p + 1;
        }
        return p < end ? p + 1 : end;
    }

    /// <summary>`=` of `{x=}`: not part of `==`, `!=`, `<=`, `>=`, and followed by the end of the field, a conversion or a spec.</summary>
    bool IsSelfDocumenting(int p, int end)
    {
        if (p > 0 && _src[p - 1] is '=' or '!' or '<' or '>') return false;
        if (p + 1 < end && _src[p + 1] == '=') return false;
        int n = p + 1;
        while (n < end && char.IsWhiteSpace(_src[n])) n++;
        return n < end && _src[n] is '}' or '!' or ':';
    }

    /// <summary>The offset after the string literal that starts at <paramref name="p"/>.</summary>
    int SkipQuoted(int p, int end)
    {
        char quote = _src[p];
        bool triple = p + 2 < end && _src[p + 1] == quote && _src[p + 2] == quote;
        int i = p + (triple ? 3 : 1);
        while (i < end)
        {
            if (_src[i] == '\\') { i += 2; continue; }
            if (_src[i] == quote && (!triple || i + 2 < end && _src[i + 1] == quote && _src[i + 2] == quote))
                return i + (triple ? 3 : 1);
            i++;
        }
        return end;
    }

    /// <summary>Moves a tree parsed from a piece of the source to where that piece sits in the whole.</summary>
    static void Shift(Node n, int by)
    {
        n.Start += by;
        n.End += by;
        foreach (var c in AstChildren.Of(n)) Shift(c, by);
    }

    void ParseFieldExpression(int start, int end, List<Expr> parts)
    {
        if (end <= start || _src.AsSpan(start, end - start).Trim().Length == 0) return;
        try
        {
            // the field is wrapped in parentheses (so it may span lines) and parsed on its own; the nodes are then moved to where the field really is
            string text = "(" + _src.Substring(start, end - start) + ")";
            var all = Lexer.Tokenize(text);
            var tokens = new List<Token>(all.Count);
            foreach (var k in all)
                if (k.Kind != TokenKind.Comment) tokens.Add(k);
            var expr = new Parser(text, tokens).ParseParen();
            Shift(expr, start - 1);
            parts.Add(expr);
        }
        catch (SyntaxError) { /* an unparsable field stays opaque, as before */ }
    }

    Expr ParseParen()
    {
        int s = Cur.Start;
        Advance(); // (
        if (AcceptOp(")")) return At(new TupleExpr(new List<Expr>(), true), s);
        if (IsKw("yield"))
        {
            var y = ParseYield();
            ExpectOp(")");
            return y;
        }
        var first = ParseStarOrNamed();
        if (IsKw("for") || (IsKw("async") && IsKwAt(1, "for")))
        {
            var gens = ParseCompFor();
            ExpectOp(")");
            return At(new CompExpr(CompKind.Generator, first, null, gens), s);
        }
        if (IsOp(","))
        {
            var items = new List<Expr> { first };
            while (AcceptOp(","))
            {
                if (IsOp(")")) break;
                items.Add(ParseStarOrNamed());
            }
            ExpectOp(")");
            return At(new TupleExpr(items, true), s);
        }
        ExpectOp(")");
        // Keep the inner node but widen its range to include the parentheses.
        return first is StarExpr ? At(new TupleExpr(new List<Expr> { first }, true), s) : first;
    }

    Expr ParseListDisplay()
    {
        int s = Cur.Start;
        Advance(); // [
        if (AcceptOp("]")) return At(new ListExpr(new List<Expr>()), s);
        var first = ParseStarOrNamed();
        if (IsKw("for") || (IsKw("async") && IsKwAt(1, "for")))
        {
            var gens = ParseCompFor();
            ExpectOp("]");
            return At(new CompExpr(CompKind.List, first, null, gens), s);
        }
        var items = new List<Expr> { first };
        while (AcceptOp(","))
        {
            if (IsOp("]")) break;
            items.Add(ParseStarOrNamed());
        }
        ExpectOp("]");
        return At(new ListExpr(items), s);
    }

    Expr ParseBraces()
    {
        int s = Cur.Start;
        Advance(); // {
        if (AcceptOp("}")) return At(new DictExpr(new List<Expr?>(), new List<Expr>()), s);

        // dict or set; decide from the first element
        if (IsOp("**"))
        {
            return ParseDictRest(s, new List<Expr?>(), new List<Expr>());
        }
        var first = ParseStarOrNamed();
        if (IsOp(":") && first is not StarExpr)
        {
            Advance();
            var v = ParseTest();
            if (IsKw("for") || (IsKw("async") && IsKwAt(1, "for")))
            {
                var gens = ParseCompFor();
                ExpectOp("}");
                return At(new CompExpr(CompKind.Dict, first, v, gens), s);
            }
            var keys = new List<Expr?> { first };
            var vals = new List<Expr> { v };
            if (AcceptOp(",")) return ParseDictRest(s, keys, vals);
            ExpectOp("}");
            return At(new DictExpr(keys, vals), s);
        }

        if (IsKw("for") || (IsKw("async") && IsKwAt(1, "for")))
        {
            var gens = ParseCompFor();
            ExpectOp("}");
            return At(new CompExpr(CompKind.Set, first, null, gens), s);
        }
        var items = new List<Expr> { first };
        while (AcceptOp(","))
        {
            if (IsOp("}")) break;
            items.Add(ParseStarOrNamed());
        }
        ExpectOp("}");
        return At(new SetExpr(items), s);
    }

    Expr ParseDictRest(int s, List<Expr?> keys, List<Expr> vals)
    {
        while (!IsOp("}"))
        {
            if (AcceptOp("**")) { keys.Add(null); vals.Add(ParseBitOr()); }
            else
            {
                keys.Add(ParseTest());
                ExpectOp(":");
                vals.Add(ParseTest());
            }
            if (!AcceptOp(",")) break;
        }
        ExpectOp("}");
        return At(new DictExpr(keys, vals), s);
    }

    List<Comprehension> ParseCompFor()
    {
        var gens = new List<Comprehension>();
        while (IsKw("for") || (IsKw("async") && IsKwAt(1, "for")))
        {
            int s = Cur.Start;
            bool isAsync = AcceptKw("async");
            ExpectKw("for");
            var target = ParseTargetList();
            ExpectKw("in");
            var iter = ParseOr();
            var ifs = new List<Expr>();
            while (IsKw("if"))
            {
                Advance();
                ifs.Add(ParseOrNoCond());
            }
            gens.Add(At(new Comprehension(target, iter, ifs, isAsync), s));
        }
        return gens;
    }

    /// <summary>Condition in a comprehension 'if': an or_test (optionally a walrus), no ternary.</summary>
    Expr ParseOrNoCond()
    {
        int s = Cur.Start;
        var e = ParseOr();
        if (e is NameExpr && IsOp(":=")) { Advance(); var v = ParseOr(); _sawWalrus = true; return At(new NamedExpr(e, v), s); }
        return e;
    }
}
