using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>
    /// Type-based findings for a module: calls whose arguments do not fit the callee, and attributes that do not
    /// exist on a fully known receiver. Conservative by design: anything involving unknown types is silent.
    /// </summary>
    /// <param name="includeTypeMismatches">Also report argument type mismatches (noisy until real typeshed is used).</param>
    /// <param name="includeOptionalMisses">Also report attributes missing on None (e.g. an attribute initialised to None in __init__ and used elsewhere).</param>
    public IReadOnlyList<Diagnostic> Inspect(ModuleInfo m, bool includeTypeMismatches = false, bool includeOptionalMisses = false)
    {
        var diags = new List<Diagnostic>();
        if (m.IsStub) return diags;

        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case CallExpr call:
                    InspectCall(m, call, includeTypeMismatches, diags);
                    break;
                case AttributeExpr attr when !IsStoreTarget(m, attr):
                    InspectAttribute(m, attr, diags, includeOptionalMisses);
                    break;
            }
        }
        return diags;
    }

    void InspectCall(ModuleInfo m, CallExpr call, bool includeTypes, List<Diagnostic> diags)
    {
        var res = ResolveCall(m, call);
        if (res.Targets.Count == 0) return;
        // a union callee is only wrong if every alternative is wrong
        // (a None callee is not reported: without narrowing, `if f is not None: f()` would be a false alarm)
        var problemsPerTarget = res.Targets
            .Select(t => IsNone(t.Callee) ? new List<CallProblem>()
                : t.Problems.Where(p => includeTypes || p.Kind != CallProblemKind.Type).ToList()).ToList();
        if (problemsPerTarget.Any(p => p.Count == 0)) return;

        foreach (var p in problemsPerTarget[0])
        {
            var at = p.At ?? call;
            int start = at.Start, len = Math.Max(at.End - at.Start, 1);
            if (ReferenceEquals(at, call)) { start = call.Func.Start; len = Math.Max(call.Func.End - call.Func.Start, 1); }
            diags.Add(new Diagnostic(start, len, p.Message));
        }
    }

    void InspectAttribute(ModuleInfo m, AttributeExpr attr, List<Diagnostic> diags, bool includeNone)
    {
        var recv = TypeOf(m, attr.Value);
        if (recv is UnknownType) return;
        var res = AttributeType(recv, attr.Attr);
        var missing = res.Missing.Where(x => IsReportableMiss(x, attr.Attr, includeNone)).ToList();
        if (missing.Count == 0) return;
        int start = attr.End - attr.Attr.Length;
        diags.Add(new Diagnostic(start, attr.Attr.Length,
            $"Cannot find reference '{attr.Attr}' in '{PyType.Union(missing)}'"));
    }

    readonly Dictionary<ModuleInfo, bool> _dynamicNamespace = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Misses we are not (yet) confident about are not reported:
    /// None (needs narrowing: `if x is not None`), classes from the minimal bundled stubs (known gaps until real
    /// typeshed is used) and modules that build their namespace at runtime.
    /// </summary>
    bool IsReportableMiss(PyType t, string attribute, bool includeNone)
    {
        // stubs describe the public interface only: a single-underscore name missing from one is not evidence
        bool privateName = attribute.StartsWith('_') && !(attribute.StartsWith("__") && attribute.EndsWith("__"));
        if (privateName && t is ClassType { Info.Module.IsStub: true } or ModuleType { Module.IsStub: true }) return false;
        switch (t)
        {
            case ClassType c when IsNone(c): return includeNone;
            case ClassType c: return !c.Info.Module.IsBundledStub;
            case ModuleType mt:
                if (!_dynamicNamespace.TryGetValue(mt.Module, out var dyn))
                {
                    var s = mt.Module.Source;
                    _dynamicNamespace[mt.Module] = dyn = s.Contains("globals()") || s.Contains("__dict__") || s.Contains("setattr(") || s.Contains("__getattr__");
                }
                return !dyn && !mt.Module.IsBundledStub;
            default: return true;
        }
    }

    /// <summary>True when the attribute expression is the target of an assignment/del, i.e. it defines rather than reads.</summary>
    static bool IsStoreTarget(ModuleInfo m, Expr e)
    {
        Node child = e;
        var p = m.ParentOf(e);
        while (p is TupleExpr or ListExpr or StarExpr) { child = p; p = m.ParentOf(p); }
        return p switch
        {
            AssignStmt a => a.Targets.Any(t => ReferenceEquals(t, child)),
            AugAssignStmt a => ReferenceEquals(a.Target, child),
            AnnAssignStmt a => ReferenceEquals(a.Target, child),
            ForStmt f => ReferenceEquals(f.Target, child),
            Comprehension c => ReferenceEquals(c.Target, child),
            WithItem w => ReferenceEquals(w.Target, child),
            DelStmt => true,
            _ => false,
        };
    }
}
