using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PyMagic.Core.Syntax;

/// <summary>
/// Syntax trees of library files (typeshed's stubs, stub packages, installed libraries) kept on disk between starts, so that a start reads
/// builtins.pyi and the rest instead of parsing them again. A tree is found by a hash of the file's text, in a folder of this build of PyMagic:
/// a changed file is parsed again, and a new PyMagic does not read what an older one wrote (the older folders are removed). Off while
/// <see cref="Folder"/> is null (the tests); the app sets it.
/// </summary>
public static class ParsedFileCache
{
    /// <summary>Where the trees are kept (the app: local app data's PyMagic\parsed, or PYMAGIC_PARSED_CACHE); null keeps nothing.</summary>
    public static string? Folder { get; set; } = System.Environment.GetEnvironmentVariable("PYMAGIC_PARSED_CACHE") is { Length: > 0 } own ? own : null;

    /// <summary>How many trees were read from the folder and how many were parsed and written (for tests and tuning).</summary>
    public static int Read => _read;
    public static int Written => _written;
    static int _read, _written;
    static int _cleaned;

    /// <summary>
    /// The most the build's folder may hold, in bytes; over it the trees used least recently go (down to three quarters of it, so that it is
    /// not trimmed again at every write). 256 MB: typeshed's 5,300 stubs make about 14 MB of trees and a large environment (pandas, numpy,
    /// pydantic and the scan's libraries, about 180 MB of library code) at most about 100 MB, so a few projects' environments stay whole,
    /// while the folder no longer grows without end until the next build removes it.
    /// </summary>
    public static long Limit { get; set; } = 256L << 20;

    /// <summary>The trees read in this run, waiting for the trim to mark them used now (their last write time is the time of their last use).</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _used = new(StringComparer.OrdinalIgnoreCase);
    static long _sinceTrim;     // bytes written since the last trim
    static int _trimming;       // a trim is waiting or running

    const int Magic = 0x31545350;   // "PST1"

    static readonly string Build = typeof(ParsedFileCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    /// <summary>The tree and syntax errors of this text: read from the folder when it was parsed before, parsed (and kept) otherwise.</summary>
    public static (Module Module, IReadOnlyList<Diagnostic> Diagnostics) Parse(string source)
    {
        string? folder = Folder;
        if (folder == null) return Plain(source);
        string dir = Path.Combine(folder, Build);
        string file = Path.Combine(dir, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(source.AsSpan())), 0, 16) + ".ast");
        if (Interlocked.Exchange(ref _cleaned, 1) == 0)
        {
            Task.Run(() => RemoveOtherBuilds(folder));
            TrimSoon(dir, 20_000);   // once the start's parsing is over
        }
        try
        {
            if (File.Exists(file))
            {
                var tree = new Reader(File.ReadAllBytes(file)).ReadFile(source.Length);
                if (tree != null)
                {
                    Interlocked.Increment(ref _read);
                    _used.TryAdd(file, 0);
                    return tree.Value;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or InvalidCastException or IndexOutOfRangeException) { }

        var parsed = Plain(source);
        try
        {
            Directory.CreateDirectory(dir);
            string temp = file + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
            var bytes = Writer.Write(parsed.Module, parsed.Diagnostics, source.Length);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, true);
            Interlocked.Increment(ref _written);
            // a session that keeps writing (a new environment, a large library opened) is trimmed after each eighth of the limit
            if (Interlocked.Add(ref _sinceTrim, bytes.Length) >= Limit / 8)
            {
                Interlocked.Exchange(ref _sinceTrim, 0);
                TrimSoon(dir, 5_000);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return parsed;
    }

    static (Module Module, IReadOnlyList<Diagnostic> Diagnostics) Plain(string source)
    {
        var r = Parser.Parse(source);
        return (r.Module, r.Diagnostics);
    }

    /// <summary>
    /// Trims the folder on a thread of its own, at a low priority and after a pause: listing and removing files never holds up the parsing
    /// a keystroke waits for. One trim at a time; a request while one waits is covered by it.
    /// </summary>
    static void TrimSoon(string dir, int delay)
    {
        if (Interlocked.Exchange(ref _trimming, 1) != 0) return;
        var thread = new Thread(() =>
        {
            try { Thread.Sleep(delay); Trim(dir, Limit); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally { Volatile.Write(ref _trimming, 0); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "parsed-file cache trim" };
        thread.Start();
    }

    /// <summary>Trims this build's folder now (what the background trim does); the number of trees removed.</summary>
    public static int Trim() => Folder is { } folder ? Trim(Path.Combine(folder, Build), Limit) : 0;

    /// <summary>
    /// Marks the trees read since the last trim as used now, then, when the folder holds more than <paramref name="limit"/> bytes, removes the
    /// trees used least recently until it holds three quarters of it. Leftover .tmp files of a write that did not finish go too.
    /// </summary>
    static int Trim(string dir, long limit)
    {
        var now = DateTime.UtcNow;
        foreach (var used in _used.Keys)
        {
            _used.TryRemove(used, out _);
            try { File.SetLastWriteTimeUtc(used, now); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // removed meanwhile, or another start reads it
        }
        if (!Directory.Exists(dir)) return 0;
        var files = new List<FileInfo>();
        long total = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
        {
            if (f.Extension == ".tmp")
            {
                if (now - f.LastWriteTimeUtc > TimeSpan.FromHours(1)) try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                continue;
            }
            files.Add(f);
            total += f.Length;
        }
        if (total <= limit) return 0;
        int removed = 0;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= limit * 3 / 4) break;
            long length = f.Length;   // a FileInfo forgets its length once the file is deleted
            try { f.Delete(); total -= length; removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>The folders older builds of PyMagic wrote.</summary>
    static void RemoveOtherBuilds(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var dir in Directory.EnumerateDirectories(folder))
                if (!string.Equals(Path.GetFileName(dir), Build, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A file: the magic number, the length of the text it was parsed from, the strings, the syntax errors, then the module, node by node: a tag,
    // the start, the length, and the node's fields in the order of its record (strings as indexes into the table, lists with their count first).

    enum Tag : byte
    {
        Null, Name, Const, Unary, Binary, Compare, Argument, Call, Attribute, Subscript, Slice, Tuple, List, Set, Dict, Comprehension, Comp,
        Lambda, If, Star, Await, Yield, Named, Error, Parameter, TypeParam, MatchValue, MatchCapture, MatchStar, MatchAs, MatchOr, MatchSequence,
        MatchMapping, MatchClass, MatchCase, ExprStmt, Assign, AugAssign, AnnAssign, Return, Pass, Break, Continue, Raise, Global, Nonlocal, Del,
        Assert, ImportAlias, Import, ImportFrom, IfStmt, While, For, ExceptHandler, Try, WithItem, With, FunctionDef, ClassDef, Match, TypeAlias, Module,
    }

    sealed class Writer
    {
        readonly BinaryWriter _w;
        readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
        readonly List<string> _table = new();

        Writer(Stream s) { _w = new BinaryWriter(s); }

        public static byte[] Write(Module module, IReadOnlyList<Diagnostic> diagnostics, int length)
        {
            var body = new MemoryStream();
            var w = new Writer(body);
            w.Count(diagnostics.Count);
            foreach (var d in diagnostics) { w.Int(d.Start); w.Int(d.Length); w.Str(d.Message); }
            w.N(module);
            w._w.Flush();
            var file = new MemoryStream();
            var head = new BinaryWriter(file);
            head.Write(Magic);
            head.Write7BitEncodedInt(length);
            head.Write7BitEncodedInt(w._table.Count);
            foreach (var s in w._table) head.Write(s);
            head.Write(body.GetBuffer(), 0, (int)body.Length);
            head.Flush();
            return file.ToArray();
        }

        void Int(int v) => _w.Write7BitEncodedInt(v);
        void Count(int n) => _w.Write7BitEncodedInt(n);
        void Bool(bool b) => _w.Write(b);
        void Byte(int b) => _w.Write((byte)b);

        void Str(string? s)
        {
            if (s == null) { Int(0); return; }
            if (!_strings.TryGetValue(s, out int i)) { _strings[s] = i = _table.Count; _table.Add(s); }
            Int(i + 1);
        }

        void Strs(IReadOnlyList<string> list) { Count(list.Count); foreach (var s in list) Str(s); }
        void L<T>(IReadOnlyList<T> list) where T : Node? { Count(list.Count); foreach (var n in list) N(n); }

        void Head(Tag tag, Node n) { Byte((int)tag); Int(n.Start); Int(n.End - n.Start); }

        public void N(Node? node)
        {
            switch (node)
            {
                case null: Byte((int)Tag.Null); break;
                case NameExpr x: Head(Tag.Name, x); Str(x.Name); break;
                case ConstExpr x: Head(Tag.Const, x); Byte((int)x.Kind); Str(x.Text); Bool(x.IsFString); L(x.Parts); break;
                case UnaryExpr x: Head(Tag.Unary, x); Str(x.Op); N(x.Operand); break;
                case BinaryExpr x: Head(Tag.Binary, x); N(x.Left); Str(x.Op); N(x.Right); break;
                case CompareExpr x: Head(Tag.Compare, x); N(x.Left); Strs(x.Ops); L(x.Comparators); break;
                case Argument x: Head(Tag.Argument, x); Str(x.Name); N(x.Value); Int(x.Stars); break;
                case CallExpr x: Head(Tag.Call, x); N(x.Func); L(x.Args); break;
                case AttributeExpr x: Head(Tag.Attribute, x); N(x.Value); Str(x.Attr); break;
                case SubscriptExpr x: Head(Tag.Subscript, x); N(x.Value); N(x.Index); break;
                case SliceExpr x: Head(Tag.Slice, x); N(x.Lower); N(x.Upper); N(x.Step); break;
                case TupleExpr x: Head(Tag.Tuple, x); L(x.Items); Bool(x.Parenthesized); break;
                case ListExpr x: Head(Tag.List, x); L(x.Items); break;
                case SetExpr x: Head(Tag.Set, x); L(x.Items); break;
                case DictExpr x: Head(Tag.Dict, x); L(x.Keys); L(x.Values); break;
                case Comprehension x: Head(Tag.Comprehension, x); N(x.Target); N(x.Iter); L(x.Ifs); Bool(x.IsAsync); break;
                case CompExpr x: Head(Tag.Comp, x); Byte((int)x.Kind); N(x.Element); N(x.Value); L(x.Generators); break;
                case LambdaExpr x: Head(Tag.Lambda, x); L(x.Params); N(x.Body); break;
                case IfExpr x: Head(Tag.If, x); N(x.Test); N(x.Body); N(x.Orelse); break;
                case StarExpr x: Head(Tag.Star, x); N(x.Value); break;
                case AwaitExpr x: Head(Tag.Await, x); N(x.Value); break;
                case YieldExpr x: Head(Tag.Yield, x); N(x.Value); Bool(x.IsFrom); break;
                case NamedExpr x: Head(Tag.Named, x); N(x.Target); N(x.Value); break;
                case ErrorExpr x: Head(Tag.Error, x); break;
                case Parameter x: Head(Tag.Parameter, x); Str(x.Name); N(x.Annotation); N(x.Default); Byte((int)x.Kind); break;
                case TypeParam x: Head(Tag.TypeParam, x); Str(x.Name); Byte((int)x.Kind); N(x.Bound); N(x.Default); break;
                case MatchValue x: Head(Tag.MatchValue, x); N(x.Value); break;
                case MatchCapture x: Head(Tag.MatchCapture, x); Str(x.Name); break;
                case MatchStar x: Head(Tag.MatchStar, x); Str(x.Name); break;
                case MatchAs x: Head(Tag.MatchAs, x); N(x.Pattern); Str(x.Name); break;
                case MatchOr x: Head(Tag.MatchOr, x); L(x.Alternatives); break;
                case MatchSequence x: Head(Tag.MatchSequence, x); L(x.Items); break;
                case MatchMapping x: Head(Tag.MatchMapping, x); L(x.Keys); L(x.Patterns); Str(x.Rest); break;
                case MatchClass x: Head(Tag.MatchClass, x); N(x.Cls); L(x.Positional); Strs(x.KeywordNames); L(x.KeywordPatterns); break;
                case MatchCase x: Head(Tag.MatchCase, x); N(x.Pattern); N(x.Guard); L(x.Body); break;
                case ExprStmt x: Head(Tag.ExprStmt, x); N(x.Value); break;
                case AssignStmt x: Head(Tag.Assign, x); L(x.Targets); N(x.Value); break;
                case AugAssignStmt x: Head(Tag.AugAssign, x); N(x.Target); Str(x.Op); N(x.Value); break;
                case AnnAssignStmt x: Head(Tag.AnnAssign, x); N(x.Target); N(x.Annotation); N(x.Value); break;
                case ReturnStmt x: Head(Tag.Return, x); N(x.Value); break;
                case PassStmt x: Head(Tag.Pass, x); break;
                case BreakStmt x: Head(Tag.Break, x); break;
                case ContinueStmt x: Head(Tag.Continue, x); break;
                case RaiseStmt x: Head(Tag.Raise, x); N(x.Exc); N(x.Cause); break;
                case GlobalStmt x: Head(Tag.Global, x); Strs(x.Names); break;
                case NonlocalStmt x: Head(Tag.Nonlocal, x); Strs(x.Names); break;
                case DelStmt x: Head(Tag.Del, x); L(x.Targets); break;
                case AssertStmt x: Head(Tag.Assert, x); N(x.Test); N(x.Message); break;
                case ImportAlias x: Head(Tag.ImportAlias, x); Str(x.Name); Str(x.AsName); break;
                case ImportStmt x: Head(Tag.Import, x); L(x.Names); break;
                case ImportFromStmt x: Head(Tag.ImportFrom, x); Int(x.Level); Str(x.Module); L(x.Names); Bool(x.IsStar); break;
                case IfStmt x: Head(Tag.IfStmt, x); N(x.Test); L(x.Body); L(x.Orelse); break;
                case WhileStmt x: Head(Tag.While, x); N(x.Test); L(x.Body); L(x.Orelse); break;
                case ForStmt x: Head(Tag.For, x); N(x.Target); N(x.Iter); L(x.Body); L(x.Orelse); Bool(x.IsAsync); break;
                case ExceptHandler x: Head(Tag.ExceptHandler, x); N(x.Type); Str(x.Name); L(x.Body); break;
                case TryStmt x: Head(Tag.Try, x); L(x.Body); L(x.Handlers); L(x.Orelse); L(x.Finally); Bool(x.IsStar); break;
                case WithItem x: Head(Tag.WithItem, x); N(x.Context); N(x.Target); break;
                case WithStmt x: Head(Tag.With, x); L(x.Items); L(x.Body); Bool(x.IsAsync); break;
                case FunctionDef x:
                    Head(Tag.FunctionDef, x); Str(x.Name); L(x.TypeParams); L(x.Params); N(x.Returns); L(x.Body); L(x.Decorators); Bool(x.IsAsync); Int(x.NameStart);
                    break;
                case ClassDef x: Head(Tag.ClassDef, x); Str(x.Name); L(x.TypeParams); L(x.Bases); L(x.Body); L(x.Decorators); Int(x.NameStart); break;
                case MatchStmt x: Head(Tag.Match, x); N(x.Subject); L(x.Cases); break;
                case TypeAliasStmt x: Head(Tag.TypeAlias, x); Str(x.Name); L(x.TypeParams); N(x.Value); Int(x.NameStart); break;
                case Module x: Head(Tag.Module, x); L(x.Body); Bool(x.HasWalrus); Bool(x.HasScopeDeclarations); break;
                default: throw new InvalidDataException("a node the cache does not know: " + node.GetType().Name);
            }
        }
    }

    sealed class Reader
    {
        readonly BinaryReader _r;
        string[] _table = Array.Empty<string>();

        public Reader(byte[] bytes) { _r = new BinaryReader(new MemoryStream(bytes, false)); }

        /// <summary>The tree, or null when the file is not one of this cache's for a text of this length.</summary>
        public (Module, IReadOnlyList<Diagnostic>)? ReadFile(int length)
        {
            if (_r.ReadInt32() != Magic || _r.Read7BitEncodedInt() != length) return null;
            _table = new string[_r.Read7BitEncodedInt()];
            for (int i = 0; i < _table.Length; i++) _table[i] = WordCache.Get(_r.ReadString());
            var diagnostics = new Diagnostic[Count()];
            for (int i = 0; i < diagnostics.Length; i++) diagnostics[i] = new Diagnostic(Int(), Int(), Str()!);
            return N() is Module m ? (m, diagnostics) : null;
        }

        int Int() => _r.Read7BitEncodedInt();
        int Count() => _r.Read7BitEncodedInt();
        bool Bool() => _r.ReadBoolean();
        byte Byte() => _r.ReadByte();

        string? Str()
        {
            int i = Int();
            return i == 0 ? null : _table[i - 1];
        }

        string[] Strs()
        {
            var a = new string[Count()];
            for (int i = 0; i < a.Length; i++) a[i] = Str()!;
            return a;
        }

        T[] L<T>() where T : Node?
        {
            int n = Count();
            if (n == 0) return Array.Empty<T>();
            var a = new T[n];
            for (int i = 0; i < n; i++) a[i] = (T)N()!;
            return a;
        }

        Expr E() => (Expr)N()!;
        Expr? OE() => (Expr?)N();

        public Node? N()
        {
            var tag = (Tag)Byte();
            if (tag == Tag.Null) return null;
            int start = Int();
            int end = start + Int();
            Node n = tag switch
            {
                Tag.Name => new NameExpr(Str()!),
                Tag.Const => ReadConst(),
                Tag.Unary => new UnaryExpr(Str()!, E()),
                Tag.Binary => new BinaryExpr(E(), Str()!, E()),
                Tag.Compare => new CompareExpr(E(), Strs(), L<Expr>()),
                Tag.Argument => new Argument(Str(), E(), Int()),
                Tag.Call => new CallExpr(E(), L<Argument>()),
                Tag.Attribute => new AttributeExpr(E(), Str()!),
                Tag.Subscript => new SubscriptExpr(E(), E()),
                Tag.Slice => new SliceExpr(OE(), OE(), OE()),
                Tag.Tuple => new TupleExpr(L<Expr>(), Bool()),
                Tag.List => new ListExpr(L<Expr>()),
                Tag.Set => new SetExpr(L<Expr>()),
                Tag.Dict => new DictExpr(L<Expr?>(), L<Expr>()),
                Tag.Comprehension => new Comprehension(E(), E(), L<Expr>(), Bool()),
                Tag.Comp => new CompExpr((CompKind)Byte(), E(), OE(), L<Comprehension>()),
                Tag.Lambda => new LambdaExpr(L<Parameter>(), E()),
                Tag.If => new IfExpr(E(), E(), E()),
                Tag.Star => new StarExpr(E()),
                Tag.Await => new AwaitExpr(E()),
                Tag.Yield => new YieldExpr(OE(), Bool()),
                Tag.Named => new NamedExpr(E(), E()),
                Tag.Error => new ErrorExpr(),
                Tag.Parameter => new Parameter(Str()!, OE(), OE(), (ParamKind)Byte()),
                Tag.TypeParam => new TypeParam(Str()!, (TypeParamKind)Byte(), OE(), OE()),
                Tag.MatchValue => new MatchValue(E()),
                Tag.MatchCapture => new MatchCapture(Str()),
                Tag.MatchStar => new MatchStar(Str()),
                Tag.MatchAs => new MatchAs((Pattern)N()!, Str()!),
                Tag.MatchOr => new MatchOr(L<Pattern>()),
                Tag.MatchSequence => new MatchSequence(L<Pattern>()),
                Tag.MatchMapping => new MatchMapping(L<Expr>(), L<Pattern>(), Str()),
                Tag.MatchClass => new MatchClass(E(), L<Pattern>(), Strs(), L<Pattern>()),
                Tag.MatchCase => new MatchCase((Pattern)N()!, OE(), L<Stmt>()),
                Tag.ExprStmt => new ExprStmt(E()),
                Tag.Assign => new AssignStmt(L<Expr>(), E()),
                Tag.AugAssign => new AugAssignStmt(E(), Str()!, E()),
                Tag.AnnAssign => new AnnAssignStmt(E(), E(), OE()),
                Tag.Return => new ReturnStmt(OE()),
                Tag.Pass => new PassStmt(),
                Tag.Break => new BreakStmt(),
                Tag.Continue => new ContinueStmt(),
                Tag.Raise => new RaiseStmt(OE(), OE()),
                Tag.Global => new GlobalStmt(Strs()),
                Tag.Nonlocal => new NonlocalStmt(Strs()),
                Tag.Del => new DelStmt(L<Expr>()),
                Tag.Assert => new AssertStmt(E(), OE()),
                Tag.ImportAlias => new ImportAlias(Str()!, Str()),
                Tag.Import => new ImportStmt(L<ImportAlias>()),
                Tag.ImportFrom => new ImportFromStmt(Int(), Str(), L<ImportAlias>(), Bool()),
                Tag.IfStmt => new IfStmt(E(), L<Stmt>(), L<Stmt>()),
                Tag.While => new WhileStmt(E(), L<Stmt>(), L<Stmt>()),
                Tag.For => new ForStmt(E(), E(), L<Stmt>(), L<Stmt>(), Bool()),
                Tag.ExceptHandler => new ExceptHandler(OE(), Str(), L<Stmt>()),
                Tag.Try => new TryStmt(L<Stmt>(), L<ExceptHandler>(), L<Stmt>(), L<Stmt>(), Bool()),
                Tag.WithItem => new WithItem(E(), OE()),
                Tag.With => new WithStmt(L<WithItem>(), L<Stmt>(), Bool()),
                Tag.FunctionDef => ReadFunction(),
                Tag.ClassDef => new ClassDef(Str()!, L<TypeParam>(), L<Argument>(), L<Stmt>(), L<Expr>()) { NameStart = Int() },
                Tag.Match => new MatchStmt(E(), L<MatchCase>()),
                Tag.TypeAlias => new TypeAliasStmt(Str()!, L<TypeParam>(), E()) { NameStart = Int() },
                Tag.Module => new Module(L<Stmt>()) { HasWalrus = Bool(), HasScopeDeclarations = Bool() },
                _ => throw new InvalidDataException("unknown node tag " + tag),
            };
            n.Start = start;
            n.End = end;
            return n;
        }

        ConstExpr ReadConst()
        {
            var kind = (ConstKind)Byte();
            string text = Str()!;
            bool f = Bool();
            return new ConstExpr(kind, text, f) { Parts = L<Expr>() };
        }

        FunctionDef ReadFunction()
        {
            string name = Str()!;
            var typeParams = L<TypeParam>();
            var ps = L<Parameter>();
            var returns = OE();
            var body = L<Stmt>();
            var decorators = L<Expr>();
            bool isAsync = Bool();
            return new FunctionDef(name, typeParams, ps, returns, body, decorators, isAsync) { NameStart = Int() };
        }
    }
}
