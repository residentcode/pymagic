using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>
    /// What FastAPI's `Depends(dependency)` hands to the parameter: the value a dependency function returns (what a generator
    /// dependency yields, what an async one is awaited to), or an instance of a class used as a dependency. Unknown when nothing can be said.
    /// </summary>
    public PyType DependencyValueType(ModuleInfo m, Expr dependency)
    {
        switch (TypeOf(m, dependency))
        {
            case FunctionType { Info.Node: FunctionDef def } f:
                return FixtureValueType(f.Info.Module, def);
            case ClassType { IsInstance: false } c:
                return new ClassType(c.Info, c.Args, true);
            default:
                return PyType.Unknown;
        }
    }
}
