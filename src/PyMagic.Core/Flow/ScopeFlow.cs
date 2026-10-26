using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Flow;

/// <summary>Which bindings can be live at a particular use of a name.</summary>
/// <param name="Definitions">Bindings that may have produced the value seen here.</param>
/// <param name="MaybeUnbound">The name may still be unassigned (or deleted) on some path to this use.</param>
/// <param name="Reachable">False when the use is in unreachable code.</param>
public sealed record ReachingResult(IReadOnlyList<Declaration> Definitions, bool MaybeUnbound, bool Reachable);

/// <summary>Minimal growable bit set (System.Collections.BitArray allocates on every operation).</summary>
sealed class Bits
{
    readonly ulong[] _w;
    public Bits(int n) { _w = new ulong[(n + 63) >> 6]; }
    Bits(ulong[] w) { _w = w; }
    public Bits Clone() => new((ulong[])_w.Clone());
    public bool Get(int i) => (_w[i >> 6] & (1UL << (i & 63))) != 0;
    public void Set(int i) => _w[i >> 6] |= 1UL << (i & 63);
    public void AndNot(Bits o) { for (int k = 0; k < _w.Length; k++) _w[k] &= ~o._w[k]; }
    /// <summary>this |= o; returns true when this changed.</summary>
    public bool Or(Bits o)
    {
        bool changed = false;
        for (int k = 0; k < _w.Length; k++)
        {
            ulong n = _w[k] | o._w[k];
            if (n != _w[k]) { _w[k] = n; changed = true; }
        }
        return changed;
    }
}

/// <summary>
/// The control-flow graph of one scope together with a forward "reaching definitions" analysis over the symbols
/// that scope owns. Every owned symbol gets one pseudo-definition meaning "unbound" so that uses which can run
/// before any assignment are detected.
/// </summary>
public sealed class ScopeFlow
{
    public Scope Scope { get; }
    public ControlFlowGraph Graph { get; }

    readonly Dictionary<NameExpr, List<Instruction>> _reads = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<NameExpr, List<Instruction>> _allReads = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<CompExpr, List<Instruction>> _markers = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<(Symbol, string)> _assertionKeys = new();
    readonly Dictionary<Symbol, int> _unboundId = new();
    readonly Dictionary<Symbol, Bits> _symbolMask = new();
    readonly List<(Symbol Sym, Declaration? Decl)> _defs = new(); // index = def id; Decl null = unbound
    readonly Dictionary<(Symbol, Declaration), int> _defId = new();
    Bits[] _out = null!;
    Bits[] _in = null!;

    internal ScopeFlow(SemanticModel model, Scope scope, IReadOnlyDictionary<Node, (Symbol, Declaration)> decls)
    {
        Scope = scope;
        Graph = new CfgBuilder(model, scope, decls).Build();
        Analyse();
    }

    bool Owned(Symbol? s) => s != null && ReferenceEquals(s.Scope, Scope);

    void Analyse()
    {
        // 1. number the definitions of every owned symbol that is touched in this graph
        var owned = new List<Symbol>();
        foreach (var ins in Graph.Instructions)
        {
            if (ins.Kind == InstrKind.Read && ins.Element is NameExpr rn)
            {
                if (!_allReads.TryGetValue(rn, out var all)) _allReads[rn] = all = new();
                all.Add(ins);
            }
            if (ins.Kind == InstrKind.Join && ins.Element is CompExpr ce)
            {
                if (!_markers.TryGetValue(ce, out var ms)) _markers[ce] = ms = new();
                ms.Add(ins);
            }
            if (ins.Kind == InstrKind.Assert && ins.Symbol != null) _assertionKeys.Add((ins.Symbol, ins.Path));
            if (ins.Kind is not (InstrKind.Read or InstrKind.Write or InstrKind.Delete) || !Owned(ins.Symbol) || ins.Path.Length > 0) continue;
            var s = ins.Symbol!;
            if (!_unboundId.ContainsKey(s))
            {
                _unboundId[s] = _defs.Count;
                _defs.Add((s, null));
                owned.Add(s);
            }
            if (ins.Kind == InstrKind.Write && ins.Declaration != null && !_defId.ContainsKey((s, ins.Declaration)))
            {
                _defId[(s, ins.Declaration)] = _defs.Count;
                _defs.Add((s, ins.Declaration));
            }
            if (ins.Kind == InstrKind.Read && ins.Element is NameExpr n)
            {
                if (!_reads.TryGetValue(n, out var list)) _reads[n] = list = new();
                list.Add(ins);
            }
        }

        int total = Math.Max(_defs.Count, 1);
        foreach (var s in owned) _symbolMask[s] = new Bits(total);
        for (int i = 0; i < _defs.Count; i++) _symbolMask[_defs[i].Sym].Set(i);

        // 2. worklist dataflow:  OUT = (IN - kill) + gen,  IN = union of predecessors' OUT
        int n2 = Graph.Instructions.Count;
        _in = new Bits[n2];
        _out = new Bits[n2];
        for (int i = 0; i < n2; i++) { _in[i] = new Bits(total); _out[i] = new Bits(total); }

        var entryOut = new Bits(total);
        foreach (var s in owned) entryOut.Set(_unboundId[s]);
        _out[Graph.Entry.Id] = entryOut;

        var queue = new Queue<Instruction>();
        var queued = new bool[n2];
        foreach (var s in Graph.Entry.Successors) { queue.Enqueue(s); queued[s.Id] = true; }

        while (queue.Count > 0)
        {
            var ins = queue.Dequeue();
            queued[ins.Id] = false;

            var inSet = new Bits(total);
            foreach (var p in ins.Predecessors) inSet.Or(_out[p.Id]);
            _in[ins.Id] = inSet;

            var outSet = inSet;
            if (Owned(ins.Symbol) && ins.Kind is InstrKind.Write or InstrKind.Delete && ins.Path.Length == 0)
            {
                var sym = ins.Symbol!;
                outSet = inSet.Clone();
                outSet.AndNot(_symbolMask[sym]);
                if (ins.Kind == InstrKind.Write && ins.Declaration != null) outSet.Set(_defId[(sym, ins.Declaration)]);
                else outSet.Set(_unboundId[sym]);
            }

            if (_out[ins.Id].Or(outSet))
                foreach (var s in ins.Successors)
                    if (!queued[s.Id]) { queued[s.Id] = true; queue.Enqueue(s); }
        }
    }

    /// <summary>Every Read instruction of this name expression (a finally block is built twice, so there can be two).</summary>
    public IReadOnlyList<Instruction>? ReadInstructions(NameExpr use) => _allReads.GetValueOrDefault(use);

    /// <summary>The point where a comprehension written in this scope starts, after its first iterable (two in a finally block).</summary>
    public IReadOnlyList<Instruction>? MarkerOf(CompExpr c) => _markers.GetValueOrDefault(c);

    /// <summary>Whether a condition somewhere in this scope narrows the given reference (symbol and member path).</summary>
    public bool HasAssertions(Symbol sym, string path) => _assertionKeys.Contains((sym, path));

    /// <summary>Reaching definitions for a read of a name in this scope; null if the name is not owned by this scope.</summary>
    public ReachingResult? ReachingFor(NameExpr use)
    {
        if (!_reads.TryGetValue(use, out var instrs)) return null;
        var defs = new List<Declaration>();
        bool maybeUnbound = false, reachable = false;
        foreach (var ins in instrs)
        {
            if (!Graph.Reachable[ins.Id]) continue;
            reachable = true;
            var sym = ins.Symbol!;
            var inSet = _in[ins.Id];
            if (inSet.Get(_unboundId[sym])) maybeUnbound = true;
            foreach (var (d, id) in DefsOf(sym))
                if (inSet.Get(id) && !defs.Contains(d, ReferenceEqualityComparer.Instance)) defs.Add(d);
        }
        return new ReachingResult(defs, maybeUnbound, reachable);
    }

    IEnumerable<(Declaration, int)> DefsOf(Symbol sym)
    {
        for (int i = 0; i < _defs.Count; i++)
            if (ReferenceEquals(_defs[i].Sym, sym) && _defs[i].Decl != null) yield return (_defs[i].Decl!, i);
    }

    /// <summary>
    /// First statement of each run of unreachable code. Instructions are numbered in execution-text order, so a run
    /// (including statements nested in it) lasts until the next reachable instruction.
    /// </summary>
    public IEnumerable<Stmt> UnreachableStatements()
    {
        // A finally block exists twice in the graph (normal and exceptional path); the statement is live if either copy is.
        var live = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var ins in Graph.Instructions)
            if (ins.Kind == InstrKind.Statement && Graph.Reachable[ins.Id]) live.Add(ins.Element!);

        bool inRun = false;
        foreach (var ins in Graph.Instructions)
        {
            if (Graph.Reachable[ins.Id]) { inRun = false; continue; }
            if (ins.Kind != InstrKind.Statement || inRun || live.Contains(ins.Element!)) continue;
            inRun = true;
            yield return (Stmt)ins.Element!;
        }
    }

    /// <summary>Uses of owned locals that can happen before assignment, with a flag for "definitely".</summary>
    public IEnumerable<(NameExpr Use, bool Definitely)> UnboundUses()
    {
        foreach (var (n, instrs) in _reads)
        {
            var r = ReachingFor(n);
            if (r is { Reachable: true, MaybeUnbound: true }) yield return (n, r.Definitions.Count == 0);
        }
    }
}
