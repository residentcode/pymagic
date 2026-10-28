namespace PyMagic.Core.Types;

/// <summary>One way of calling something: the parameters it takes and what it returns (null for a class, whose call makes an instance).</summary>
/// <param name="Function">The def the parameters were read from, when there is one (to show its docstring).</param>
public sealed record CallSignature(IReadOnlyList<ParamInfo> Params, PyType? Returns, FunctionInfo? Function = null);

public sealed partial class TypeEvaluator
{
    /// <summary>
    /// What calling <paramref name="callee"/> takes, one signature for each way of calling it: a function, a bound method (without its first
    /// parameter), a class (its constructor), an object with `__call__`, a callable type, and each overload of an overloaded function.
    /// Empty when nothing is known about it. Read-only: it adds no knowledge to the evaluator, it only shows what call resolution already uses.
    /// </summary>
    public IReadOnlyList<CallSignature> CallSignatures(PyType callee)
    {
        var found = new List<CallSignature>();
        void Add(PyType t)
        {
            switch (t)
            {
                case OverloadedType o:
                    foreach (var f in o.Overloads) Add(f);
                    break;
                case FunctionType f:
                    found.Add(new CallSignature(CallableParameters(f), ReturnTypeOf(f), f.Info));
                    break;
                case SignatureType { Params: { } ps } s:
                    found.Add(new CallSignature(ps, s.Origin == null ? s.Return : null, s.Origin?.Info));
                    break;
                default:
                    if (ParametersOfValue(t) is { Params: { } ps2 }) found.Add(new CallSignature(ps2, null));
                    break;
            }
        }
        Add(callee);
        return found;
    }
}
