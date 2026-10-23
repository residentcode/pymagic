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
    /// <summary>Per instruction id: reachable from Entry.</summary>
    public bool[] Reachable { get; }

    internal ControlFlowGraph(Instruction entry, Instruction exit, List<Instruction> instrs, List<ReturnStmt> returns, bool endReachable)
    {
        Entry = entry; Exit = exit; Instructions = instrs; Returns = returns; EndReachable = endReachable;
        Reachable = new bool[instrs.Count];
        var stack = new Stack<Instruction>();
        Reachable[entry.Id] = true;
        stack.Push(entry);
        while (stack.Count > 0)
            foreach (var s in stack.Pop().Successors)
                if (!Reachable[s.Id]) { Reachable[s.Id] = true; stack.Push(s); }
    }
}
