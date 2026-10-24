using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>
    /// Whether a candidate of type <paramref name="actual"/> is a good fit where <paramref name="expected"/> is wanted. Stricter than
    /// <see cref="IsAssignable"/>, which says yes whenever either side is unknown: here nothing is a fit unless both are known and
    /// the expectation says something (not Any, not object).
    /// </summary>
    public bool Fits(PyType expected, PyType actual)
    {
        if (expected is UnknownType or NeverType || actual is UnknownType or NeverType) return false;
        if (ReferenceEquals(expected, PyType.Any)) return false;
        if (expected is ClassType { IsInstance: true, Info.Name: "object" }) return false;
        if (expected is TypeVarType || actual is TypeVarType) return false;
        return IsAssignable(expected, actual);
    }

    /// <summary>What calling a function (or the first overload of several) returns; Unknown for anything else.</summary>
    public PyType ReturnOf(PyType callable) => callable switch
    {
        FunctionType f => ReturnTypeOf(f),
        OverloadedType o when o.Overloads.Count > 0 => ReturnTypeOf(o.Overloads[0]),
        _ => PyType.Unknown,
    };

    /// <summary>The type an annotation expression stands for (an instance of the class it names, a union, ...).</summary>
    public PyType AnnotatedType(ModuleInfo m, Expr annotation) => AnnotationType(m, annotation);
}
