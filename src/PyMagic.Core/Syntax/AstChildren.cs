namespace PyMagic.Core.Syntax;

/// <summary>Enumerates the direct child nodes of any AST node, in source order.</summary>
public static class AstChildren
{
    public static IEnumerable<Node> Of(Node node)
    {
        switch (node)
        {
            // --- expressions
            case NameExpr or ConstExpr or ErrorExpr: break;
            case UnaryExpr x: yield return x.Operand; break;
            case BinaryExpr x: yield return x.Left; yield return x.Right; break;
            case CompareExpr x:
                yield return x.Left;
                foreach (var c in x.Comparators) yield return c;
                break;
            case CallExpr x:
                yield return x.Func;
                foreach (var a in x.Args) yield return a;
                break;
            case Argument x: yield return x.Value; break;
            case AttributeExpr x: yield return x.Value; break;
            case SubscriptExpr x: yield return x.Value; yield return x.Index; break;
            case SliceExpr x:
                if (x.Lower != null) yield return x.Lower;
                if (x.Upper != null) yield return x.Upper;
                if (x.Step != null) yield return x.Step;
                break;
            case TupleExpr x: foreach (var i in x.Items) yield return i; break;
            case ListExpr x: foreach (var i in x.Items) yield return i; break;
            case SetExpr x: foreach (var i in x.Items) yield return i; break;
            case DictExpr x:
                for (int i = 0; i < x.Values.Count; i++)
                {
                    if (x.Keys[i] != null) yield return x.Keys[i]!;
                    yield return x.Values[i];
                }
                break;
            case Comprehension x:
                yield return x.Target; yield return x.Iter;
                foreach (var i in x.Ifs) yield return i;
                break;
            case CompExpr x:
                yield return x.Element;
                if (x.Value != null) yield return x.Value;
                foreach (var g in x.Generators) yield return g;
                break;
            case LambdaExpr x:
                foreach (var p in x.Params) yield return p;
                yield return x.Body;
                break;
            case IfExpr x: yield return x.Body; yield return x.Test; yield return x.Orelse; break;
            case StarExpr x: yield return x.Value; break;
            case AwaitExpr x: yield return x.Value; break;
            case YieldExpr x: if (x.Value != null) yield return x.Value; break;
            case NamedExpr x: yield return x.Target; yield return x.Value; break;

            // --- parameters / type params
            case Parameter x:
                if (x.Annotation != null) yield return x.Annotation;
                if (x.Default != null) yield return x.Default;
                break;
            case TypeParam x:
                if (x.Bound != null) yield return x.Bound;
                if (x.Default != null) yield return x.Default;
                break;

            // --- patterns
            case MatchValue x: yield return x.Value; break;
            case MatchCapture or MatchStar: break;
            case MatchAs x: yield return x.Pattern; break;
            case MatchOr x: foreach (var p in x.Alternatives) yield return p; break;
            case MatchSequence x: foreach (var p in x.Items) yield return p; break;
            case MatchMapping x:
                for (int i = 0; i < x.Keys.Count; i++) { yield return x.Keys[i]; yield return x.Patterns[i]; }
                break;
            case MatchClass x:
                yield return x.Cls;
                foreach (var p in x.Positional) yield return p;
                foreach (var p in x.KeywordPatterns) yield return p;
                break;
            case MatchCase x:
                yield return x.Pattern;
                if (x.Guard != null) yield return x.Guard;
                foreach (var s in x.Body) yield return s;
                break;

            // --- statements
            case ExprStmt x: yield return x.Value; break;
            case AssignStmt x:
                foreach (var t in x.Targets) yield return t;
                yield return x.Value;
                break;
            case AugAssignStmt x: yield return x.Target; yield return x.Value; break;
            case AnnAssignStmt x:
                yield return x.Target; yield return x.Annotation;
                if (x.Value != null) yield return x.Value;
                break;
            case ReturnStmt x: if (x.Value != null) yield return x.Value; break;
            case RaiseStmt x:
                if (x.Exc != null) yield return x.Exc;
                if (x.Cause != null) yield return x.Cause;
                break;
            case DelStmt x: foreach (var t in x.Targets) yield return t; break;
            case AssertStmt x:
                yield return x.Test;
                if (x.Message != null) yield return x.Message;
                break;
            case ImportStmt x: foreach (var a in x.Names) yield return a; break;
            case ImportFromStmt x: foreach (var a in x.Names) yield return a; break;
            case IfStmt x:
                yield return x.Test;
                foreach (var s in x.Body) yield return s;
                foreach (var s in x.Orelse) yield return s;
                break;
            case WhileStmt x:
                yield return x.Test;
                foreach (var s in x.Body) yield return s;
                foreach (var s in x.Orelse) yield return s;
                break;
            case ForStmt x:
                yield return x.Target; yield return x.Iter;
                foreach (var s in x.Body) yield return s;
                foreach (var s in x.Orelse) yield return s;
                break;
            case ExceptHandler x:
                if (x.Type != null) yield return x.Type;
                foreach (var s in x.Body) yield return s;
                break;
            case TryStmt x:
                foreach (var s in x.Body) yield return s;
                foreach (var h in x.Handlers) yield return h;
                foreach (var s in x.Orelse) yield return s;
                foreach (var s in x.Finally) yield return s;
                break;
            case WithItem x:
                yield return x.Context;
                if (x.Target != null) yield return x.Target;
                break;
            case WithStmt x:
                foreach (var i in x.Items) yield return i;
                foreach (var s in x.Body) yield return s;
                break;
            case FunctionDef x:
                foreach (var d in x.Decorators) yield return d;
                foreach (var t in x.TypeParams) yield return t;
                foreach (var p in x.Params) yield return p;
                if (x.Returns != null) yield return x.Returns;
                foreach (var s in x.Body) yield return s;
                break;
            case ClassDef x:
                foreach (var d in x.Decorators) yield return d;
                foreach (var t in x.TypeParams) yield return t;
                foreach (var b in x.Bases) yield return b;
                foreach (var s in x.Body) yield return s;
                break;
            case MatchStmt x:
                yield return x.Subject;
                foreach (var c in x.Cases) yield return c;
                break;
            case TypeAliasStmt x:
                foreach (var t in x.TypeParams) yield return t;
                yield return x.Value;
                break;
            case Module x: foreach (var s in x.Body) yield return s; break;
            // Pass, Break, Continue, Global, Nonlocal, ImportAlias, MatchCapture: leaves
        }
    }

    /// <summary>Pre-order walk of a whole subtree.</summary>
    public static IEnumerable<Node> Descendants(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            var kids = Of(n).ToList();
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
        }
    }
}
