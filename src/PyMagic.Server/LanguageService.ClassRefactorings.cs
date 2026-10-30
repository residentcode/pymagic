using PyModule = PyMagic.Core.Syntax.Module;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// The refactorings PyCharm has beside extract and inline: Introduce Parameter and Introduce Attribute (from a selected expression),
/// Extract Superclass and Extract Delegate (from a class and the members chosen), Pull Members Up and Push Members Down,
/// Encapsulate Field, Invert Boolean, Make Function Top-Level and Move Static Members. Like the others, each either returns all
/// of its edits or fails with the reason.
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>The refactorings of this file that apply at the cursor (an empty selection).</summary>
    void ClassRefactorOptions(Snapshot snap, ModuleInfo m, int at, List<RefactorOption> options, Func<string, string?, bool, IReadOnlyList<string>?, bool> feasible)
    {
        if (ClassNamedAt(m, at) is { } cls)
        {
            var movable = MovableMembers(cls).Select(s => MemberName(s)!).ToList();
            if (feasible("extractSuperclass", "Base" + cls.Name, false, null))
                options.Add(new RefactorOption("extractSuperclass", "Extract superclass…", "Base" + cls.Name, Members: movable, Chosen: movable));
            var methods = MovableMembers(cls).OfType<FunctionDef>().Select(f => f.Name).ToList();
            if (methods.Count > 0)
                options.Add(new RefactorOption("extractDelegate", "Extract delegate…", cls.Name + "Delegate", Members: methods, Chosen: new List<string>()));
        }
        if (MemberAt(m, at) is ({ } owner, { } member))
        {
            var movable = MovableMembers(owner).Select(s => MemberName(s)!).ToList();
            var chosen = new List<string> { MemberName(member)! };
            if (feasible("pullMembersUp", null, false, null)) options.Add(new RefactorOption("pullMembersUp", "Pull members up…", null, Members: movable, Chosen: chosen));
            if (feasible("pushMembersDown", null, false, null)) options.Add(new RefactorOption("pushMembersDown", "Push members down…", null, Members: movable, Chosen: chosen));
            if (member is AssignStmt or AnnAssignStmt || member is FunctionDef f && IsStatic(f))
                options.Add(new RefactorOption("moveStaticMember", "Move static member…", "TargetClass"));
        }
        if (feasible("encapsulateField", null, false, null)) options.Add(new RefactorOption("encapsulateField", "Encapsulate field", null));
        if (feasible("makeTopLevel", null, false, null)) options.Add(new RefactorOption("makeTopLevel", "Make function top-level", null));
        if (AstChildren.Descendants(m.Ast).OfType<NameExpr>().FirstOrDefault(n => at >= n.Start && at <= n.End) is { } name
            && m.Semantics.Resolve(name) is { } sym && sym.Declarations.All(d => d.Kind == DeclKind.Variable)
            && feasible("invertBoolean", "not_" + name.Name, false, null))
            options.Add(new RefactorOption("invertBoolean", "Invert boolean…", InvertedName(name.Name)));
    }

    /// <summary>A name for the opposite of a flag: is_open → is_closed is beyond this, so a not_ (or one taken away) does it.</summary>
    static string InvertedName(string name) => name.StartsWith("not_") ? name[4..] : name.StartsWith("is_not_") ? "is_" + name[7..] : name.StartsWith("is_") ? "is_not_" + name[3..] : "not_" + name;

    // ------------------------------------------------------------ what is at the cursor

    /// <summary>The class whose name is at the offset.</summary>
    static ClassDef? ClassNamedAt(ModuleInfo m, int offset) =>
        AstChildren.Descendants(m.Ast).OfType<ClassDef>().FirstOrDefault(c => offset >= c.NameStart && offset <= c.NameStart + c.Name.Length);

    /// <summary>The name a statement of a class body defines: a method, a nested class, `x = ...` or `x: T = ...`.</summary>
    static string? MemberName(Stmt s) => s switch
    {
        FunctionDef f => f.Name,
        ClassDef c => c.Name,
        AssignStmt { Targets: [NameExpr n] } => n.Name,
        AnnAssignStmt { Target: NameExpr n } => n.Name,
        _ => null,
    };

    static (int Start, int End) MemberNameSpan(Stmt s) => s switch
    {
        FunctionDef f => (f.NameStart, f.NameStart + f.Name.Length),
        ClassDef c => (c.NameStart, c.NameStart + c.Name.Length),
        AssignStmt { Targets: [NameExpr n] } => (n.Start, n.End),
        AnnAssignStmt { Target: NameExpr n } => (n.Start, n.End),
        _ => (-1, -1),
    };

    /// <summary>The member of a class whose name is at the offset.</summary>
    static (ClassDef Class, Stmt Member)? MemberAt(ModuleInfo m, int offset)
    {
        foreach (var cls in AstChildren.Descendants(m.Ast).OfType<ClassDef>())
            foreach (var s in cls.Body)
            {
                var (a, b) = MemberNameSpan(s);
                if (a >= 0 && offset >= a && offset <= b) return (cls, s);
            }
        return null;
    }

    static bool IsStatic(FunctionDef f) => f.Decorators.Any(d => d is NameExpr { Name: "staticmethod" });
    static bool IsClassMethod(FunctionDef f) => f.Decorators.Any(d => d is NameExpr { Name: "classmethod" });
    static string? SelfName(FunctionDef f) =>
        IsStatic(f) ? null : f.Params.FirstOrDefault() is { Kind: ParamKind.Normal } p ? p.Name : null;

    /// <summary>The members a class offers to move: its methods and class attributes, not the dunder ones.</summary>
    static List<Stmt> MovableMembers(ClassDef cls) =>
        cls.Body.Where(s => MemberName(s) is { } n && !(n.StartsWith("__") && n.EndsWith("__")) && s is not ClassDef).ToList();

    // ------------------------------------------------------------ text helpers

    /// <summary>The whole lines of a statement (its decorators included).</summary>
    static (int Start, int End) BlockOf(string text, Stmt s) => (LineStart(text, s.Start), LineEndInclusive(text, CodeEnd(text, s.End)));

    /// <summary>Lines moved from one indentation to another; lines indented less (inside a multi-line string) stay as they are.</summary>
    static string Reindent(string block, string from, string to)
    {
        var lines = block.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.Trim().Length == 0) lines[i] = "";
            else if (l.StartsWith(from)) lines[i] = to + l[from.Length..];
        }
        return string.Join("\n", lines);
    }

    static string BodyIndent(string text, ClassDef cls) =>
        cls.Body.Count > 0 ? LineIndent(text, cls.Body[0].Start) : LineIndent(text, cls.Start) + "    ";

    /// <summary>A member's text at another indentation, ending with a line break.</summary>
    static string MemberText(ModuleInfo m, ClassDef from, Stmt s, string to)
    {
        var (a, b) = BlockOf(m.Source, s);
        string text = Reindent(m.Source[a..b].Replace("\r\n", "\n"), BodyIndent(m.Source, from), to);
        return text.EndsWith('\n') ? text : text + "\n";
    }

    /// <summary>Members joined for a class body: a blank line before each def or decorated one.</summary>
    static string JoinMembers(IEnumerable<string> texts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in texts)
        {
            bool method = t.TrimStart().StartsWith("def ") || t.TrimStart().StartsWith("async def ") || t.TrimStart().StartsWith('@');
            if (sb.Length > 0 && method) sb.Append('\n');
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>The edits that take statements out of a class body, with the blank lines between them; `pass` when nothing is left.</summary>
    static List<Edit> RemoveMembers(ModuleInfo m, ClassDef cls, IReadOnlyCollection<Stmt> removed)
    {
        string text = m.Source;
        bool IsBlankLineBefore(int lineStart, out int prev)
        {
            prev = LineStart(text, Math.Max(0, lineStart - 1));
            return lineStart > 0 && text[prev..lineStart].Trim().Length == 0;
        }
        if (cls.Body.All(s => removed.Contains(s)))
        {
            int a = BlockOf(text, cls.Body[0]).Start, b = BlockOf(text, cls.Body[^1]).End;
            return new List<Edit> { new(a, b, BodyIndent(text, cls) + "pass\n") };
        }
        var ranges = new List<(int Start, int End)>();
        foreach (var s in cls.Body.Where(removed.Contains))
        {
            var (a, b) = BlockOf(text, s);
            // after the last member that stays, the blank lines before it go; else the ones after it
            bool last = cls.Body.ToList().IndexOf(s) > cls.Body.ToList().FindLastIndex(x => !removed.Contains(x));
            if (!last)
                while (b < text.Length && LineEndInclusive(text, b) is var next && next > b && text[b..next].Trim().Length == 0) b = next;
            else
                while (IsBlankLineBefore(a, out int prev)) a = prev;
            ranges.Add((a, b));
        }
        var merged = new List<Edit>();
        foreach (var (a, b) in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && a <= merged[^1].End) merged[^1] = merged[^1] with { End = Math.Max(b, merged[^1].End) };
            else merged.Add(new Edit(a, b, ""));
        }
        return merged;
    }

    static Dictionary<ModuleInfo, List<Edit>> Changes() => new();
    /// <summary>`from module import name` in a file, once, unless the file has the name already.</summary>
    void AddImport(Dictionary<ModuleInfo, List<Edit>> changes, ModuleInfo m, string module, string name)
    {
        if (m.Semantics.ModuleScope.Symbols.TryGetValue(name, out var there) && there.Declarations.Count > 0) return;
        var edit = ImportEdit(m, module, name);
        if (changes.TryGetValue(m, out var list) && list.Any(e => e.Start == edit.Start && e.Text == edit.Text)) return;
        AddEdit(changes, m, edit);
    }

    static void AddEdit(Dictionary<ModuleInfo, List<Edit>> changes, ModuleInfo m, Edit e)
    {
        if (!changes.TryGetValue(m, out var list)) changes[m] = list = new List<Edit>();
        list.Add(e);
    }

    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    };

    static string RequireName(string? name, string what) =>
        IsIdentifier(name?.Trim() ?? "") && !Keywords.Contains(name!.Trim()) ? name!.Trim() : throw new ServiceException($"'{name}' is not a usable name for {what}.");

    /// <summary>The members to work on: the ones named, or the given default.</summary>
    static List<Stmt> ChosenMembers(ClassDef cls, IReadOnlyList<string>? names, IEnumerable<Stmt> fallback)
    {
        if (names == null || names.Count == 0) return fallback.ToList();
        var list = cls.Body.Where(s => MemberName(s) is { } n && names.Contains(n)).ToList();
        if (list.Count == 0) throw new ServiceException("None of the chosen members is in the class.");
        return list;
    }

    bool InProject(Snapshot snap, ModuleInfo module) => snap.Project.Contains(module) && !module.IsStub && UriOfModule(module) != null;

    // ------------------------------------------------------------ Introduce Parameter / Attribute

    /// <summary>The names of an expression that belong to a scope (its own locals and parameters).</summary>
    static List<string> LocalNamesIn(ModuleInfo m, Expr e, Node scopeNode) =>
        AstChildren.Descendants(e).OfType<NameExpr>()
            .Where(n => m.Semantics.Resolve(n) is { } sym && ReferenceEquals(sym.Scope.Node, scopeNode))
            .Select(n => n.Name).Distinct().ToList();

    List<Edit> IntroduceParameter(ModuleInfo m, Selection sel, string? name)
    {
        var e = sel.Expression ?? throw new ServiceException("Select an expression.");
        if (EnclosingScopeNode(m, e) is not FunctionDef f) throw new ServiceException("The expression is not inside a function.");
        string p = RequireName(name, "a parameter");
        if (f.Params.Any(x => x.Name == p) || m.Semantics.ScopeOf(f)?.Symbols.ContainsKey(p) == true)
            throw new ServiceException($"'{f.Name}' has a name '{p}' already.");
        var locals = LocalNamesIn(m, e, f);
        if (locals.Count > 0)
            throw new ServiceException($"The expression uses {string.Join(", ", locals.Select(x => $"'{x}'"))} of '{f.Name}', which a default value cannot see.");
        string value = m.Source[e.Start..e.End];
        // after the last ordinary parameter, before *args, a bare * and **kwargs
        int lastNormal = -1;
        for (int i = 0; i < f.Params.Count && f.Params[i].Kind is ParamKind.Normal or ParamKind.Slash; i++) lastNormal = i;
        Edit insert;
        if (lastNormal >= 0) insert = new Edit(f.Params[lastNormal].End, f.Params[lastNormal].End, $", {p}={value}");
        else if (f.Params.Count > 0) insert = new Edit(f.Params[0].Start, f.Params[0].Start, $"{p}={value}, ");
        else
        {
            int open = m.Source.IndexOf('(', f.NameStart);
            insert = new Edit(open + 1, open + 1, $"{p}={value}");
        }
        return new List<Edit> { insert, new(sel.Start, sel.End, p) };
    }

    List<Edit> IntroduceField(ModuleInfo m, Selection sel, string? name)
    {
        var e = sel.Expression ?? throw new ServiceException("Select an expression.");
        if (EnclosingScopeNode(m, e) is not FunctionDef f || ClassOfMethod(m, f) is not { } cls || SelfName(f) is not { } self || IsClassMethod(f))
            throw new ServiceException("The expression is not inside a method.");
        string attr = RequireName(name, "an attribute");
        string text = m.Source;
        var locals = LocalNamesIn(m, e, f).Where(n => n != self).ToList();
        var init = cls.Body.OfType<FunctionDef>().FirstOrDefault(x => x.Name == "__init__");
        string value = text[e.Start..e.End];
        var edits = new List<Edit> { new(sel.Start, sel.End, $"{self}.{attr}") };
        if (locals.Count == 0 && init != null && !ReferenceEquals(init, f) && SelfName(init) is { } initSelf && init.Body.Count > 0)
        {
            int at = LineEndInclusive(text, CodeEnd(text, init.Body[^1].End));
            string indent = LineIndent(text, init.Body[^1].Start);
            value = value.Replace(self + ".", initSelf + ".");
            edits.Add(new Edit(at, at, (at == text.Length && !text.EndsWith('\n') ? "\n" : "") + $"{indent}{initSelf}.{attr} = {value}\n"));
        }
        else
        {
            var stmt = EnclosingStatement(m, e) ?? throw new ServiceException("The expression is not inside a statement.");
            int at = LineStart(text, stmt.Start);
            edits.Add(new Edit(at, at, $"{LineIndent(text, stmt.Start)}{self}.{attr} = {value}\n"));
        }
        return edits;
    }

    // ------------------------------------------------------------ Extract Superclass

    Dictionary<ModuleInfo, List<Edit>> ExtractSuperclass(ModuleInfo m, int offset, string? name, IReadOnlyList<string>? members)
    {
        var cls = ClassNamedAt(m, offset) ?? throw new ServiceException("Put the cursor on the name of a class.");
        if (m.ParentOf(cls) is not PyModule) throw new ServiceException("Only a class at the top of a file can get a superclass this way.");
        string super = RequireName(name, "the superclass");
        if (m.Semantics.ModuleScope.Symbols.ContainsKey(super)) throw new ServiceException($"The file has a '{super}' already.");
        if (cls.Bases.Any(b => b.Name != null || b.Stars != 0)) throw new ServiceException("The class has keyword arguments in its bases (a metaclass); move them by hand.");
        var moved = ChosenMembers(cls, members, MovableMembers(cls));
        string text = m.Source;
        string bases = cls.Bases.Count > 0 ? text[cls.Bases[0].Start..cls.Bases[^1].End] : "";
        string body = moved.Count == 0 ? "    pass\n" : JoinMembers(moved.Select(s => MemberText(m, cls, s, "    ")));
        var edits = new List<Edit> { TopLevelInsert(m, cls, $"class {super}{(bases.Length > 0 ? $"({bases})" : "")}:\n{body.TrimEnd('\n')}") };
        if (cls.Bases.Count > 0) edits.Add(new Edit(cls.Bases[0].Start, cls.Bases[^1].End, super));
        else
        {
            int after = cls.NameStart + cls.Name.Length;
            int i = after;
            while (i < text.Length && text[i] == ' ') i++;
            edits.Add(i < text.Length && text[i] == '(' ? new Edit(i + 1, i + 1, super) : new Edit(after, after, $"({super})"));
        }
        edits.AddRange(RemoveMembers(m, cls, moved));
        return new Dictionary<ModuleInfo, List<Edit>> { [m] = edits };
    }

    // ------------------------------------------------------------ Pull Members Up / Push Members Down

    (ModuleInfo Module, ClassDef Class)? ProjectBase(Snapshot snap, ModuleInfo m, ClassDef cls) =>
        DirectBases(snap, m, cls).Where(b => InProject(snap, b.Module)).Select(b => ((ModuleInfo, ClassDef)?)b).FirstOrDefault();

    Dictionary<ModuleInfo, List<Edit>> PullMembersUp(Snapshot snap, ModuleInfo m, int offset, IReadOnlyList<string>? members)
    {
        var (cls, at) = MemberAt(m, offset) ?? throw new ServiceException("Put the cursor on the name of a method or class attribute.");
        var (bm, bcls) = ProjectBase(snap, m, cls) ?? throw new ServiceException($"'{cls.Name}' has no base class in the project to pull members into.");
        var moved = ChosenMembers(cls, members, new[] { at });
        foreach (var s in moved)
            if (bcls.Body.Any(x => MemberName(x) == MemberName(s))) throw new ServiceException($"'{bcls.Name}' has a '{MemberName(s)}' already.");
        var changes = Changes();
        string to = BodyIndent(bm.Source, bcls);
        bool onlyPass = bcls.Body.Count == 1 && bcls.Body[0] is PassStmt;
        AddEdit(changes, bm, AtClassEnd(bm, bcls, JoinMembers(moved.Select(s => MemberText(m, cls, s, to))), onlyPass));
        foreach (var e in RemoveMembers(m, cls, moved)) AddEdit(changes, m, e);
        return changes;
    }

    Dictionary<ModuleInfo, List<Edit>> PushMembersDown(Snapshot snap, ModuleInfo m, int offset, IReadOnlyList<string>? members)
    {
        var (cls, at) = MemberAt(m, offset) ?? throw new ServiceException("Put the cursor on the name of a method or class attribute.");
        var subs = (SubclassIndex(snap).GetValueOrDefault(cls) ?? new()).Where(s => InProject(snap, s.Module)).ToList();
        if (subs.Count == 0) throw new ServiceException($"'{cls.Name}' has no subclasses in the project.");
        var moved = ChosenMembers(cls, members, new[] { at });
        var changes = Changes();
        foreach (var (sm, scls) in subs)
        {
            var copies = moved.Where(s => !scls.Body.Any(x => MemberName(x) == MemberName(s))).ToList();   // a subclass's own one stays
            if (copies.Count == 0) continue;
            bool onlyPass = scls.Body.Count == 1 && scls.Body[0] is PassStmt;
            AddEdit(changes, sm, AtClassEnd(sm, scls, JoinMembers(copies.Select(s => MemberText(m, cls, s, BodyIndent(sm.Source, scls)))), onlyPass));
        }
        foreach (var e in RemoveMembers(m, cls, moved)) AddEdit(changes, m, e);
        return changes;
    }

    // ------------------------------------------------------------ Encapsulate Field

    /// <summary>The class and attribute name at the cursor: `self.x` in a method, or `x = ...` in the class body.</summary>
    static (ClassDef Class, string Name)? FieldAt(ModuleInfo m, int offset)
    {
        if (MemberAt(m, offset) is ({ } c, AssignStmt or AnnAssignStmt) member) return (c, MemberName(member.Member)!);
        foreach (var a in AstChildren.Descendants(m.Ast).OfType<AttributeExpr>())
        {
            if (offset < a.End - a.Attr.Length || offset > a.End || a.Value is not NameExpr recv) continue;
            for (var p = m.ParentOf(a); p != null; p = m.ParentOf(p))
                if (p is FunctionDef f && ClassOfMethod(m, f) is { } cls)
                    return SelfName(f) == recv.Name ? (cls, a.Attr) : null;
        }
        return null;
    }

    Dictionary<ModuleInfo, List<Edit>> EncapsulateField(ModuleInfo m, int offset)
    {
        var (cls, field) = FieldAt(m, offset) ?? throw new ServiceException("Put the cursor on an attribute: self.name in a method, or name = ... in the class.");
        if (field.StartsWith('_')) throw new ServiceException($"'{field}' is private already.");
        string store = "_" + field;
        if (cls.Body.Any(s => MemberName(s) == store || s is FunctionDef f && f.Name == field)) throw new ServiceException($"'{cls.Name}' has a '{store}' or a method '{field}' already.");
        var edits = new List<Edit>();
        bool any = false;
        foreach (var s in cls.Body)
        {
            if (MemberName(s) == field && s is AssignStmt or AnnAssignStmt)
            {
                var (a, b) = MemberNameSpan(s);
                edits.Add(new Edit(a, b, store));
                any = true;
            }
            if (s is not FunctionDef f || SelfName(f) is not { } self) continue;
            foreach (var attr in AstChildren.Descendants(f).OfType<AttributeExpr>())
                if (attr.Attr == field && attr.Value is NameExpr { Name: var n } && n == self && m.Semantics.Resolve((NameExpr)attr.Value)?.Scope.Node == f)
                {
                    edits.Add(new Edit(attr.End - field.Length, attr.End, store));
                    any = true;
                }
        }
        if (!any) throw new ServiceException($"'{field}' is not set or read in '{cls.Name}'.");
        string i = BodyIndent(m.Source, cls), ii = i + "    ";
        string props = $"{i}@property\n{i}def {field}(self):\n{ii}return self.{store}\n\n{i}@{field}.setter\n{i}def {field}(self, value):\n{ii}self.{store} = value\n";
        edits.Add(AtClassEnd(m, cls, props, false));
        return new Dictionary<ModuleInfo, List<Edit>> { [m] = edits };
    }

    // ------------------------------------------------------------ Invert Boolean

    /// <summary>A value that is plainly True or False: a literal, a comparison, not, and / or of those, bool(...) or isinstance(...).</summary>
    static bool LooksBoolean(Expr e) => e switch
    {
        ConstExpr { Kind: ConstKind.True or ConstKind.False } or CompareExpr or UnaryExpr { Op: "not" } => true,
        BinaryExpr { Op: "and" or "or" } b => LooksBoolean(b.Left) && LooksBoolean(b.Right),
        CallExpr { Func: NameExpr { Name: "bool" or "isinstance" or "issubclass" or "callable" or "hasattr" or "any" or "all" } } => true,
        _ => false,
    };

    static string TextOf(ModuleInfo m, Node n) => m.Source[n.Start..n.End];

    /// <summary>The text of `not e`, written the short way where there is one.</summary>
    static string Negated(ModuleInfo m, Expr e)
    {
        switch (e)
        {
            case ConstExpr { Kind: ConstKind.True }: return "False";
            case ConstExpr { Kind: ConstKind.False }: return "True";
            case UnaryExpr { Op: "not" } u: return TextOf(m, u.Operand);
            case CompareExpr { Ops.Count: 1 } c:
                string? swapped = c.Ops[0] switch { "==" => "!=", "!=" => "==", "is" => "is not", "is not" => "is", "in" => "not in", "not in" => "in", _ => null };
                if (swapped != null) return $"{TextOf(m, c.Left)} {swapped} {TextOf(m, c.Comparators[0])}";
                break;
        }
        bool parens = e is BinaryExpr { Op: "and" or "or" } or IfExpr or LambdaExpr;
        return parens ? $"not ({TextOf(m, e)})" : $"not {TextOf(m, e)}";
    }

    Dictionary<ModuleInfo, List<Edit>> InvertBoolean(Snapshot snap, ModuleInfo m, int offset, string? name)
    {
        var target = FindTarget(snap, m, offset) ?? throw new ServiceException("Put the cursor on a variable.");
        string newName = RequireName(name, "the inverted variable");
        var changes = Changes();
        bool assigned = false, booleanValue = false;
        foreach (var site in Usages(snap, target, forRename: true))
        {
            var sm = site.Module;
            var node = AstChildren.Descendants(sm.Ast).OfType<NameExpr>().FirstOrDefault(n => n.Start == site.Start && n.End == site.End)
                ?? throw new ServiceException("Only a plain variable can be inverted (it is used as an attribute somewhere).");
            var parent = sm.ParentOf(node);
            switch (parent)
            {
                case AssignStmt a when a.Targets.Count == 1 && ReferenceEquals(a.Targets[0], node):
                    AddEdit(changes, sm, new Edit(node.Start, node.End, newName));
                    AddEdit(changes, sm, new Edit(a.Value.Start, a.Value.End, Negated(sm, a.Value)));
                    assigned = true;
                    booleanValue |= LooksBoolean(a.Value);
                    break;
                case AnnAssignStmt { Value: { } v } an when ReferenceEquals(an.Target, node):
                    AddEdit(changes, sm, new Edit(node.Start, node.End, newName));
                    AddEdit(changes, sm, new Edit(v.Start, v.End, Negated(sm, v)));
                    assigned = true;
                    booleanValue |= LooksBoolean(v) || an.Annotation is NameExpr { Name: "bool" };
                    break;
                case Parameter or AssignStmt or AugAssignStmt or AnnAssignStmt or ForStmt or WithStmt or ImportAlias:
                    throw new ServiceException($"'{target.Name}' is set in a way that cannot be inverted (line {Lines(sm).RangeOf(node.Start, node.End).Start.Line + 1}).");
                case UnaryExpr { Op: "not" } u:
                    AddEdit(changes, sm, new Edit(u.Start, u.End, newName));
                    break;
                default:
                    if (sm.Semantics.ReferenceOf(node) is { Access: not Access.Read })
                        throw new ServiceException($"'{target.Name}' is set in a way that cannot be inverted.");
                    bool parens = parent is BinaryExpr { Op: not ("and" or "or") } or CompareExpr or AttributeExpr or SubscriptExpr or UnaryExpr
                        || parent is CallExpr call && ReferenceEquals(call.Func, node);
                    AddEdit(changes, sm, new Edit(node.Start, node.End, parens ? $"(not {newName})" : $"not {newName}"));
                    break;
            }
        }
        if (!assigned) throw new ServiceException($"'{target.Name}' is not a variable set with =.");
        if (!booleanValue) throw new ServiceException($"'{target.Name}' is not set to a boolean.");
        return changes;
    }

    // ------------------------------------------------------------ Make Function Top-Level

    Dictionary<ModuleInfo, List<Edit>> MakeTopLevel(Snapshot snap, ModuleInfo m, int offset)
    {
        var f = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(x => offset >= x.NameStart && offset <= x.NameStart + x.Name.Length)
            ?? throw new ServiceException("Put the cursor on the name of a method or a nested function.");
        var parent = m.ParentOf(f);
        if (parent is PyModule) throw new ServiceException($"'{f.Name}' is at the top level already.");
        if (m.Semantics.ModuleScope.Symbols.TryGetValue(f.Name, out var clash) && clash.Declarations.Count > 0)
            throw new ServiceException($"The file has a '{f.Name}' at the top level already.");
        string text = m.Source;
        var changes = Changes();
        var (a, b) = BlockOf(text, f);
        string own = LineIndent(text, f.Start);
        string moved = Reindent(text[a..b].Replace("\r\n", "\n"), own, "");

        if (parent is ClassDef cls)
        {
            if (m.ParentOf(cls) is not PyModule) throw new ServiceException("Only a method of a class at the top of a file can be moved out.");
            if (IsClassMethod(f)) throw new ServiceException("A class method needs its class; it cannot be moved out.");
            bool isStatic = IsStatic(f);
            if (isStatic)
                moved = string.Join("\n", moved.Split('\n').Where(l => l.Trim() != "@staticmethod"));
            var target = FindTarget(snap, m, f.NameStart) ?? throw new ServiceException("The method's usages cannot be found.");
            var sites = Usages(snap, target, forRename: false).Where(s => !s.IsDeclaration).ToList();
            foreach (var site in sites)
            {
                var sm = site.Module;
                var attr = AstChildren.Descendants(sm.Ast).OfType<AttributeExpr>().FirstOrDefault(x => x.End == site.End && x.End - x.Attr.Length == site.Start);
                if (attr == null || sm.ParentOf(attr) is not CallExpr call || !ReferenceEquals(call.Func, attr))
                    throw new ServiceException($"'{f.Name}' is used other than by calling it (line {Lines(sm).RangeOf(site.Start, site.End).Start.Line + 1}).");
                string args = call.Args.Count == 0 ? "" : sm.Source[call.Args[0].Start..call.Args[^1].End];
                string first = isStatic ? "" : TextOf(sm, attr.Value);
                string all = string.Join(", ", new[] { first, args }.Where(x => x.Length > 0));
                AddEdit(changes, sm, new Edit(call.Start, call.End, $"{f.Name}({all})"));
                if (!ReferenceEquals(sm, m)) AddImport(changes, sm, m.Name, f.Name);
            }
            foreach (var e in RemoveMembers(m, cls, new[] { (Stmt)f })) AddEdit(changes, m, e);
            int after = LineEndInclusive(text, CodeEnd(text, cls.End));
            AddEdit(changes, m, new Edit(after, after, (after == text.Length && !text.EndsWith('\n') ? "\n" : "") + "\n\n" + moved.TrimEnd('\n') + "\n"));
            return changes;
        }

        // a function inside a function: it must not use the names of the functions around it
        var outer = new List<string>();
        foreach (var n in AstChildren.Descendants(f).OfType<NameExpr>())
            if (m.Semantics.Resolve(n) is { } sym && sym.Scope.Kind == ScopeKind.Function && !IsInside(sym.Scope.Node, f) && !outer.Contains(n.Name)) outer.Add(n.Name);
        if (outer.Count > 0)
            throw new ServiceException($"'{f.Name}' uses {string.Join(", ", outer.Select(x => $"'{x}'"))} of the function around it; pass them as parameters first.");
        if (BodyContaining(parent!, f) is { } body && body.Count == 1)
        {
            var (pa, pb) = BlockOf(text, f);
            AddEdit(changes, m, new Edit(pa, pb, own + "pass\n"));
        }
        else
        {
            int start = a, end = b;
            while (start > 0 && LineStart(text, start - 1) is var prev && text[prev..start].Trim().Length == 0) start = prev;
            if (start == a)   // the first statement of its block: the blank lines after it go instead
                while (end < text.Length && LineEndInclusive(text, end) is var next && next > end && text[end..next].Trim().Length == 0) end = next;
            AddEdit(changes, m, new Edit(start, end, ""));
        }
        AddEdit(changes, m, TopLevelInsert(m, f, moved.TrimEnd('\n')));
        return changes;
    }

    static bool IsInside(Node inner, Node outer) => inner.Start >= outer.Start && inner.End <= outer.End;

    // ------------------------------------------------------------ Move Static Members

    Dictionary<ModuleInfo, List<Edit>> MoveStaticMember(Snapshot snap, ModuleInfo m, int offset, string? targetName)
    {
        var (cls, member) = MemberAt(m, offset) ?? throw new ServiceException("Put the cursor on the name of a static method or class attribute.");
        if (member is FunctionDef fd && !IsStatic(fd)) throw new ServiceException($"'{fd.Name}' is not a static method.");
        if (member is ClassDef) throw new ServiceException("Move a nested class with Move to another file.");
        string name = MemberName(member)!;
        string to = RequireName(targetName, "the target class");
        var found = snap.Project.Where(p => InProject(snap, p))
            .SelectMany(p => p.Ast.Body.OfType<ClassDef>().Where(c => c.Name == to).Select(c => (Module: p, Class: c))).ToList();
        if (found.Count == 0) throw new ServiceException($"There is no class '{to}' at the top of a file of the project.");
        if (found.Count > 1) throw new ServiceException($"There are {found.Count} classes named '{to}' in the project.");
        var (tm, tcls) = found[0];
        if (ReferenceEquals(tcls, cls)) throw new ServiceException("Choose another class.");
        if (tcls.Body.Any(s => MemberName(s) == name)) throw new ServiceException($"'{to}' has a '{name}' already.");
        var changes = Changes();
        var target = FindTarget(snap, m, MemberNameSpan(member).Start) ?? throw new ServiceException("The member's usages cannot be found.");
        foreach (var site in Usages(snap, target, forRename: false).Where(s => !s.IsDeclaration))
        {
            var sm = site.Module;
            var attr = AstChildren.Descendants(sm.Ast).OfType<AttributeExpr>().FirstOrDefault(x => x.End == site.End && x.End - x.Attr.Length == site.Start)
                ?? throw new ServiceException($"'{name}' is used without its class (line {Lines(sm).RangeOf(site.Start, site.End).Start.Line + 1}).");
            AddEdit(changes, sm, new Edit(attr.Value.Start, attr.Value.End, to));
            if (!ReferenceEquals(sm, tm)) AddImport(changes, sm, tm.Name, to);
        }
        bool onlyPass = tcls.Body.Count == 1 && tcls.Body[0] is PassStmt;
        AddEdit(changes, tm, AtClassEnd(tm, tcls, MemberText(m, cls, member, BodyIndent(tm.Source, tcls)), onlyPass));
        foreach (var e in RemoveMembers(m, cls, new[] { member })) AddEdit(changes, m, e);
        return changes;
    }

    // ------------------------------------------------------------ Extract Delegate

    static string SnakeCase(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 && !char.IsUpper(name[i - 1]) ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

    Dictionary<ModuleInfo, List<Edit>> ExtractDelegate(ModuleInfo m, int offset, string? name, IReadOnlyList<string>? members)
    {
        var cls = ClassNamedAt(m, offset) ?? throw new ServiceException("Put the cursor on the name of a class.");
        if (m.ParentOf(cls) is not PyModule) throw new ServiceException("Only a class at the top of a file can be split this way.");
        string delegateClass = RequireName(name, "the new class");
        if (m.Semantics.ModuleScope.Symbols.ContainsKey(delegateClass)) throw new ServiceException($"The file has a '{delegateClass}' already.");
        var methods = ChosenMembers(cls, members, MovableMembers(cls).OfType<FunctionDef>()).OfType<FunctionDef>().ToList();
        if (methods.Count == 0) throw new ServiceException("Choose the methods that go to the new class.");
        var movedNames = methods.Select(x => x.Name).ToHashSet();
        foreach (var f in methods)
        {
            if (IsClassMethod(f)) throw new ServiceException($"'{f.Name}' is a class method; it cannot be delegated.");
            if (SelfName(f) is not { } self) continue;
            var used = AstChildren.Descendants(f).OfType<AttributeExpr>()
                .Where(a => a.Value is NameExpr n && n.Name == self && !movedNames.Contains(a.Attr)).Select(a => a.Attr).Distinct().ToList();
            if (used.Count > 0)
                throw new ServiceException($"'{f.Name}' uses {string.Join(", ", used.Select(x => $"self.{x}"))}, which stays in '{cls.Name}'; choose those methods too, or move the attributes first.");
        }
        string text = m.Source, field = SnakeCase(delegateClass);
        string indent = BodyIndent(text, cls), inner = indent + "    ";
        var edits = new List<Edit>
        {
            TopLevelInsert(m, cls, $"class {delegateClass}:\n" + JoinMembers(methods.Select(f => MemberText(m, cls, f, "    "))).TrimEnd('\n')),
        };
        // each moved method stays as a method that hands the call on
        foreach (var f in methods)
        {
            var args = new List<string>();
            bool keywordOnly = false;
            foreach (var p in f.Params.Skip(SelfName(f) != null ? 1 : 0))
            {
                switch (p.Kind)
                {
                    case ParamKind.Slash: break;
                    case ParamKind.KwOnlySeparator: keywordOnly = true; break;
                    case ParamKind.VarArgs: args.Add("*" + p.Name); keywordOnly = true; break;
                    case ParamKind.KwArgs: args.Add("**" + p.Name); break;
                    default: args.Add(keywordOnly ? $"{p.Name}={p.Name}" : p.Name); break;
                }
            }
            string self = SelfName(f) ?? "self";
            string call = IsStatic(f) ? $"{delegateClass}.{f.Name}({string.Join(", ", args)})" : $"{self}.{field}.{f.Name}({string.Join(", ", args)})";
            int bodyStart = LineStart(text, f.Body[0].Start);
            int bodyEnd = LineEndInclusive(text, CodeEnd(text, f.Body[^1].End));
            if (LineStart(text, f.Body[0].Start) <= LineStart(text, f.NameStart))
                throw new ServiceException($"'{f.Name}' is written on one line; split it first.");
            edits.Add(new Edit(bodyStart, bodyEnd, $"{LineIndent(text, f.Body[0].Start)}return {call}\n"));
        }
        // the new object is made in __init__
        var init = cls.Body.OfType<FunctionDef>().FirstOrDefault(x => x.Name == "__init__");
        if (methods.Any(f => !IsStatic(f)))
        {
            if (init != null && SelfName(init) is { } initSelf && init.Body.Count > 0)
            {
                int at = LineStart(text, init.Body[0].Start);
                if (init.Body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) at = LineEndInclusive(text, CodeEnd(text, init.Body[0].End));
                edits.Add(new Edit(at, at, $"{LineIndent(text, init.Body[0].Start)}{initSelf}.{field} = {delegateClass}()\n"));
            }
            else
            {
                int at = LineStart(text, cls.Body[0].Start);
                if (cls.Body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }) at = LineEndInclusive(text, CodeEnd(text, cls.Body[0].End));
                edits.Add(new Edit(at, at, $"{indent}def __init__(self):\n{inner}self.{field} = {delegateClass}()\n\n"));
            }
        }
        return new Dictionary<ModuleInfo, List<Edit>> { [m] = edits };
    }
}
