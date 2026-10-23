using System.Reflection;

namespace PyMagic.Tests;

/// <summary>Marks a parameterless test method. Optional <see cref="CaseAttribute"/>s add parameter sets.</summary>
[AttributeUsage(AttributeTargets.Method)] public sealed class FactAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CaseAttribute(params object[] args) : Attribute { public object[] Args { get; } = args; }

public sealed class AssertionException(string m) : Exception(m) { }

public static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"expected: {expected}\n  actual: {actual}");
    }
    public static void True(bool c, string? msg = null) { if (!c) throw new AssertionException(msg ?? "expected true"); }
    public static void Contains(string needle, string hay)
    {
        if (!hay.Contains(needle)) throw new AssertionException($"'{needle}' not found in: {hay}");
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        int pass = 0, fail = 0;
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsClass && t.Name.EndsWith("Tests")))
        {
            var inst = Activator.CreateInstance(type)!;
            foreach (var m in type.GetMethods().Where(m => m.GetCustomAttribute<FactAttribute>() != null))
            {
                var cases = m.GetCustomAttributes<CaseAttribute>().Select(c => c.Args).ToList();
                if (cases.Count == 0) cases.Add([]);
                foreach (var a in cases)
                {
                    string name = $"{type.Name}.{m.Name}({string.Join(", ", a.Select(x => x?.ToString()?.Replace("\n", "\\n")))})";
                    try { m.Invoke(inst, a); pass++; }
                    catch (TargetInvocationException e)
                    {
                        fail++;
                        Console.WriteLine($"FAIL {name}\n  {e.InnerException?.Message}");
                    }
                }
            }
        }
        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
