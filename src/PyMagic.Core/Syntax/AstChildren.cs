namespace PyMagic.Core.Syntax;

/// <summary>Enumerates the direct child nodes of any AST node, in source order.</summary>
public static class AstChildren
{
    [ThreadStatic] static List<Node>? _scratch;

    public static Node[] Of(Node node)
    {
        var list = _scratch ??= new List<Node>();
        list.Clear();
        AddTo(node, list);
        return list.Count == 0 ? Array.Empty<Node>() : list.ToArray();
    }

    static void Add<T>(List<Node> into, IReadOnlyList<T> items) where T : Node
    {
        for (int i = 0; i < items.Count; i++) into.Add(items[i]);
    }

    /// <summary>Adds the direct child nodes of the node to the list, in source order (what <see cref="Of"/> gives, without making an enumerator per node).</summary>
    public static void AddTo(Node node, List<Node> into)
    {
        switch (node)
        {
            // --- expressions
            case NameExpr or ErrorExpr: break;
            case ConstExpr x: Add(into, x.Parts); break;
            case UnaryExpr x: into.Add(x.Operand); break;
            case BinaryExpr x: into.Add(x.Left); into.Add(x.Right); break;
            case CompareExpr x:
                into.Add(x.Left);
                Add(into, x.Comparators);
                break;
            case CallExpr x:
                into.Add(x.Func);
                Add(into, x.Args);
                break;
            case Argument x: into.Add(x.Value); break;
            case AttributeExpr x: into.Add(x.Value); break;
            case SubscriptExpr x: into.Add(x.Value); into.Add(x.Index); break;
            case SliceExpr x:
                if (x.Lower != null) into.Add(x.Lower);
                if (x.Upper != null) into.Add(x.Upper);
                if (x.Step != null) into.Add(x.Step);
                break;
            case TupleExpr x: Add(into, x.Items); break;
            case ListExpr x: Add(into, x.Items); break;
            case SetExpr x: Add(into, x.Items); break;
            case DictExpr x:
                for (int i = 0; i < x.Values.Count; i++)
                {
                    if (x.Keys[i] != null) into.Add(x.Keys[i]!);
                    into.Add(x.Values[i]);
                }
                break;
            case Comprehension x:
                into.Add(x.Target); into.Add(x.Iter);
                Add(into, x.Ifs);
                break;
            case CompExpr x:
                into.Add(x.Element);
                if (x.Value != null) into.Add(x.Value);
                Add(into, x.Generators);
                break;
            case LambdaExpr x:
                Add(into, x.Params);
                into.Add(x.Body);
                break;
            case IfExpr x: into.Add(x.Body); into.Add(x.Test); into.Add(x.Orelse); break;
            case StarExpr x: into.Add(x.Value); break;
            case AwaitExpr x: into.Add(x.Value); break;
            case YieldExpr x: if (x.Value != null) into.Add(x.Value); break;
            case NamedExpr x: into.Add(x.Target); into.Add(x.Value); break;

            // --- parameters / type params
            case Parameter x:
                if (x.Annotation != null) into.Add(x.Annotation);
                if (x.Default != null) into.Add(x.Default);
                break;
            case TypeParam x:
                if (x.Bound != null) into.Add(x.Bound);
                if (x.Default != null) into.Add(x.Default);
                break;

            // --- patterns
            case MatchValue x: into.Add(x.Value); break;
            case MatchCapture or MatchStar: break;
            case MatchAs x: into.Add(x.Pattern); break;
            case MatchOr x: Add(into, x.Alternatives); break;
            case MatchSequence x: Add(into, x.Items); break;
            case MatchMapping x:
                for (int i = 0; i < x.Keys.Count; i++) { into.Add(x.Keys[i]); into.Add(x.Patterns[i]); }
                break;
            case MatchClass x:
                into.Add(x.Cls);
                Add(into, x.Positional);
                Add(into, x.KeywordPatterns);
                break;
            case MatchCase x:
                into.Add(x.Pattern);
                if (x.Guard != null) into.Add(x.Guard);
                Add(into, x.Body);
                break;

            // --- statements
            case ExprStmt x: into.Add(x.Value); break;
            case AssignStmt x:
                Add(into, x.Targets);
                into.Add(x.Value);
                break;
            case AugAssignStmt x: into.Add(x.Target); into.Add(x.Value); break;
            case AnnAssignStmt x:
                into.Add(x.Target); into.Add(x.Annotation);
                if (x.Value != null) into.Add(x.Value);
                break;
            case ReturnStmt x: if (x.Value != null) into.Add(x.Value); break;
            case RaiseStmt x:
                if (x.Exc != null) into.Add(x.Exc);
                if (x.Cause != null) into.Add(x.Cause);
                break;
            case DelStmt x: Add(into, x.Targets); break;
            case AssertStmt x:
                into.Add(x.Test);
                if (x.Message != null) into.Add(x.Message);
                break;
            case ImportStmt x: Add(into, x.Names); break;
            case ImportFromStmt x: Add(into, x.Names); break;
            case IfStmt x:
                into.Add(x.Test);
                Add(into, x.Body);
                Add(into, x.Orelse);
                break;
            case WhileStmt x:
                into.Add(x.Test);
                Add(into, x.Body);
                Add(into, x.Orelse);
                break;
            case ForStmt x:
                into.Add(x.Target); into.Add(x.Iter);
                Add(into, x.Body);
                Add(into, x.Orelse);
                break;
            case ExceptHandler x:
                if (x.Type != null) into.Add(x.Type);
                Add(into, x.Body);
                break;
            case TryStmt x:
                Add(into, x.Body);
                Add(into, x.Handlers);
                Add(into, x.Orelse);
                Add(into, x.Finally);
                break;
            case WithItem x:
                into.Add(x.Context);
                if (x.Target != null) into.Add(x.Target);
                break;
            case WithStmt x:
                Add(into, x.Items);
                Add(into, x.Body);
                break;
            case FunctionDef x:
                Add(into, x.Decorators);
                Add(into, x.TypeParams);
                Add(into, x.Params);
                if (x.Returns != null) into.Add(x.Returns);
                Add(into, x.Body);
                break;
            case ClassDef x:
                Add(into, x.Decorators);
                Add(into, x.TypeParams);
                Add(into, x.Bases);
                Add(into, x.Body);
                break;
            case MatchStmt x:
                into.Add(x.Subject);
                Add(into, x.Cases);
                break;
            case TypeAliasStmt x:
                Add(into, x.TypeParams);
                into.Add(x.Value);
                break;
            case Module x: Add(into, x.Body); break;
            // Pass, Break, Continue, Global, Nonlocal, ImportAlias, MatchCapture: leaves
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Module, Node[]> Flattened = new();

    /// <summary>
    /// Pre-order walk of a whole subtree. The walk of a whole module is made once and kept with the module (the tree never changes), because the
    /// inspections, the fixes and the project-wide searches each walk the same module again and again.
    /// </summary>
    public static IEnumerable<Node> Descendants(Node root) =>
        root is Module module ? Flattened.GetValue(module, m => Walk(m).ToArray()) : Walk(root);

    static IEnumerable<Node> Walk(Node root)
    {
        var stack = new Stack<Node>();
        var kids = new List<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            kids.Clear();
            AddTo(n, kids);
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }
}
