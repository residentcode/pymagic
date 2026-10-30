using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>A place structural search found, with what replaces it when a replacement was given.</summary>
public sealed record StructuralMatch(string Uri, Range Range, string Text, string? Replacement);

/// <summary>Code that is written more than once: the places and how many lines each has.</summary>
public sealed record DuplicateGroup(int Lines, IReadOnlyList<Location> Places, string Preview);

/// <summary>
/// Structural search and replace: a pattern is Python with variables, `$x$` for one expression, statement or name, `$x*$` for
/// any number of arguments, parameters, items or statements, and `$x:regex$` for one whose text the regular expression finds.
/// A variable used twice must match the same text both times. And finding duplicated code: runs of statements whose shape is
/// the same once local names are numbered.
/// </summary>
public sealed partial class LanguageService
{
    static readonly Regex PatternVariable = new(@"\$(\w+)(\*)?(?::((?:[^$\\]|\\.)*))?\$", RegexOptions.Compiled);
    const string VarPrefix = "__ssr_";

    sealed record PatternVar(string Name, bool Many, Regex? Constraint);

    sealed class Pattern
    {
        public required Dictionary<string, PatternVar> Vars { get; init; }   // by the identifier that stands in for it
        public Expr? Expression { get; init; }
        public IReadOnlyList<Stmt>? Statements { get; init; }
    }

    static Pattern ParsePattern(string text)
    {
        var vars = new Dictionary<string, PatternVar>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        string code = PatternVariable.Replace(text, m =>
        {
            string name = m.Groups[1].Value;
            if (!byName.TryGetValue(name, out var id))
            {
                id = VarPrefix + name;
                byName[name] = id;
                Regex? constraint = null;
                if (m.Groups[3].Success)
                {
                    try { constraint = new Regex(m.Groups[3].Value.Replace("\\$", "$"), RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
                    catch (ArgumentException e) { throw new ServiceException($"The condition of ${name}$ is not a regular expression: {e.Message}"); }
                }
                vars[id] = new PatternVar(name, m.Groups[2].Success, constraint);
            }
            return id;
        });
        var parsed = Parser.Parse(code.Trim() + "\n");
        if (parsed.Diagnostics.Count > 0)
            throw new ServiceException($"The pattern is not Python: {parsed.Diagnostics[0].Message}.");
        var body = parsed.Module.Body;
        if (body.Count == 0) throw new ServiceException("The pattern is empty.");
        if (body is [ExprStmt { Value: var e }] && !(e is NameExpr n && vars.ContainsKey(n.Name)))
            return new Pattern { Vars = vars, Expression = e };
        return new Pattern { Vars = vars, Statements = body };
    }

    // ------------------------------------------------------------ comparing trees

    static readonly Dictionary<Type, PropertyInfo[]> ShapeProps = new();

    /// <summary>The properties of a node that make its shape: not its offsets.</summary>
    static PropertyInfo[] PropsOf(Type t)
    {
        lock (ShapeProps)
        {
            if (!ShapeProps.TryGetValue(t, out var props))
                ShapeProps[t] = props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.Name is not ("Start" or "End" or "NameStart") && p.GetIndexParameters().Length == 0).ToArray();
            return props;
        }
    }

    sealed class Matcher(Pattern pattern, string source)
    {
        public Dictionary<string, string> Bound = new(StringComparer.Ordinal);

        string Text(Node n) => source[n.Start..n.End];

        bool Bind(string id, string text)
        {
            var v = pattern.Vars[id];
            if (v.Constraint != null && !v.Constraint.IsMatch(text)) return false;
            if (Bound.TryGetValue(id, out var before)) return before == text;
            Bound[id] = text;
            return true;
        }

        /// <summary>The variable a pattern item stands for, when it is one: a name, an expression statement or argument of one, a parameter.</summary>
        string? VarOf(object? item) => item switch
        {
            NameExpr n when pattern.Vars.ContainsKey(n.Name) => n.Name,
            ExprStmt { Value: NameExpr n } when pattern.Vars.ContainsKey(n.Name) => n.Name,
            Argument { Name: null, Stars: 0, Value: NameExpr n } when pattern.Vars.ContainsKey(n.Name) => n.Name,
            Parameter { Annotation: null, Default: null } p when pattern.Vars.ContainsKey(p.Name) => p.Name,
            _ => null,
        };

        public bool Same(object? p, object? t)
        {
            if (p == null || t == null) return p == null && t == null;
            if (VarOf(p) is { } id && !pattern.Vars[id].Many)
            {
                // a variable for an expression takes any expression; for a statement any statement; for a parameter any parameter
                bool fits = p switch { NameExpr => t is Expr, ExprStmt => t is Stmt, Argument => t is Argument, Parameter => t is Parameter, _ => false };
                return fits && Bind(id, Text((Node)t));
            }
            if (p is string ps)
            {
                if (t is not string ts) return false;
                return pattern.Vars.ContainsKey(ps) ? Bind(ps, ts) : ps == ts;
            }
            if (p.GetType() != t.GetType()) return false;
            if (p is Node)
            {
                foreach (var prop in PropsOf(p.GetType()))
                    if (!Same(prop.GetValue(p), prop.GetValue(t))) return false;
                return true;
            }
            if (p is IList pl && t is IList tl) return SameList(pl, tl, 0, 0);
            return Equals(p, t);
        }

        /// <summary>Lists, where a $x*$ item takes any number of items (trying the fewest first).</summary>
        bool SameList(IList p, IList t, int i, int j)
        {
            if (i == p.Count) return j == t.Count;
            if (VarOf(p[i]) is { } id && pattern.Vars[id].Many)
            {
                for (int k = j; k <= t.Count; k++)
                {
                    var saved = new Dictionary<string, string>(Bound);
                    string text = k == j ? "" : source[((Node)t[j]!).Start..((Node)t[k - 1]!).End];
                    if (Bind(id, text) && SameList(p, t, i + 1, k)) return true;
                    Bound = saved;
                }
                return false;
            }
            if (j == t.Count) return false;
            var keep = new Dictionary<string, string>(Bound);
            if (Same(p[i], t[j]) && SameList(p, t, i + 1, j + 1)) return true;
            Bound = keep;
            return false;
        }

        /// <summary>Statements that match the pattern's statements from `start` on: how many they are, or -1.</summary>
        public int MatchRun(IReadOnlyList<Stmt> body, int start)
        {
            var p = pattern.Statements!;
            for (int end = start + 1; end <= body.Count; end++)
            {
                Bound = new(StringComparer.Ordinal);
                var window = body.Skip(start).Take(end - start).ToList();
                if (SameList((IList)p, window, 0, 0)) return end - start;
                // a run longer than the pattern needs only when the pattern has a $x*$ statement
                if (!p.Any(s => VarOf(s) is { } v && pattern.Vars[v].Many) && end - start >= p.Count) break;
            }
            return -1;
        }
    }

    /// <summary>The statement lists of a tree: module, class and function bodies, branches, loops, handlers.</summary>
    static IEnumerable<IReadOnlyList<Stmt>> StatementLists(Node root)
    {
        foreach (var n in AstChildren.Descendants(root))
            foreach (var prop in PropsOf(n.GetType()))
                if (prop.PropertyType == typeof(IReadOnlyList<Stmt>) && prop.GetValue(n) is IReadOnlyList<Stmt> { Count: > 0 } list)
                    yield return list;
    }

    // ------------------------------------------------------------ search

    public IReadOnlyList<StructuralMatch> StructuralSearch(string patternText, string? replacement, int limit = 2000)
    {
        var pattern = ParsePattern(patternText);
        var snap = Full();
        var found = new List<StructuralMatch>();
        foreach (var m in snap.Project.Where(p => InProject(snap, p)).OrderBy(p => p.Path, StringComparer.Ordinal))
        {
            if (found.Count >= limit) break;
            string uri = UriOfModule(m)!, src = m.Source;
            var lines = Lines(m);
            void Add(int start, int end, Dictionary<string, string> bound)
            {
                string? text = replacement == null ? null : Substitute(replacement, bound, LineIndent(src, start));
                int ls = LineStart(src, start);
                int le = src.IndexOfAny(new[] { '\r', '\n' }, start) is var x && x >= 0 ? x : src.Length;
                found.Add(new StructuralMatch(uri, lines.RangeOf(start, end), src[ls..le], text));
            }
            if (pattern.Expression is { } pe)
            {
                foreach (var n in AstChildren.Descendants(m.Ast))
                {
                    if (n.GetType() != pe.GetType()) continue;
                    var matcher = new Matcher(pattern, src);
                    if (matcher.Same(pe, n)) Add(n.Start, n.End, matcher.Bound);
                }
                continue;
            }
            foreach (var body in StatementLists(m.Ast))
            {
                for (int i = 0; i < body.Count; i++)
                {
                    var matcher = new Matcher(pattern, src);
                    int count = matcher.MatchRun(body, i);
                    if (count <= 0) continue;
                    Add(body[i].Start, TrueEnd(src, body[i + count - 1]), matcher.Bound);
                    i += count - 1;
                }
            }
        }
        return found;
    }

    /// <summary>The replacement with its variables filled in; its later lines take the indentation of the place it goes to.</summary>
    static string Substitute(string template, Dictionary<string, string> bound, string indent)
    {
        string text = PatternVariable.Replace(template, m => bound.GetValueOrDefault(VarPrefix + m.Groups[1].Value, m.Value));
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((l, i) => i == 0 || l.Length == 0 ? l : indent + l));
    }

    // ------------------------------------------------------------ duplicated code

    /// <summary>The shape of a statement as text: its kind and parts, with the names it uses numbered in the order they come.</summary>
    static string ShapeOf(Node root)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        void Walk(object? o)
        {
            switch (o)
            {
                case null: sb.Append('~'); break;
                case NameExpr n:
                    if (!names.TryGetValue(n.Name, out int k)) names[n.Name] = k = names.Count;
                    sb.Append('v').Append(k).Append(' ');
                    break;
                case Node node:
                    sb.Append(node.GetType().Name).Append('(');
                    foreach (var p in PropsOf(node.GetType())) { Walk(p.GetValue(node)); sb.Append(','); }
                    sb.Append(')');
                    break;
                case string s: sb.Append('"').Append(s).Append('"'); break;
                case IList list:
                    sb.Append('[');
                    foreach (var x in list) { Walk(x); sb.Append(';'); }
                    sb.Append(']');
                    break;
                default: sb.Append(o).Append(' '); break;
            }
        }
        Walk(root);
        return sb.ToString();
    }

    /// <summary>Runs of statements of at least `minLines` lines found more than once in the project, the biggest first.</summary>
    public IReadOnlyList<DuplicateGroup> Duplicates(int minLines = 6, int limit = 100)
    {
        var snap = Full();
        var groups = new Dictionary<string, List<(ModuleInfo M, int Start, int End, int Lines)>>(StringComparer.Ordinal);
        foreach (var m in snap.Project.Where(p => InProject(snap, p)))
        {
            string src = m.Source;
            int LineOf(int offset) => src.AsSpan(0, Math.Min(offset, src.Length)).Count('\n');
            foreach (var body in StatementLists(m.Ast))
            {
                var shapes = body.Select(s => ShapeOf(s)).ToList();
                for (int i = 0; i < body.Count; i++)
                {
                    var key = new StringBuilder();
                    for (int j = i; j < body.Count && j < i + 40; j++)
                    {
                        key.Append(shapes[j]).Append('\n');
                        int end = TrueEnd(src, body[j]);
                        int lines = LineOf(end) - LineOf(body[i].Start) + 1;
                        if (lines < minLines) continue;
                        string k = key.ToString();
                        if (!groups.TryGetValue(k, out var list)) groups[k] = list = new();
                        list.Add((m, body[i].Start, end, lines));
                    }
                }
            }
        }
        // keep the groups that are found twice, without overlaps, and that no bigger group holds entirely
        var kept = new List<List<(ModuleInfo M, int Start, int End, int Lines)>>();
        foreach (var g in groups.Values.OrderByDescending(g => g[0].Lines))
        {
            var places = new List<(ModuleInfo M, int Start, int End, int Lines)>();
            foreach (var p in g)
                if (!places.Any(q => ReferenceEquals(q.M, p.M) && q.Start < p.End && p.Start < q.End)) places.Add(p);
            if (places.Count < 2) continue;
            bool inside = places.All(p => kept.Any(k => k.Any(q => ReferenceEquals(q.M, p.M) && q.Start <= p.Start && p.End <= q.End)));
            if (inside) continue;
            kept.Add(places);
            if (kept.Count >= limit) break;
        }
        return kept.Select(g =>
        {
            var first = g[0];
            string preview = first.M.Source[first.Start..first.End].Split('\n')[0].Trim();
            return new DuplicateGroup(first.Lines, g.Select(p => new Location(UriOfModule(p.M)!, Lines(p.M).RangeOf(p.Start, p.End))).ToList(), preview);
        }).ToList();
    }
}
