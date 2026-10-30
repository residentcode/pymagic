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
    /// A callee that is declared to return NoReturn / Never in every form it can have (sys.exit, os._exit, typing.assert_never,
    /// a def annotated so, an object whose __call__ is). Only declarations count: a def that happens to raise on every path may
    /// be overridden or replaced, so a call of it is not taken as the end.
    /// </summary>
    bool NeverReturns(PyType callee, int depth)
    {
        if (callee is NeverType || depth > 1) return false;
        foreach (var c in callee.Members)
        {
            bool never = c switch
            {
                FunctionType f => DeclaredReturn(f.Info) is NeverType,
                OverloadedType o => o.Overloads.All(f => DeclaredReturn(f.Info) is NeverType),
                ClassType { IsInstance: true } inst => NeverReturns(AttributeType(inst, "__call__").Type, depth + 1),
                _ => false,
            };
            if (!never) return false;
        }
        return true;
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
