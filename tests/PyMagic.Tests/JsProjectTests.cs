using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>New Project's other kinds (HTML5 Boilerplate, Bootstrap, Vite, React Native, Next.js, Node.js, Express): what each generator is asked, and what is made without one.</summary>
public class JsProjectTests
{
    static string Folder(string name = "shop") => Path.Combine(Path.GetTempPath(), "pm_js_" + Guid.NewGuid().ToString("N"), name);

    [Fact]
    public void EachGeneratorIsAskedForTheChoicesMade()
    {
        string f = Folder("my-site");
        Assert.Equal("--yes create-vite@latest . --template react-ts --no-interactive", string.Join(" ", JsProjects.GeneratorArgs("vite", f, new JsProjectOptions(Template: "react-ts"), true)!));
        Assert.Equal("--yes create-vite@latest . --template vanilla --no-interactive", string.Join(" ", JsProjects.GeneratorArgs("vite", f, new JsProjectOptions(Template: "evil; rm"), true)!));
        Assert.Equal("--yes create-next-app@15.1.0 . --js --no-eslint --tailwind --no-app --src-dir --import-alias @/* --use-npm --disable-git --yes --skip-install",
            string.Join(" ", JsProjects.GeneratorArgs("nextjs", f, new JsProjectOptions(Version: "15.1.0", TypeScript: false, Eslint: false, AppRouter: false, SrcDir: true), false)!));
        Assert.Equal("--yes create-next-app@latest . --ts --eslint --tailwind --app --no-src-dir --import-alias @/* --use-npm --disable-git --yes",
            string.Join(" ", JsProjects.GeneratorArgs("nextjs", f, new JsProjectOptions(Version: "1.0; x"), true)!));
        Assert.Equal($"--yes @react-native-community/cli@latest init MySite --directory {f} --skip-git-init --pm npm --version 0.76.0",
            string.Join(" ", JsProjects.GeneratorArgs("reactnative", f, new JsProjectOptions(Version: "0.76.0"), true)!));
        Assert.Equal("--yes express-generator@latest --view=ejs .", string.Join(" ", JsProjects.GeneratorArgs("express", f, new JsProjectOptions(View: "ejs"), true)!));
        Assert.Equal("--yes express-generator@latest --no-view .", string.Join(" ", JsProjects.GeneratorArgs("express", f, new JsProjectOptions(View: "none"), true)!));
        A.Null(JsProjects.GeneratorArgs("nodejs", f, new JsProjectOptions(), true));
        Assert.True(JsProjects.InstallsAfter("vite") && JsProjects.InstallsAfter("express") && !JsProjects.InstallsAfter("nextjs"));
    }

    [Fact]
    public void NamesAndVersionsAsNpmAndReactNativeWantThem()
    {
        Assert.Equal("my-site", JsProjects.PackageName("My Site"));
        Assert.Equal("project", JsProjects.PackageName("!!!"));
        Assert.Equal("MySite", JsProjects.AppName("my site"));
        Assert.Equal("App2go", JsProjects.AppName("2go"));
        Assert.Equal("9.0.1,5.3.3,5.0.0", string.Join(",", JsProjects.StableVersions(new[] { "5.0.0", "9.0.1", "5.3.3", "6.0.0-beta.1", "x" })));
        Assert.Equal("bootstrap", JsProjects.VersionPackage("bootstrap"));
        A.Null(JsProjects.VersionPackage("vite"));
    }

    [Fact]
    public void APackagesDistFolderIsUnpackedAndNothingOutsideTheFolder()
    {
        string dir = Folder();
        Directory.CreateDirectory(dir);
        string tgz = Path.Combine(Path.GetDirectoryName(dir)!, "p.tgz");
        try
        {
            using (var file = File.Create(tgz))
            using (var gz = new GZipStream(file, CompressionLevel.Fastest))
            using (var tar = new System.Formats.Tar.TarWriter(gz))
            {
                void Add(string name, string text)
                {
                    var e = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) };
                    tar.WriteEntry(e);
                }
                Add("package/dist/index.html", "<p>hi</p>");
                Add("package/dist/css/style.css", "p{}");
                Add("package/README.md", "not this");
                Add("package/dist/../../evil.txt", "no");
            }
            Assert.Equal(2, JsProjects.ExtractUnder(tgz, "package/dist/", dir));
            Assert.Equal("<p>hi</p>", File.ReadAllText(Path.Combine(dir, "index.html")));
            Assert.True(File.Exists(Path.Combine(dir, "css", "style.css")) && !File.Exists(Path.Combine(dir, "README.md")));
            Assert.True(!File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!, "evil.txt")));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void ANodeProjectIsWrittenWithoutAnyDownload()
    {
        string folder = Folder("My Tool");
        try
        {
            var made = NewProject.Create(new NewProjectRequest(folder, "nodejs", false, false, "none", null, null, null));
            var package = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "package.json")))!;
            Assert.Equal("my-tool", package["name"]!.GetValue<string>());
            Assert.Equal("node index.js", package["scripts"]!["start"]!.GetValue<string>());
            Assert.Equal("console.log('Hello from My Tool!');\n", File.ReadAllText(Path.Combine(folder, "index.js")));
            Assert.Equal("index.js,package.json", string.Join(",", made.Files));
            A.Null(made.Interpreter);
            Assert.True(!Directory.Exists(Path.Combine(folder, ".venv")), "no Python environment");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void NextJsAsksForAFolderNameNpmTakes()
    {
        string folder = Folder("My Site");
        try
        {
            bool refused = false;
            try { NewProject.Create(new NewProjectRequest(folder, "nextjs", false, false, "none", null, null, null)); }
            catch (ServiceException e) { refused = e.Message.Contains("my-site"); }
            Assert.True(refused);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } }
    }

    /// <summary>
    /// With PYMAGIC_TEST_JS_LIVE=1 (a download from the npm registry, with the user's yes): one project of each kind made by its generator, packages
    /// not installed, and the versions npm lists.
    /// </summary>
    [Fact]
    public void EachKindIsMadeByItsGenerator()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_JS_LIVE") != "1") return;
        var bootstrapVersions = JsProjects.Versions("bootstrap");
        Assert.True(bootstrapVersions.Count > 5 && bootstrapVersions.Contains("5.3.3"), string.Join(",", bootstrapVersions));
        var cases = new (string Kind, JsProjectOptions Options, string[] Files)[]
        {
            ("html5boilerplate", new(Version: JsProjects.Versions("html5boilerplate").FirstOrDefault()), new[] { "index.html", "css/style.css" }),
            ("bootstrap", new(Version: "5.3.3"), new[] { "index.html", "css/bootstrap.min.css", "js/bootstrap.bundle.min.js" }),
            ("vite", new(Template: "react-ts"), new[] { "package.json", "index.html", "src/main.tsx" }),
            ("express", new(View: "pug"), new[] { "package.json", "app.js", "bin/www", "views/index.pug" }),
            ("nextjs", new(), new[] { "package.json", "app/page.tsx", "tsconfig.json" }),
            ("reactnative", new(), new[] { "package.json", "App.tsx", "android/build.gradle" }),
        };
        foreach (var (kind, options, files) in cases)
        {
            string folder = Folder("pm-" + kind);
            try
            {
                var made = NewProject.Create(new NewProjectRequest(folder, kind, true, false, "none", null, null, null, InstallPackages: false, Js: options));
                foreach (var f in files)
                    Assert.True(File.Exists(Path.Combine(folder, f.Replace('/', Path.DirectorySeparatorChar))), $"{kind}: {f} (made: {string.Join(",", made.Files)}; {string.Join(" | ", made.Notes)})");
                Assert.True(File.Exists(Path.Combine(folder, ".gitignore")) && Directory.Exists(Path.Combine(folder, ".git")), kind + ": Git");
                Assert.True(!Directory.Exists(Path.Combine(folder, "node_modules")) || kind == "reactnative", kind + ": nothing installed");
                Console.WriteLine($"  {kind}: {string.Join(",", made.Files)} | {string.Join(" | ", made.Notes)}");
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    [Fact]
    public void BootstrapsStarterPageUsesTheUnpackedFiles()
    {
        string page = JsProjects.BootstrapPage("Shop <1>", true);
        Assert.True(page.Contains("css/bootstrap.min.css") && page.Contains("js/bootstrap.bundle.min.js") && page.Contains("<title>Shop &lt;1&gt;</title>"));
        Assert.True(JsProjects.BootstrapPage("x", false).Contains("js/bootstrap.min.js"), "Bootstrap 3 has no bundle");
    }
}
