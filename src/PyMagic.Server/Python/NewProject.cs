using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>What the New Project dialog asks for.</summary>
/// <param name="Kind">python, fastapi, flask, django or jupyter.</param>
/// <param name="Environment">venv (a project .venv made by <paramref name="Python"/>), uv (`uv venv` with <paramref name="PythonVersion"/>),
/// interpreter (an existing one, such as conda's base: <paramref name="Python"/>), or none.</param>
public sealed record NewProjectRequest(string Location, string Kind, bool Git, bool Welcome, string Environment, string? Python, string? UvPath, string? PythonVersion);

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
    public static Dictionary<string, string> Files(string kind, string name, bool welcome)
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
                string secret = "django-insecure-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                files["manage.py"] = "#!/usr/bin/env python\n\"\"\"Django's command-line utility for administrative tasks.\"\"\"\nimport os\nimport sys\n\n\ndef main():\n    os.environ.setdefault(\"DJANGO_SETTINGS_MODULE\", \"" + module + ".settings\")\n    from django.core.management import execute_from_command_line\n\n    execute_from_command_line(sys.argv)\n\n\nif __name__ == \"__main__\":\n    main()\n";
                files[module + "/__init__.py"] = "";
                files[module + "/settings.py"] =
                    "from pathlib import Path\n\nBASE_DIR = Path(__file__).resolve().parent.parent\n\nSECRET_KEY = \"" + secret + "\"\n\nDEBUG = True\n\nALLOWED_HOSTS = []\n\n" +
                    "INSTALLED_APPS = [\n    \"django.contrib.admin\",\n    \"django.contrib.auth\",\n    \"django.contrib.contenttypes\",\n    \"django.contrib.sessions\",\n    \"django.contrib.messages\",\n    \"django.contrib.staticfiles\",\n]\n\n" +
                    "MIDDLEWARE = [\n    \"django.middleware.security.SecurityMiddleware\",\n    \"django.contrib.sessions.middleware.SessionMiddleware\",\n    \"django.middleware.common.CommonMiddleware\",\n    \"django.middleware.csrf.CsrfViewMiddleware\",\n    \"django.contrib.auth.middleware.AuthenticationMiddleware\",\n    \"django.contrib.messages.middleware.MessageMiddleware\",\n    \"django.middleware.clickjacking.XFrameOptionsMiddleware\",\n]\n\n" +
                    "ROOT_URLCONF = \"" + module + ".urls\"\n\n" +
                    "TEMPLATES = [\n    {\n        \"BACKEND\": \"django.template.backends.django.DjangoTemplates\",\n        \"DIRS\": [BASE_DIR / \"templates\"],\n        \"APP_DIRS\": True,\n        \"OPTIONS\": {\n            \"context_processors\": [\n                \"django.template.context_processors.request\",\n                \"django.contrib.auth.context_processors.auth\",\n                \"django.contrib.messages.context_processors.messages\",\n            ],\n        },\n    },\n]\n\n" +
                    "WSGI_APPLICATION = \"" + module + ".wsgi.application\"\n\n" +
                    "DATABASES = {\n    \"default\": {\n        \"ENGINE\": \"django.db.backends.sqlite3\",\n        \"NAME\": BASE_DIR / \"db.sqlite3\",\n    }\n}\n\n" +
                    "LANGUAGE_CODE = \"en-us\"\nTIME_ZONE = \"UTC\"\nUSE_I18N = True\nUSE_TZ = True\n\nSTATIC_URL = \"static/\"\n\nDEFAULT_AUTO_FIELD = \"django.db.models.BigAutoField\"\n";
                files[module + "/urls.py"] = "from django.contrib import admin\nfrom django.urls import path\n\nurlpatterns = [\n    path(\"admin/\", admin.site.urls),\n]\n";
                files[module + "/wsgi.py"] = "import os\n\nfrom django.core.wsgi import get_wsgi_application\n\nos.environ.setdefault(\"DJANGO_SETTINGS_MODULE\", \"" + module + ".settings\")\n\napplication = get_wsgi_application()\n";
                files[module + "/asgi.py"] = "import os\n\nfrom django.core.asgi import get_asgi_application\n\nos.environ.setdefault(\"DJANGO_SETTINGS_MODULE\", \"" + module + ".settings\")\n\napplication = get_asgi_application()\n";
                files["templates/.gitkeep"] = "";
                files["requirements.txt"] = "django\n";
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
        var files = Files(r.Kind, name, r.Welcome);
        if (r.Git) files[".gitignore"] = "__pycache__/\n*.py[cod]\n.venv/\n.pymagic/\n*.egg-info/\n.pytest_cache/\n" + (r.Kind == "django" ? "db.sqlite3\n" : "");

        Directory.CreateDirectory(folder);
        foreach (var (rel, text) in files)
        {
            string full = System.IO.Path.Combine(folder, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
        }
        var notes = new List<string>();
        if (r.Git)
        {
            try { Run(Environment.GetEnvironmentVariable("PYMAGIC_GIT") is { Length: > 0 } g ? g : "git", folder, "init"); }
            catch (ServiceException e) { notes.Add("Git: " + e.Message); }
        }

        string? interpreter = null;
        string venv = System.IO.Path.Combine(folder, ".venv");
        string venvPython = OperatingSystem.IsWindows() ? System.IO.Path.Combine(venv, "Scripts", "python.exe") : System.IO.Path.Combine(venv, "bin", "python");
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
                Run(string.IsNullOrWhiteSpace(r.UvPath) ? "uv" : r.UvPath.Trim(), folder, args.ToArray());
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
        if (interpreter != null)
        {
            if (!File.Exists(interpreter)) throw new ServiceException("The environment was made but has no interpreter.");
            string settings = System.IO.Path.Combine(folder, ".pymagic", "settings.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, new JsonObject { ["interpreter"] = interpreter }.ToJsonString() + "\n");
        }
        if (files.ContainsKey("requirements.txt"))
            notes.Add("The packages it needs are in requirements.txt; the Packages panel installs them.");
        return new NewProjectResult(folder, files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), interpreter, notes);
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
