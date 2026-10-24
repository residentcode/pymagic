using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Unresolved imports: modules that cannot be found, names a module does not have, and packages that are not installed.</summary>
public sealed partial class LanguageService
{
    /// <summary>
    /// Report imports that cannot be resolved. Only active with an interpreter selected, because only then is it known which
    /// packages are installed and which names belong to the standard library.
    /// </summary>
    public bool ReportUnresolvedImports { get; set; } = true;

    enum ImportProblemKind { MissingModule, MissingName, NotInstalled }

    /// <param name="Module">The module that is missing, or the module a name could not be imported from.</param>
    /// <param name="Name">For MissingName: the name.</param>
    /// <param name="StubsDistribution">For NotInstalled: the typeshed stub distribution that supplied the type information.</param>
    sealed record ImportProblem(ImportProblemKind Kind, int Start, int End, string Module, string? Name, string? StubsDistribution)
    {
        public string TopLevel => Module.Split('.')[0];
        public string Message => Kind switch
        {
            ImportProblemKind.MissingModule => $"No module named '{Module}'",
            ImportProblemKind.MissingName => $"Cannot import name '{Name}' from '{Module}'",
            _ => $"Package '{TopLevel}' is not installed",
        };
        public string Code => Kind switch
        {
            ImportProblemKind.MissingModule => "unresolved-import",
            ImportProblemKind.MissingName => "unresolved-import-name",
            _ => "package-not-installed",
        };
    }

    /// <summary>Import name -> the name of the package on PyPI, where they differ.</summary>
    static readonly Dictionary<string, string> PackageNames = new(StringComparer.Ordinal)
    {
        ["yaml"] = "PyYAML", ["PIL"] = "Pillow", ["cv2"] = "opencv-python", ["sklearn"] = "scikit-learn", ["skimage"] = "scikit-image",
        ["bs4"] = "beautifulsoup4", ["dateutil"] = "python-dateutil", ["dotenv"] = "python-dotenv", ["jwt"] = "PyJWT", ["attr"] = "attrs",
        ["serial"] = "pyserial", ["Crypto"] = "pycryptodome", ["OpenSSL"] = "pyOpenSSL", ["git"] = "GitPython", ["magic"] = "python-magic",
        ["docx"] = "python-docx", ["pptx"] = "python-pptx", ["zmq"] = "pyzmq", ["usb"] = "pyusb", ["psycopg2"] = "psycopg2-binary",
        ["MySQLdb"] = "mysqlclient", ["fitz"] = "PyMuPDF", ["win32api"] = "pywin32", ["win32con"] = "pywin32", ["win32com"] = "pywin32",
        ["google.protobuf"] = "protobuf", ["markdown"] = "Markdown", ["jose"] = "python-jose", ["slugify"] = "python-slugify",
        ["socks"] = "PySocks", ["xdist"] = "pytest-xdist", ["ruamel"] = "ruamel.yaml", ["Xlib"] = "python-xlib", ["gi"] = "PyGObject",
        ["wx"] = "wxPython", ["lxml"] = "lxml", ["yaml_include"] = "pyyaml-include", ["tomllib"] = "tomli", ["pkg_resources"] = "setuptools",
    };

    // ------------------------------------------------------------ detection

    List<ImportProblem> ImportProblems(ModuleInfo m, TypeEvaluator ev)
    {
        var result = new List<ImportProblem>();
        if (Interpreter == null || !ReportUnresolvedImports || m.IsStub) return result;
        var ws = ev.Workspace;

        // imports in branches that the interpreter's version or platform rules out are never bound: leave them alone
        var live = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        foreach (var scope in AllScopes(m.Semantics.ModuleScope))
        {
            foreach (var sym in scope.Symbols.Values)
                foreach (var d in sym.Declarations)
                    if (d.Kind == DeclKind.Import) live.Add(d.Node);
            foreach (var star in scope.StarImports) live.Add(star);
        }

        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case ImportStmt s:
                    foreach (var alias in s.Names)
                        if (live.Contains(alias) && !GuardedByImportError(m, s))
                            CheckDotted(m, ws, alias.Name, alias.Start, alias.Start + alias.Name.Length, InTypeChecking(m, s), result);
                    break;

                case ImportFromStmt f when (f.Names.Count > 0 && f.Names.Any(a => live.Contains(a))) || (f.IsStar && live.Contains(f)):
                    if (!GuardedByImportError(m, f)) CheckFrom(m, ws, ev, f, live, result);
                    break;
            }
        }
        return result;
    }

    void CheckDotted(ModuleInfo m, Workspace ws, string name, int start, int end, bool typeChecking, List<ImportProblem> into)
    {
        var parts = name.Split('.');
        for (int k = 1; k <= parts.Length; k++)
        {
            string prefix = string.Join('.', parts.Take(k));
            if (!ModuleExists(ws, prefix))
            {
                into.Add(new ImportProblem(ImportProblemKind.MissingModule, start, end, prefix, null, null));
                return;
            }
        }
        if (!typeChecking && NotInstalled(ws, parts[0]) is { } stubs) into.Add(new ImportProblem(ImportProblemKind.NotInstalled, start, end, parts[0], null, stubs));
    }

    void CheckFrom(ModuleInfo m, Workspace ws, TypeEvaluator ev, ImportFromStmt f, HashSet<Node> live, List<ImportProblem> into)
    {
        string? baseName = Workspace.AbsoluteName(m, f.Level, f.Module);
        if (baseName == null) return;

        // `from . import x` next to a script: the names are modules of the folder
        if (baseName.Length == 0)
        {
            foreach (var alias in f.Names)
                if (live.Contains(alias) && !ModuleExists(ws, alias.Name))
                    into.Add(new ImportProblem(ImportProblemKind.MissingModule, alias.Start, alias.Start + alias.Name.Length, alias.Name, null, null));
            return;
        }

        // the module part: located in the text, since the syntax tree keeps only the name
        var header = Regex.Match(m.Source[f.Start..Math.Min(f.End, m.Source.Length)], @"^\s*from\s+(?<dots>\.*)\s*(?<name>[\w.]*)");
        int nameStart = header.Success ? f.Start + header.Groups["name"].Index : f.Start;
        int nameEnd = nameStart + (header.Success ? header.Groups["name"].Length : 0);
        if (header.Success && header.Groups["name"].Length == 0) { nameStart = f.Start; nameEnd = f.Start + header.Length; }

        if (f.Level == 0)
        {
            var parts = baseName.Split('.');
            for (int k = 1; k <= parts.Length; k++)
            {
                string prefix = string.Join('.', parts.Take(k));
                if (!ModuleExists(ws, prefix))
                {
                    into.Add(new ImportProblem(ImportProblemKind.MissingModule, nameStart, nameEnd, prefix, null, null));
                    return;
                }
            }
            if (!InTypeChecking(m, f) && NotInstalled(ws, parts[0]) is { } stubs)
            {
                into.Add(new ImportProblem(ImportProblemKind.NotInstalled, nameStart, nameEnd, parts[0], null, stubs));
                return;
            }
        }
        else if (!ModuleExists(ws, baseName))
        {
            into.Add(new ImportProblem(ImportProblemKind.MissingModule, nameStart, nameEnd, baseName, null, null));
            return;
        }

        // the names: they must exist in the module (or be one of its submodules)
        var module = ws.GetModule(baseName);
        if (module == null || f.IsStar || IsDynamicModule(module)) return;
        foreach (var alias in f.Names)
        {
            if (!live.Contains(alias)) continue;
            bool found = ev.FindModuleSymbol(module, alias.Name) != null
                || ws.GetModule(baseName + "." + alias.Name) != null
                || CompiledModuleExists(baseName + "." + alias.Name);
            if (!found)
                into.Add(new ImportProblem(ImportProblemKind.MissingName, alias.Start, alias.Start + alias.Name.Length, baseName, alias.Name, null));
        }
    }

    /// <summary>A module that can provide names its source does not spell out (so a missing name proves nothing).</summary>
    static bool IsDynamicModule(ModuleInfo module)
    {
        var scope = module.Semantics.ModuleScope;
        if (scope.Symbols.ContainsKey("__getattr__") || scope.HasStarImport) return true;
        return !module.IsStub && (module.Source.Contains("globals()") || module.Source.Contains("sys.modules") || module.Source.Contains("setattr("));
    }

    bool ModuleExists(Workspace ws, string name)
    {
        if (ws.GetModule(name) != null) return true;
        if (Interpreter!.StdlibModules.Contains(name.Split('.')[0])) return true;   // the standard library, even without stubs for it
        return CompiledModuleExists(name);
    }

    /// <summary>An extension module (.pyd / .so) has no source to parse but is importable.</summary>
    bool CompiledModuleExists(string name)
    {
        var parts = name.Split('.');
        foreach (var root in SearchRoots())
        {
            string folder = Path.Combine(new[] { root }.Concat(parts.Take(parts.Length - 1)).ToArray());
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, parts[^1] + ".*"))
                {
                    string ext = Path.GetExtension(file);
                    if (ext.Equals(".pyd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".so", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    IEnumerable<string> SearchRoots() => SourceRoots.Concat(ImplicitSourceRoots()).Concat(EffectiveLibraryRoots());

    /// <summary>
    /// A module whose type information comes from typeshed but that is not a standard-library module and not installed:
    /// the editor can complete it, Python cannot import it. Returns the stub distribution (or the module name).
    /// </summary>
    string? NotInstalled(Workspace ws, string topLevel)
    {
        if (topLevel.StartsWith("_typeshed", StringComparison.Ordinal) || Interpreter!.StdlibModules.Contains(topLevel)) return null;
        var module = ws.GetModule(topLevel);
        if (module is not { Kind: ResolutionKind.Typeshed, Path: { } path }) return null;
        if (InstalledOnDisk(topLevel)) return null;

        string[] segments = path.Replace('\\', '/').Split('/');
        int i = Array.LastIndexOf(segments, "stubs");
        return i >= 0 && i + 1 < segments.Length ? segments[i + 1] : topLevel;
    }

    bool InstalledOnDisk(string topLevel)
    {
        foreach (var root in SearchRoots())
        {
            if (Directory.Exists(Path.Combine(root, topLevel)) || File.Exists(Path.Combine(root, topLevel + ".py"))) return true;
        }
        return CompiledModuleExists(topLevel);
    }

    // ------------------------------------------------------------ context

    static bool GuardedByImportError(ModuleInfo m, Node import)
    {
        Node child = import;
        for (var parent = m.ParentOf(child); parent != null; child = parent, parent = m.ParentOf(parent))
            if (parent is TryStmt t && t.Body.Any(b => ReferenceEquals(b, child)) && t.Handlers.Any(CatchesImportError)) return true;
        return false;
    }

    static bool CatchesImportError(ExceptHandler h)
    {
        if (h.Type == null) return true;
        IEnumerable<Expr> types = h.Type is TupleExpr tuple ? tuple.Items : new[] { h.Type };
        return types.Any(t => (t is NameExpr n ? n.Name : t is AttributeExpr a ? a.Attr : "") is "ImportError" or "ModuleNotFoundError" or "Exception" or "BaseException");
    }

    static bool InTypeChecking(ModuleInfo m, Node import)
    {
        Node child = import;
        for (var parent = m.ParentOf(child); parent != null; child = parent, parent = m.ParentOf(parent))
            if (parent is IfStmt i && i.Body.Any(b => ReferenceEquals(b, child))
                && (i.Test is NameExpr { Name: "TYPE_CHECKING" } || i.Test is AttributeExpr { Attr: "TYPE_CHECKING" })) return true;
        return false;
    }

    /// <summary>Folders next to open files that are under no configured root: Python puts a script's own folder on sys.path.</summary>
    IEnumerable<string> ImplicitSourceRoots()
    {
        var roots = SourceRoots.Concat(EffectiveLibraryRoots()).Select(r => System.IO.Path.GetFullPath(r).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar).ToList();
        return _docs.Values.Where(d => d.Path != null)
            .Select(d => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(d.Path!))!)
            .Where(dir => dir != null && !roots.Any(r => (dir + System.IO.Path.DirectorySeparatorChar).StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ quick fixes

    void ImportFixes(Snapshot snap, ModuleInfo m, TypeEvaluator ev, int offset, List<CodeAction> actions, Action<string, bool, Edit[]> addEdit)
    {
        var problem = ImportProblems(m, ev).FirstOrDefault(p => p.Start == offset);
        if (problem == null) return;

        // the missing top-level module or uninstalled package: install it
        if (problem.Kind == ImportProblemKind.NotInstalled || (problem.Kind == ImportProblemKind.MissingModule && !problem.Module.Contains('.')))
        {
            string package = SuggestPackage(problem, out bool declared);
            actions.Add(new CodeAction($"Install package '{package}'" + (declared ? " (listed in the project's requirements)" : ""), QuickFixKind, null,
                IsPreferred: true, Command: new LspCommand($"Install {package}", "pymagic.installPackage", new object[] { package })));
        }

        // a likely typo
        if (problem.Kind == ImportProblemKind.MissingModule && !problem.Module.Contains('.'))
            foreach (var near in Closest(problem.Module, ImportableTopLevelNames(snap), 3))
                addEdit($"Change to '{near}'", false, new[] { new Edit(problem.Start, problem.Start + problem.Module.Length, near) });
        else if (problem.Kind == ImportProblemKind.MissingName && snap.Workspace.GetModule(problem.Module) is { } module)
        {
            var names = snap.Workspace.Evaluator.MemberNames(new ModuleType(module)).Select(x => x.Name).Where(x => !x.StartsWith('_'));
            foreach (var near in Closest(problem.Name!, names, 3))
                addEdit($"Change to '{near}'", false, new[] { new Edit(problem.Start, problem.End, near) });
        }
    }

    /// <summary>Names that `import x` could mean: the standard library, the project's modules and what is installed.</summary>
    IEnumerable<string> ImportableTopLevelNames(Snapshot snap)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in Interpreter!.StdlibModules) if (!n.StartsWith('_')) names.Add(n);
        foreach (var project in snap.Project) names.Add(project.Name.Split('.')[0]);
        foreach (var root in SearchRoots())
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                {
                    string leaf = System.IO.Path.GetFileName(entry);
                    string stem = leaf.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? leaf[..^3] : Directory.Exists(entry) ? leaf : "";
                    if (stem.Length > 0 && !stem.StartsWith('_') && !stem.StartsWith('.') && Regex.IsMatch(stem, @"^[A-Za-z_]\w*$")) names.Add(stem);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return names;
    }

    // ------------------------------------------------------------ what to install

    /// <summary>The package to offer for an import: one the project already declares, else typeshed's distribution, else the usual name.</summary>
    string SuggestPackage(ImportProblem problem, out bool declared)
    {
        string top = problem.TopLevel;
        string guess = problem.StubsDistribution ?? (PackageNames.TryGetValue(problem.Module, out var dotted) ? dotted : PackageNames.GetValueOrDefault(top, top));
        declared = false;
        foreach (var root in SourceRoots.Take(1))
            foreach (var file in RequirementsScanner.Scan(root))
                foreach (var r in file.Requirements)
                {
                    string n = PackageManager.Normalize(r.Name);
                    if (n == PackageManager.Normalize(guess) || n == PackageManager.Normalize(top)) { declared = true; return r.Name; }
                }
        return guess;
    }
}
