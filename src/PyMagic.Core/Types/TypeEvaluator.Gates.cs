using PyMagic.Core.Flow;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>
/// What types say about the gates of a control-flow graph (built without types): a call statement whose callee is
/// declared never to return ends the flow like raise, and a with block whose context manager swallows exceptions can
/// end at the with. The graph is shared by every workspace that holds the module, so the answers live here.
/// </summary>
public sealed partial class TypeEvaluator
{
    readonly Dictionary<Instruction, bool> _gates = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<Instruction> _gatesActive = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ScopeFlow, ScopeFlow> _typedFlows = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<ScopeFlow> _typedFlowsActive = new(ReferenceEqualityComparer.Instance);

    /// <summary>Unreachable code and locals read before assignment, knowing which calls never return and which with blocks swallow exceptions.</summary>
    public IReadOnlyList<Diagnostic> FlowDiagnostics(ModuleInfo m) => m.Flow.Analyse(s => TypedFlow(m, s));

    /// <summary>Whether a function can run off the end of its body, knowing which calls never return and which with blocks swallow exceptions.</summary>
    public bool EndReachable(ModuleInfo m, FunctionDef f) =>
        m.Semantics.ScopeOf(f) is not { } scope || (TypedFlow(m, scope)?.EndReachable ?? true);

    /// <summary>
    /// The flow of a scope with every gate it reaches decided by types; the plain flow when none differs from what the
    /// graph assumes without types, or while this scope's gates are being decided.
    /// </summary>
    internal ScopeFlow? TypedFlow(ModuleInfo m, Scope scope)
    {
        var flow = m.Flow.For(scope);
        if (flow == null || flow.Graph.Gates.Count == 0) return flow;
        if (_typedFlows.TryGetValue(flow, out var typed)) return typed;
        if (!_typedFlowsActive.Add(flow)) return flow;
        try
        {
            // a gate decides which gates after it are reached: decide those reached so far until no new one is
            var decided = new Dictionary<Instruction, bool>(ReferenceEqualityComparer.Instance);
            bool Blocks(Instruction i) => decided.TryGetValue(i, out var b) ? b : ControlFlowGraph.BlocksWithoutTypes(i);
            var reachable = flow.Graph.Reachable;
            while (true)
            {
                bool more = false;
                foreach (var g in flow.Graph.Gates)
                    if (reachable[g.Id] && !decided.ContainsKey(g)) { decided[g] = GateBlocks(m, g); more = true; }
                if (!more) break;
                reachable = flow.Graph.ReachableWith(Blocks);
            }
            typed = decided.All(kv => kv.Value == ControlFlowGraph.BlocksWithoutTypes(kv.Key)) ? flow : flow.WithGates(Blocks);
        }
        finally { _typedFlowsActive.Remove(flow); }
        if (_open.Count == 0) _typedFlows[flow] = typed;
        return typed;
    }

    /// <summary>How far the gates of a module or class body are decided, in the order they are written.</summary>
    sealed class BodyGates
    {
        public required List<Instruction> Gates;              // by id, which is the order they are written
        public int Decided;                                   // Gates[..Decided] are decided
        public List<Instruction> Differ = new();              // the decided ones types turn the other way, in order
        public List<ScopeFlow> Flows = new();                 // Flows[k]: the flow with Differ[..k] turned
    }
    readonly Dictionary<ScopeFlow, BodyGates> _bodyGates = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The definitions that reach a read in a module or class body past the calls before it that never return. The gates of a
    /// body are its top-level calls, and a read there is often made while something else is being worked out (a class's base
    /// while the class is built, a ParamSpec in a decorator's annotation while the decorated def is): deciding every gate of
    /// the body then, as TypedFlow does, meets that thing half done further down (`FullConstructor.add_constructor(...)` in
    /// PyYAML's constructor.py after `class FullConstructor(SafeConstructor)`), and some answers are kept that way. Only the
    /// gates written before the read can end the paths to it (a gate later in a loop is left as the graph has it), so only
    /// those are decided; the whole typed flow is taken when it is known.
    /// </summary>
    internal ReachingResult? BodyReachingFor(ModuleInfo m, Scope scope, NameExpr n)
    {
        var flow = m.Flow.For(scope);
        if (flow == null) return null;
        if (flow.Graph.Gates.Count == 0) return flow.ReachingFor(n);
        if (_typedFlows.TryGetValue(flow, out var whole)) return whole.ReachingFor(n);
        // while a class's bases or MRO are being worked out they are kept without a cycle mark: decide nothing then
        if (_classDataActive.Count > 0 || _mroActive.Count > 0 || flow.ReadInstructions(n) is not { Count: > 0 } reads) return flow.ReachingFor(n);
        int upTo = reads.Max(i => i.Id);

        if (!_bodyGates.TryGetValue(flow, out var known))
            known = new BodyGates { Gates = flow.Graph.Gates.OrderBy(g => g.Id).ToList(), Flows = { flow } };
        if (known.Decided < known.Gates.Count && known.Gates[known.Decided].Id < upTo)
        {
            if (!_typedFlowsActive.Add(flow)) return flow.ReachingFor(n);
            // decided on a copy: an answer that leaned on a cut cycle holds for this read only
            var next = new BodyGates { Gates = known.Gates, Decided = known.Decided, Differ = known.Differ.ToList(), Flows = known.Flows.ToList() };
            try
            {
                while (next.Decided < next.Gates.Count && next.Gates[next.Decided].Id < upTo)
                {
                    var g = next.Gates[next.Decided++];
                    // a gate after one that ends the flow is not reached, and stays as the graph has it
                    if (!next.Flows[^1].Reachable[g.Id] || GateBlocks(m, g) == ControlFlowGraph.BlocksWithoutTypes(g)) continue;
                    next.Differ.Add(g);
                    var differ = new HashSet<Instruction>(next.Differ, ReferenceEqualityComparer.Instance);
                    next.Flows.Add(flow.WithGates(i => differ.Contains(i) != ControlFlowGraph.BlocksWithoutTypes(i)));
                }
            }
            finally { _typedFlowsActive.Remove(flow); }
            if (_open.Count == 0) _bodyGates[flow] = next;
            known = next;
        }
        else _bodyGates.TryAdd(flow, known);
        int turned = 0;
        while (turned < known.Differ.Count && known.Differ[turned].Id < upTo) turned++;
        return known.Flows[turned].ReachingFor(n);
    }

    /// <summary>Whether nothing flows on from a gate: a call that never returns, or a with block none of whose context managers swallows exceptions.</summary>
    internal bool GateBlocks(ModuleInfo m, Instruction gate)
    {
        if (_gates.TryGetValue(gate, out var known)) return known;
        if (!_gatesActive.Add(gate)) return ControlFlowGraph.BlocksWithoutTypes(gate);
        bool blocks;
        try
        {
            blocks = gate.Element switch
            {
                CallExpr call => NeverReturns(TypeOf(m, call.Func), 0),
                WithStmt w => !w.Items.Any(i => SwallowsExceptions(TypeOf(m, i.Context), w.IsAsync)),
                _ => ControlFlowGraph.BlocksWithoutTypes(gate),
            };
        }
        finally { _gatesActive.Remove(gate); }
        // an answer that leaned on a cut cycle may be wrong: work it out again next time
        if (_open.Count == 0) _gates[gate] = blocks;
        return blocks;
    }

    /// <summary>
    /// A callee that returns NoReturn / Never in every form it can have (sys.exit, os._exit, typing.assert_never, a def annotated
    /// so, an object whose __call__ is), or a project def without a return annotation whose every path raises, as pyright infers
    /// it. A def that raises NotImplementedError, an abstract method or an overload stands for an implementation elsewhere, and
    /// does not count.
    /// </summary>
    bool NeverReturns(PyType callee, int depth)
    {
        if (callee is NeverType || depth > 1) return false;
        foreach (var c in callee.Members)
        {
            bool never = c switch
            {
                FunctionType f => (DeclaredReturn(f.Info) ?? InferredNoReturn(f.Info)) is NeverType,
                OverloadedType o => o.Overloads.All(f => DeclaredReturn(f.Info) is NeverType),
                ClassType { IsInstance: true } inst => NeverReturns(AttributeType(inst, "__call__").Type, depth + 1),
                _ => false,
            };
            if (!never) return false;
        }
        return true;
    }

    /// <summary>What an unannotated def's body gives when it may be taken as never returning: Never when it only raises; null otherwise.</summary>
    PyType? InferredNoReturn(FunctionInfo info)
    {
        if (info.Node is not FunctionDef { Returns: null } || info.Module.IsStub) return null;
        if ((info.Flags & (FuncFlags.Abstract | FuncFlags.Overload | FuncFlags.Async | FuncFlags.Generator | FuncFlags.Property)) != 0) return null;
        // InferReturn leaves a body that raises NotImplementedError unknown
        return InferReturn(info);
    }

    /// <summary>
    /// A context manager whose __exit__ (__aexit__ for async with) is declared to return bool, so it may swallow the exception
    /// (contextlib.suppress). `bool | None`, as typeshed's AbstractContextManager declares, and None do not count, as in pyright.
    /// </summary>
    bool SwallowsExceptions(PyType manager, bool isAsync)
    {
        foreach (var c in manager.Members)
        {
            if (c is not ClassType { IsInstance: true } inst) continue;
            var exit = AttributeType(inst, isAsync ? "__aexit__" : "__exit__").Type;
            foreach (var f in exit.Members.OfType<FunctionType>())
            {
                if (f.Info.Node is not FunctionDef { Returns: { } annotation } def) continue;
                var r = AnnotationType(f.Info.Module, annotation);
                // `def __aexit__(...) -> Awaitable[bool]`: what awaiting it gives
                if (isAsync && !def.IsAsync && r is ClassType { Args.Count: > 0 } aw && aw.Info.Name is "Awaitable" or "Coroutine")
                    r = aw.Args[^1];
                if (r is ClassType { IsInstance: true, Info.Name: "bool", LiteralValue: null or true } b && b.Info.Module.Name == "builtins") return true;
            }
        }
        return false;
    }
}
