using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

public sealed partial class LanguageService
{
    // ------------------------------------------------------------ f-strings
    // The parser keeps f-strings as text, so names used inside {braces} are not references. Refactorings that depend on
    // knowing every use of a name therefore look at the text of f-strings too, and refuse when the name appears there.

    static IEnumerable<ConstExpr> FStrings(IEnumerable<Node> roots) =>
        roots.SelectMany(AstChildren.Descendants).OfType<ConstExpr>().Where(c => c.IsFString);

    static bool MentionedInFStrings(ModuleInfo m, string word, Func<Node, bool>? exclude = null)
    {
        var pattern = new Regex(@"(?<![\w.])" + Regex.Escape(word) + @"(?!\w)");
        return FStrings(new[] { (Node)m.Ast }).Any(c => (exclude == null || !exclude(c)) && pattern.IsMatch(c.Text));
    }

    // ------------------------------------------------------------ inline variable

    static bool IsAtom(Expr e) => e is NameExpr or ConstExpr or AttributeExpr or SubscriptExpr or CallExpr or ListExpr or DictExpr or SetExpr or CompExpr
        or TupleExpr { Parenthesized: true };

    /// <summary>The text that replaces a use: the expression itself, in parentheses unless the context makes them unnecessary.</summary>
    static string InlinedText(ModuleInfo m, Expr value, NameExpr use) => WrapForContext(m, value, m.Source[value.Start..value.End], use);

    Dictionary<ModuleInfo, List<Edit>> InlineVariable(Snapshot snap, ModuleInfo m, int offset)
    {
        if (NameLikeAt(m, offset) is not NameExpr at) throw new ServiceException("Put the cursor on a variable.");
        var rawSymbol = m.Semantics.Resolve(at) ?? throw new ServiceException("This name is not a variable of the code here.");
        var sym = Canon(rawSymbol);
        if (sym.Scope.Kind is ScopeKind.Class) throw new ServiceException("Class attributes cannot be inlined.");
        if (sym.Declarations.Count != 1 || sym.Declarations[0].Kind != DeclKind.Variable)
            throw new ServiceException($"'{sym.Name}' is assigned more than once (or is not a plain variable).");

        var decl = sym.Declarations[0];
        Stmt statement;
        Expr value;
        switch (m.ParentOf(decl.Node))
        {
            case AssignStmt a when a.Targets.Count == 1 && ReferenceEquals(a.Targets[0], decl.Node):
                statement = a; value = a.Value; break;
            case AnnAssignStmt { Value: { } v } aa when ReferenceEquals(aa.Target, decl.Node):
                statement = aa; value = v; break;
            default:
                throw new ServiceException("Only a simple assignment (`name = value`) can be inlined.");
        }
        if (value is YieldExpr or StarExpr) throw new ServiceException("This kind of value cannot be inlined.");

        var uses = sym.References.Where(r => !ReferenceEquals(r.Node, decl.Node)).ToList();
        if (uses.Any(r => r.Access != Access.Read)) throw new ServiceException($"'{sym.Name}' is changed or deleted elsewhere.");
        if (uses.Any(r => r.UseScope != sym.Scope)) throw new ServiceException($"'{sym.Name}' is used inside a nested function, lambda or comprehension, where it may be evaluated later.");
        if (MentionedInFStrings(m, sym.Name)) throw new ServiceException($"'{sym.Name}' appears inside an f-string, which cannot be updated yet.");

        // module-level names may be imported by other files
        if (sym.Scope.Kind == ScopeKind.Module)
        {
            var target = FindTarget(snap, m, decl.Node.Start);
            if (target != null && Usages(snap, target, forRename: false).Any(s => !ReferenceEquals(s.Module, m)))
                throw new ServiceException($"'{sym.Name}' is used in other files.");
        }

        // every use must see this assignment and nothing else
        foreach (var r in uses)
        {
            var rr = m.Flow.ReachingDefinitions(r.Node);
            if (rr == null || rr.MaybeUnbound || rr.Definitions.Count != 1 || !ReferenceEquals(rr.Definitions[0].Node, decl.Node))
                throw new ServiceException($"'{sym.Name}' is not always assigned before this use.");
        }

        // the value must mean the same at every use: no name in it may change after the assignment
        foreach (var n in AstChildren.Descendants(value).OfType<NameExpr>())
        {
            var s2 = m.Semantics.Resolve(n);
            if (s2 != null && Canon(s2).Declarations.Any(d => d.Node.Start > statement.Start))
                throw new ServiceException($"'{n.Name}' is assigned again later, so the value could differ at the places where '{sym.Name}' is used.");
        }

        // a value with side effects may only move to the single use right after it
        bool pure = IsPure(value);
        if (!pure)
        {
            var parent = m.ParentOf(statement);
            var block = parent == null ? null : BodyContaining(parent, statement);
            int i = block == null ? -1 : block.ToList().FindIndex(x => ReferenceEquals(x, statement));
            var next = i >= 0 && i + 1 < block!.Count ? block[i + 1] : null;
            if (uses.Count > 1 || (uses.Count == 1 && (next == null || uses[0].Node.Start < next.Start || uses[0].Node.End > next.End)))
                throw new ServiceException("The value calls something, so it can only move to a single use in the next statement.");
        }

        var edits = new List<Edit>();
        foreach (var r in uses) edits.Add(new Edit(r.Node.Start, r.Node.End, InlinedText(m, value, r.Node)));
        if (uses.Count == 0 && !pure) edits.Add(new Edit(statement.Start, statement.End, m.Source[value.Start..value.End]));   // keep the call, drop the name
        else edits.Add(DeleteStatement(m, statement));
        return new Dictionary<ModuleInfo, List<Edit>> { [m] = edits };
    }

    // ------------------------------------------------------------ safe delete

    Dictionary<ModuleInfo, List<Edit>> SafeDelete(Snapshot snap, ModuleInfo m, int offset)
    {
        var target = FindTarget(snap, m, offset) ?? throw new ServiceException("Put the cursor on the name of a function, class, variable or import.");
        if (target.Symbols.Count != 1 || target.Nodes.OfType<AttributeExpr>().Any()) throw new ServiceException("Safe delete works on functions, classes, variables and imports.");
        var sym = target.Symbols[0];
        if (sym.Declarations.Count == 0) throw new ServiceException("There is nothing to delete here.");
        if (!(offset >= target.Token.Start && offset <= target.Token.End)) throw new ServiceException("Put the cursor on the name.");
        if (!ReferenceEquals(snap.Workspace.ModuleOf(sym), m)) throw new ServiceException("The name is defined in another file: open it there.");

        // only the declaration under the cursor counts as "the definition" when the cursor is on it
        var decl = sym.Declarations.FirstOrDefault(d => { var (a, b) = DeclSpan(m, d); return offset >= a && offset <= b; })
            ?? throw new ServiceException("Put the cursor on the definition of the name.");
        if (sym.Declarations.Count > 1) throw new ServiceException($"'{sym.Name}' is defined more than once.");

        Edit edit;
        switch (decl.Node)
        {
            case FunctionDef or ClassDef:
                edit = DeleteStatement(m, (Stmt)decl.Node);
                break;
            case ImportAlias alias when m.ParentOf(alias) is Stmt import:
                edit = RemoveAliases(m, import, new[] { alias });
                break;
            case NameExpr when m.ParentOf(decl.Node) is AssignStmt { Targets.Count: 1 } a:
                if (!IsPure(a.Value)) throw new ServiceException($"The value assigned to '{sym.Name}' calls something: deleting it would remove that call.");
                edit = DeleteStatement(m, a);
                break;
            default:
                throw new ServiceException($"'{sym.Name}' is not something that can be deleted safely (parameters need a signature change).");
        }

        var usages = Usages(snap, target, forRename: false).Where(s => !s.IsDeclaration).ToList();
        // the declaration of an import is the alias itself, not a use
        usages = usages.Where(s => !(ReferenceEquals(s.Module, m) && s.Start >= decl.Node.Start && s.End <= decl.Node.End)).ToList();
        if (usages.Count > 0)
        {
            var where = usages.Take(4).Select(s => $"{System.IO.Path.GetFileName(s.Module.Path ?? s.Module.Name)}:{Lines(s.Module).PositionOf(s.Start).Line + 1}");
            throw new ServiceException($"'{sym.Name}' is used {usages.Count} time{(usages.Count == 1 ? "" : "s")} ({string.Join(", ", where)}{(usages.Count > 4 ? ", ..." : "")}).");
        }
        foreach (var module in snap.Project)
            if (MentionedInFStrings(module, sym.Name)) throw new ServiceException($"'{sym.Name}' appears inside an f-string.");
        foreach (var module in snap.Project)
            if (AstChildren.Descendants(module.Ast).OfType<ConstExpr>().Any(c => c.Kind == ConstKind.Str && Regex.IsMatch(c.Text, @"^[rRbBuU]*['""]" + Regex.Escape(sym.Name) + @"['""]$")))
                throw new ServiceException($"'{sym.Name}' is mentioned in a string (for example in __all__).");

        return new Dictionary<ModuleInfo, List<Edit>> { [m] = new List<Edit> { edit } };
    }
}
