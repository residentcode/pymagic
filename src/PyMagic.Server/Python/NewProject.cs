using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>What the New Project dialog asks for.</summary>
/// <param name="Kind">python, fastapi, flask, django or jupyter.</param>
/// <param name="Environment">venv (a project .venv made by <paramref name="Python"/>), uv (`uv venv` with <paramref name="PythonVersion"/>),
/// interpreter (an existing one, such as conda's base: <paramref name="Python"/>), or none.</param>
/// <param name="InstallPackages">In a new environment: install what the project needs (a download from PyPI): Django, so its own startproject writes the project; with uv, the packages its kind needs (uv add).</param>
public sealed record NewProjectRequest(string Location, string Kind, bool Git, bool Welcome, string Environment, string? Python, string? UvPath, string? PythonVersion,
    DjangoProjectOptions? Django = null, bool InstallPackages = true);

/// <summary>The Django choices of New Project: the template language (django or jinja2), the name of the templates folder, a first app, and the admin.</summary>
public sealed record DjangoProjectOptions(string TemplateLanguage = "django", string TemplatesFolder = "templates", string? AppName = null, bool Admin = true);

/// <summary>What was made: the folder, the files written, and the interpreter the project uses (or null).</summary>
public sealed record NewProjectResult(string Folder, IReadOnlyList<string> Files, string? Interpreter, IReadOnlyList<string> Notes);

/// <summary>
/// Makes a new project, as PyCharm's New Project dialog does: the folder, the starting files of its kind, a Git repository and
/// its .gitignore, and its environment. The packages a kind needs are listed in requirements.txt; nothing is downloaded here.
/// </summary>
public static class NewProject
{
    public static readonly string[] Kinds = { "python", "fastapi", "flask", "django", "jupyter" };

    /// <summary>The files of a kind of project, by path, for a project named <paramref name="name"/>.</summary>
    /// <param name="python">An interpreter that may already have Django: its own startproject writes a Django project.</param>
    public static Dictionary<string, string> Files(string kind, string name, bool welcome, DjangoProjectOptions? django = null, string? python = null)
    {
        string module = ModuleName(name);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (kind)
        {
            case "python":
                if (welcome)
                    files["main.py"] = "def main():\n    print(\"Hello from " + name.Replace("\"", "'") + "!\")\n\n\nif __name__ == \"__main__\":\n    main()\n";
                break;
            case "fastapi":
                files["main.py"] = "from fastapi import FastAPI\n\napp = FastAPI()\n\n\n@app.get(\"/\")\nasync def root():\n    return {\"message\": \"Hello World\"}\n\n\n@app.get(\"/hello/{name}\")\nasync def say_hello(name: str):\n    return {\"message\": f\"Hello {name}\"}\n";
                files["test_main.http"] = "# Requests to try while the server runs (uvicorn main:app --reload)\n\nGET http://127.0.0.1:8000/\nAccept: application/json\n\n###\n\nGET http://127.0.0.1:8000/hello/User\nAccept: application/json\n";
                files["requirements.txt"] = "fastapi\nuvicorn[standard]\n";
                break;
            case "flask":
                files["app.py"] = "from flask import Flask\n\napp = Flask(__name__)\n\n\n@app.route(\"/\")\ndef hello_world():\n    return \"Hello World!\"\n\n\nif __name__ == \"__main__\":\n    app.run()\n";
                files["templates/.gitkeep"] = "";
                files["static/.gitkeep"] = "";
                files["requirements.txt"] = "flask\n";
                break;
            case "django":
                foreach (var (path, text) in DjangoFiles(module, django ?? new DjangoProjectOptions(), python)) files[path] = text;
                break;
            case "jupyter":
                files["notebook.ipynb"] = new JsonObject
                {
                    ["cells"] = new JsonArray(
                        new JsonObject { ["cell_type"] = "markdown", ["metadata"] = new JsonObject(), ["source"] = new JsonArray("# " + name) },
                        new JsonObject { ["cell_type"] = "code", ["execution_count"] = null, ["metadata"] = new JsonObject(), ["outputs"] = new JsonArray(), ["source"] = new JsonArray("print(\"Hello from Jupyter\")") }),
                    ["metadata"] = new JsonObject { ["kernelspec"] = new JsonObject { ["display_name"] = "Python 3", ["language"] = "python", ["name"] = "python3" } },
                    ["nbformat"] = 4,
                    ["nbformat_minor"] = 5,
                }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
                files["requirements.txt"] = "jupyter\n";
                break;
            default:
                throw new ServiceException($"'{kind}' is not a kind of project PyMagic makes.");
        }
        return files;
    }

    /// <summary>
    /// A Django project as startproject makes it, comments and docstrings included, with the choices of the dialog: Django's own templates
    /// (DjangoTemplates), or what the chosen interpreter's Django writes when it has one (<paramref name="python"/>).
    /// </summary>
    static Dictionary<string, string> DjangoFiles(string module, DjangoProjectOptions o, string? python = null)
    {
        bool jinja = o.TemplateLanguage == "jinja2";
        string folder = System.Text.RegularExpressions.Regex.IsMatch(o.TemplatesFolder ?? "", @"^[\w.-]+(/[\w.-]+)*$") ? o.TemplatesFolder! : "templates";
        string? app = string.IsNullOrWhiteSpace(o.AppName) ? null : ModuleName(o.AppName!);
        if (app == module) throw new ServiceException("The app needs another name than the project's package.");
        var files = python != null && DjangoTemplates.VersionOf(python) != null ? DjangoTemplates.FromDjango(python, module, app) : DjangoTemplates.Builtin(module, app);
        DjangoTemplates.Apply(files, module, app, folder, jinja, o.Admin);
        files[folder + "/.gitkeep"] = "";
        files["requirements.txt"] = jinja ? "django\njinja2\n" : "django\n";
        return files;
    }

    /// <summary>A Python package name from a folder name: `My Site` gives `my_site`.</summary>
    public static string ModuleName(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '_').ToArray();
        string s = new string(chars).Trim('_');
        if (s.Length == 0) s = "project";
        return char.IsDigit(s[0]) ? "_" + s : s;
    }

    public static NewProjectResult Create(NewProjectRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Location) || !System.IO.Path.IsPathFullyQualified(r.Location))
            throw new ServiceException("Give the project's folder as a full path.");
        string folder = System.IO.Path.GetFullPath(r.Location.Trim());
        if (File.Exists(folder)) throw new ServiceException("There is a file of that name.");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new ServiceException("The folder is not empty.");
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        Directory.CreateDirectory(folder);
        var notes = new List<string>();

        // the environment first: a Django project is then written by that environment's own Django
        string? interpreter = null;
        string venv = System.IO.Path.Combine(folder, ".venv");
        string venvPython = OperatingSystem.IsWindows() ? System.IO.Path.Combine(venv, "Scripts", "python.exe") : System.IO.Path.Combine(venv, "bin", "python");
        string uv = string.IsNullOrWhiteSpace(r.UvPath) ? "uv" : r.UvPath.Trim();
        switch (r.Environment)
        {
            case "venv":
                if (string.IsNullOrEmpty(r.Python)) throw new ServiceException("Choose the Python the environment is made with.");
                Run(r.Python, folder, "-m", "venv", venv);
                interpreter = venvPython;
                break;
            case "uv":
                var args = new List<string> { "venv" };
                if (!string.IsNullOrWhiteSpace(r.PythonVersion)) { args.Add("--python"); args.Add(r.PythonVersion.Trim()); }
                args.Add(venv);
                Run(uv, folder, args.ToArray());
                interpreter = venvPython;
                break;
            case "interpreter":
                if (string.IsNullOrEmpty(r.Python) || !File.Exists(r.Python)) throw new ServiceException("Choose an interpreter that exists.");
                interpreter = r.Python;
                break;
            case "none":
                break;
            default:
                throw new ServiceException($"'{r.Environment}' is not a kind of environment.");
        }
        if (interpreter != null && !File.Exists(interpreter)) throw new ServiceException("The environment was made but has no interpreter.");

        // uv: a uv project, as `uv init` makes one: pyproject.toml, and the packages of its kind added with `uv add` (pyproject.toml lists them,
        // uv.lock pins them, .venv has them; a download from PyPI). Offline, they are written into pyproject.toml only (`uv add --frozen`)
        bool uvProject = r.Environment == "uv";
        if (uvProject)
        {
            var init = new List<string> { "init", "--bare", "--no-workspace", "--vcs", "none", "--name", ModuleName(name) };
            if (!string.IsNullOrWhiteSpace(r.PythonVersion)) { init.Add("--python"); init.Add(r.PythonVersion.Trim()); }
            Run(uv, folder, init.ToArray());
            // an application, not a package to build (uv's own setting for that); it also says the project is uv's before uv.lock is there (offline)
            try { File.AppendAllText(System.IO.Path.Combine(folder, "pyproject.toml"), "\n[tool.uv]\npackage = false\n"); }
            catch (IOException) { }
            var packages = UvProject.PackagesOf(r.Kind, r.Django?.TemplateLanguage == "jinja2");
            if (packages.Count > 0)
            {
                try
                {
                    if (!r.InstallPackages) throw new ServiceException("installing was left out");
                    Run(uv, folder, UvProject.AddArgs(packages, false).ToArray());
                    notes.Add($"uv added {string.Join(", ", packages)}: pyproject.toml lists them, uv.lock pins them, and .venv has them.");
                }
                catch (ServiceException e)
                {
                    var frozen = new List<string> { "add", "--frozen" };
                    frozen.AddRange(packages);
                    Run(uv, folder, frozen.ToArray());
                    notes.Add($"pyproject.toml lists {string.Join(", ", packages)}, but they are not installed ({e.Message}): `uv sync` (the lock file's Sync in the Packages panel) installs them.");
                }
            }
        }

        // Django: the project's own environment gets Django (from PyPI; with uv, by the uv add above), then Django's startproject writes the project;
        // an interpreter that was chosen as it is is used when it has Django and left alone when it does not; without Django, Django's own templates are
        // written by PyMagic
        string? django = null;
        if (r.Kind == "django" && interpreter != null)
        {
            django = DjangoTemplates.VersionOf(interpreter);
            if (django == null && r.Environment == "venv" && r.InstallPackages)
            {
                try
                {
                    Run(interpreter, folder, "-m", "pip", "install", "--disable-pip-version-check", "django");
                    django = DjangoTemplates.VersionOf(interpreter);
                    if (django != null) notes.Add($"Django {django} was installed in .venv; django-admin startproject wrote the project.");
                }
                catch (ServiceException e) { notes.Add("Django could not be installed (" + e.Message + "): the project is Django " + DjangoTemplates.Version + "'s own template, and requirements.txt lists it for the Packages panel."); }
            }
            else if (django != null) notes.Add($"django-admin startproject of Django {django} wrote the project.");
        }
        var files = Files(r.Kind, name, r.Welcome, r.Django, django != null ? interpreter : null);
        // a uv project's packages are in pyproject.toml
        if (uvProject) files.Remove("requirements.txt");
        if (r.Git) files[".gitignore"] = "__pycache__/\n*.py[cod]\n.venv/\n.pymagic/\n*.egg-info/\n.pytest_cache/\n" + (r.Kind == "django" ? "db.sqlite3\n" : "");

        foreach (var (rel, text) in files)
        {
            string full = System.IO.Path.Combine(folder, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
        }
        if (r.Git)
        {
            try { Run(Environment.GetEnvironmentVariable("PYMAGIC_GIT") is { Length: > 0 } g ? g : "git", folder, "init"); }
            catch (ServiceException e) { notes.Add("Git: " + e.Message); }
        }
        if (interpreter != null)
        {
            string settings = System.IO.Path.Combine(folder, ".pymagic", "settings.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, new JsonObject { ["interpreter"] = interpreter }.ToJsonString() + "\n");
        }
        if (files.ContainsKey("requirements.txt") && django == null)
            notes.Add("The packages it needs are in requirements.txt; the Packages panel installs them.");
        // what uv wrote is part of the project too
        var made = files.Keys.ToList();
        foreach (var own in new[] { "pyproject.toml", "uv.lock", ".python-version" })
            if (!files.ContainsKey(own) && File.Exists(System.IO.Path.Combine(folder, own))) made.Add(own);
        return new NewProjectResult(folder, made.OrderBy(k => k, StringComparer.Ordinal).ToList(), interpreter, notes);
    }

    static void Run(string program, string folder, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new ServiceException("cannot start " + program);
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(300000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException(program + " took too long"); }
            if (p.ExitCode != 0) throw new ServiceException($"{System.IO.Path.GetFileName(program)} {string.Join(' ', args.Take(2))} failed: " + (err.Result.Trim().Split('\n').LastOrDefault()?.Trim() ?? "exit code " + p.ExitCode));
        }
        catch (System.ComponentModel.Win32Exception e) { throw new ServiceException($"cannot start {program}: {e.Message}"); }
    }
}
