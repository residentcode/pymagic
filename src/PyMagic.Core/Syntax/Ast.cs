namespace PyMagic.Core.Syntax;

/// <summary>Base of all syntax nodes. Start/End are offsets into the source text.</summary>
public abstract record Node
{
    public int Start { get; init; }
    public int End { get; init; }
}

public readonly record struct Diagnostic(int Start, int Length, string Message);

// ---------------------------------------------------------------- expressions

public abstract record Expr : Node;

public sealed record NameExpr(string Name) : Expr;

public enum ConstKind { Int, Float, Imaginary, Str, Bytes, True, False, None, Ellipsis }

/// <summary>Literal. For strings Text is the raw source (all adjacent literals joined); IsFString marks any f-/t-string part.</summary>
public sealed record ConstExpr(ConstKind Kind, string Text, bool IsFString = false) : Expr
{
    /// <summary>The expressions inside the replacement fields of an f-string (and of nested format specs), in source order.</summary>
    public IReadOnlyList<Expr> Parts { get; init; } = Array.Empty<Expr>();
}

public sealed record UnaryExpr(string Op, Expr Operand) : Expr;
/// <summary>Arithmetic, bitwise and boolean (and/or) binary operators.</summary>
public sealed record BinaryExpr(Expr Left, string Op, Expr Right) : Expr;
/// <summary>Chained comparison: a &lt; b &lt;= c. Ops use "not in" / "is not" spelling.</summary>
public sealed record CompareExpr(Expr Left, IReadOnlyList<string> Ops, IReadOnlyList<Expr> Comparators) : Expr;

/// <param name="Stars">0 = positional, 1 = *args, 2 = **kwargs</param>
public sealed record Argument(string? Name, Expr Value, int Stars) : Node;
public sealed record CallExpr(Expr Func, IReadOnlyList<Argument> Args) : Expr;
public sealed record AttributeExpr(Expr Value, string Attr) : Expr;
public sealed record SubscriptExpr(Expr Value, Expr Index) : Expr;
public sealed record SliceExpr(Expr? Lower, Expr? Upper, Expr? Step) : Expr;

public sealed record TupleExpr(IReadOnlyList<Expr> Items, bool Parenthesized) : Expr;
public sealed record ListExpr(IReadOnlyList<Expr> Items) : Expr;
public sealed record SetExpr(IReadOnlyList<Expr> Items) : Expr;
/// <summary>Dict display. A null key at index i means <c>**Values[i]</c>.</summary>
public sealed record DictExpr(IReadOnlyList<Expr?> Keys, IReadOnlyList<Expr> Values) : Expr;

public enum CompKind { List, Set, Dict, Generator }
public sealed record Comprehension(Expr Target, Expr Iter, IReadOnlyList<Expr> Ifs, bool IsAsync) : Node;
/// <summary>Comprehension or generator expression. Value is only set for dict comprehensions.</summary>
public sealed record CompExpr(CompKind Kind, Expr Element, Expr? Value, IReadOnlyList<Comprehension> Generators) : Expr;

public sealed record LambdaExpr(IReadOnlyList<Parameter> Params, Expr Body) : Expr;
public sealed record IfExpr(Expr Test, Expr Body, Expr Orelse) : Expr;
public sealed record StarExpr(Expr Value) : Expr;
public sealed record AwaitExpr(Expr Value) : Expr;
public sealed record YieldExpr(Expr? Value, bool IsFrom) : Expr;
public sealed record NamedExpr(Expr Target, Expr Value) : Expr;
/// <summary>Placeholder produced after a syntax error so later phases still see a tree.</summary>
public sealed record ErrorExpr : Expr;

// ---------------------------------------------------------------- parameters / type params

public enum ParamKind { Normal, VarArgs, KwArgs, Slash, KwOnlySeparator }
/// <summary>Slash and KwOnlySeparator are the bare "/" and "*" markers (Name is empty).</summary>
public sealed record Parameter(string Name, Expr? Annotation, Expr? Default, ParamKind Kind) : Node;

public enum TypeParamKind { TypeVar, TypeVarTuple, ParamSpec }
public sealed record TypeParam(string Name, TypeParamKind Kind, Expr? Bound, Expr? Default) : Node;

// ---------------------------------------------------------------- patterns (match statement)

public abstract record Pattern : Node;
public sealed record MatchValue(Expr Value) : Pattern;                       // 1, "s", a.b, None
public sealed record MatchCapture(string? Name) : Pattern;                   // x, or _ when Name is null
public sealed record MatchStar(string? Name) : Pattern;                      // *rest, *_
public sealed record MatchAs(Pattern Pattern, string Name) : Pattern;        // p as x
public sealed record MatchOr(IReadOnlyList<Pattern> Alternatives) : Pattern;
public sealed record MatchSequence(IReadOnlyList<Pattern> Items) : Pattern;
public sealed record MatchMapping(IReadOnlyList<Expr> Keys, IReadOnlyList<Pattern> Patterns, string? Rest) : Pattern;
public sealed record MatchClass(Expr Cls, IReadOnlyList<Pattern> Positional,
    IReadOnlyList<string> KeywordNames, IReadOnlyList<Pattern> KeywordPatterns) : Pattern;

public sealed record MatchCase(Pattern Pattern, Expr? Guard, IReadOnlyList<Stmt> Body) : Node;

// ---------------------------------------------------------------- statements

public abstract record Stmt : Node;

public sealed record ExprStmt(Expr Value) : Stmt;
public sealed record AssignStmt(IReadOnlyList<Expr> Targets, Expr Value) : Stmt;
public sealed record AugAssignStmt(Expr Target, string Op, Expr Value) : Stmt;
public sealed record AnnAssignStmt(Expr Target, Expr Annotation, Expr? Value) : Stmt;
public sealed record ReturnStmt(Expr? Value) : Stmt;
public sealed record PassStmt : Stmt;
public sealed record BreakStmt : Stmt;
public sealed record ContinueStmt : Stmt;
public sealed record RaiseStmt(Expr? Exc, Expr? Cause) : Stmt;
public sealed record GlobalStmt(IReadOnlyList<string> Names) : Stmt;
public sealed record NonlocalStmt(IReadOnlyList<string> Names) : Stmt;
public sealed record DelStmt(IReadOnlyList<Expr> Targets) : Stmt;
public sealed record AssertStmt(Expr Test, Expr? Message) : Stmt;

public sealed record ImportAlias(string Name, string? AsName) : Node;
public sealed record ImportStmt(IReadOnlyList<ImportAlias> Names) : Stmt;
/// <summary>Level = number of leading dots. Module is null for "from . import x". Names empty with IsStar for "import *".</summary>
public sealed record ImportFromStmt(int Level, string? Module, IReadOnlyList<ImportAlias> Names, bool IsStar) : Stmt;

public sealed record IfStmt(Expr Test, IReadOnlyList<Stmt> Body, IReadOnlyList<Stmt> Orelse) : Stmt;
public sealed record WhileStmt(Expr Test, IReadOnlyList<Stmt> Body, IReadOnlyList<Stmt> Orelse) : Stmt;
public sealed record ForStmt(Expr Target, Expr Iter, IReadOnlyList<Stmt> Body, IReadOnlyList<Stmt> Orelse, bool IsAsync) : Stmt;

public sealed record ExceptHandler(Expr? Type, string? Name, IReadOnlyList<Stmt> Body) : Node;
public sealed record TryStmt(IReadOnlyList<Stmt> Body, IReadOnlyList<ExceptHandler> Handlers,
    IReadOnlyList<Stmt> Orelse, IReadOnlyList<Stmt> Finally, bool IsStar) : Stmt;

public sealed record WithItem(Expr Context, Expr? Target) : Node;
public sealed record WithStmt(IReadOnlyList<WithItem> Items, IReadOnlyList<Stmt> Body, bool IsAsync) : Stmt;

/// <summary>NameStart is the offset of the function's name token (Start may point at a decorator).</summary>
public sealed record FunctionDef(string Name, IReadOnlyList<TypeParam> TypeParams, IReadOnlyList<Parameter> Params,
    Expr? Returns, IReadOnlyList<Stmt> Body, IReadOnlyList<Expr> Decorators, bool IsAsync) : Stmt
{
    public int NameStart { get; init; }
}
public sealed record ClassDef(string Name, IReadOnlyList<TypeParam> TypeParams, IReadOnlyList<Argument> Bases,
    IReadOnlyList<Stmt> Body, IReadOnlyList<Expr> Decorators) : Stmt
{
    public int NameStart { get; init; }
}

public sealed record MatchStmt(Expr Subject, IReadOnlyList<MatchCase> Cases) : Stmt;
public sealed record TypeAliasStmt(string Name, IReadOnlyList<TypeParam> TypeParams, Expr Value) : Stmt
{
    public int NameStart { get; init; }
}

public sealed record Module(IReadOnlyList<Stmt> Body) : Node;

/// <summary>Result of parsing a file: always a tree, plus any syntax errors found.</summary>
public sealed record ParseResult(Module Module, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<Token> Tokens);
