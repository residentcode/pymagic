using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    /// <summary>
    /// What a pytest fixture hands to the tests that ask for it: the type it returns, the type a generator fixture yields,
    /// or what an async fixture is awaited to. Unknown when nothing can be said.
    /// </summary>
    public PyType FixtureValueType(ModuleInfo m, FunctionDef def)
    {
        var info = FunctionOf(m, def);
        var ret = DeclaredReturn(info) ?? InferReturn(info);
        if (ret is ClassType { IsInstance: true } c)
        {
            switch (c.Info.Name)
            {
                case "Generator" or "Iterator" or "Iterable" or "AsyncGenerator" or "AsyncIterator" or "AsyncIterable" when c.Args.Count > 0:
                    return c.Args[0];
                case "Coroutine" or "Awaitable" when c.Args.Count > 0:
                    return c.Args[^1];
            }
        }
        return ret is NeverType ? PyType.Unknown : ret;
    }

    /// <summary>An instance of <paramref name="cls"/> from a loadable module, or null when the module or class cannot be found.</summary>
    public PyType? InstanceOf(string module, string cls, params PyType[] args)
    {
        var mod = _ws.GetModule(module);
        if (mod == null) return null;
        if (!_moduleClasses.TryGetValue(mod, out var perModule)) _moduleClasses[mod] = perModule = new();
        var c = ModuleClass(perModule, mod, cls);
        return c == null ? null : new ClassType(c.Info, args, true);
    }

    /// <summary>Instance of a builtin class such as str.</summary>
    public PyType BuiltinInstance(string name) => Inst(name);

    readonly Dictionary<ModuleInfo, Dictionary<string, ClassType?>> _moduleClasses = new(ReferenceEqualityComparer.Instance);
}
