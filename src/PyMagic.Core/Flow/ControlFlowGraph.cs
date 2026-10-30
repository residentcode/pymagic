using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Flow;

public enum InstrKind
{
    Entry,
    Exit,
    Statement,   // marks the start of a statement (used for reachability)
    Read,        // a name is read
    Write,       // a name is bound (assignment target, def, import, parameter, ...)
    Delete,      // a name is unbound (del, end of except block)
    Join,        // merge/loop head/handler entry with no effect of its own
    Return,
    Raise,
    /// <summary>A condition is known to be true or false from here on (narrowing of Symbol/Path).</summary>
    Assert,
    /// <summary>A call made as a statement (Element: the CallExpr); nothing flows on from it when its callee never returns (NoReturn).</summary>
    Call,
    /// <summary>
    /// Where an exception raised in a with block's body (Element: the WithStmt) would leave it; flow goes on from here
    /// only when a context manager swallows exceptions (contextlib.suppress).
    /// </summary>
    WithExit,
}

/// <summary>One step of evaluation. Instructions are linked in the order Python would execute them.</summary>
public sealed class Instruction
{
    public int Id { get; }
    public InstrKind Kind { get; }
    /// <summary>The syntax node this step stands for (NameExpr for reads/writes, the statement for markers).</summary>
    public Node? Element { get; }
    /// <summary>For Read/Write/Delete: the symbol involved (null for builtins/unresolved).</summary>
    public Symbol? Symbol { get; }
    /// <summary>For Write: the binding being established.</summary>
    public Declaration? Declaration { get; }
    /// <summary>
    /// Member path below Symbol for attribute chains ("x" for self.x, "a.b" for self.a.b); empty for the name itself.
    /// Name-level analyses (reaching definitions) ignore instructions with a path.
    /// </summary>
    public string Path { get; }
    /// <summary>For Assert: whether Element (the condition) is known to be true (else known to be false).</summary>
    public bool AssertPositive { get; }
    public List<Instruction> Successors { get; } = new();
    public List<Instruction> Predecessors { get; } = new();

    internal Instruction(int id, InstrKind kind, Node? element, Symbol? symbol, Declaration? decl, string path = "", bool positive = false)
    {
        Id = id; Kind = kind; Element = element; Symbol = symbol; Declaration = decl; Path = path; AssertPositive = positive;
    }

    public override string ToString() => $"#{Id} {Kind} {(Element is NameExpr n ? n.Name : Element?.GetType().Name)}";
}

/// <summary>Control-flow graph of one scope (module, class body, function, lambda or comprehension).</summary>
public sealed class ControlFlowGraph
{
    public Instruction Entry { get; }
    public Instruction Exit { get; }
    public IReadOnlyList<Instruction> Instructions { get; }
    /// <summary>Return statements, in source order, whether or not they are reachable.</summary>
    public IReadOnlyList<ReturnStmt> Returns { get; }
    /// <summary>True when execution can reach the end of the body without an explicit return.</summary>
    public bool EndReachable { get; }
    /// <summary>Per instruction id: reachable from Entry (as far as the graph knows without types, see <see cref="BlocksWithoutTypes"/>).</summary>
    public bool[] Reachable { get; }
    /// <summary>The instructions that run off the end of the body (dead code after a return among them).</summary>
    internal IReadOnlyList<Instruction> EndPoints { get; }
    /// <summary>The Call and WithExit instructions: what flows on from them depends on types.</summary>
    public IReadOnlyList<Instruction> Gates { get; }

    internal ControlFlowGraph(Instruction entry, Instruction exit, List<Instruction> instrs, List<ReturnStmt> returns, List<Instruction> endPoints)
    {
        Entry = entry; Exit = exit; Instructions = instrs; Returns = returns; EndPoints = endPoints;
        Gates = instrs.Where(i => i.Kind is InstrKind.Call or InstrKind.WithExit).ToList();
        Reachable = ReachableWith(BlocksWithoutTypes);
        EndReachable = EndReachableWith(Reachable, BlocksWithoutTypes);
    }

    /// <summary>Without types every call returns and no with block swallows an exception.</summary>
    public static bool BlocksWithoutTypes(Instruction i) => i.Kind == InstrKind.WithExit;

    /// <summary>Per instruction id: reachable from Entry when nothing flows on from the instructions <paramref name="blocks"/> picks.</summary>
    internal bool[] ReachableWith(Func<Instruction, bool> blocks)
    {
        var reachable = new bool[Instructions.Count];
        var stack = new Stack<Instruction>();
        reachable[Entry.Id] = true;
        stack.Push(Entry);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (Gates.Count > 0 && blocks(i)) continue;
            foreach (var s in i.Successors)
                if (!reachable[s.Id]) { reachable[s.Id] = true; stack.Push(s); }
        }
        return reachable;
    }

    internal bool EndReachableWith(bool[] reachable, Func<Instruction, bool> blocks) => EndPoints.Any(p => reachable[p.Id] && !blocks(p));
}
