using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>What the editor's TypeScript service is given of a project (TsProject): its code, its tsconfig with what it extends, the packages' types.</summary>
public class TsProjectTests
{
    static string Project(Dictionary<string, string> files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_ts_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        return root;
    }

    [Fact]
    public void TheProjectsCodeConfigAndPackagesTypes()
    {
        string root = Project(new()
        {
            ["package.json"] = """{ "name": "app", "dependencies": { "shouty": "1.0.0" }, "devDependencies": { "@scope/kit": "2.0.0" } }""",
            // comments and trailing commas, as tsconfig files have them; what it extends: a relative file and a package's
            ["tsconfig.json"] = """
                // the app's
                {
                  "extends": ["./tsconfig.base.json", "@tsconfig/strictest/tsconfig.json"],
                  "compilerOptions": { "target": "ES2022", "paths": { "@/*": ["./src/*"] }, },
                }
                """,
            ["tsconfig.base.json"] = """{ "compilerOptions": { "target": "ES5", "jsx": "react-jsx", "baseUrl": "./src" } }""",
            ["node_modules/@tsconfig/strictest/tsconfig.json"] = """{ "compilerOptions": { "strict": true, "noUncheckedIndexedAccess": true } }""",
            ["src/main.ts"] = "import { twice } from './util';\n",
            ["src/util.ts"] = "export const twice = (n: number) => n * 2;\n",
            ["src/view.jsx"] = "export const V = () => <p/>;\n",
            ["src/env.d.ts"] = "declare module '*.vue';\n",
            ["src/vendor.min.js"] = "var a=1;\n",
            ["dist/main.js"] = "built\n",
            [".venv/x.js"] = "not the project's\n",
            ["README.md"] = "# app\n",
            // packages: a dependency with types, what it depends on, @types, one not depended on (its package.json only)
            ["node_modules/shouty/package.json"] = """{ "name": "shouty", "types": "index.d.ts", "dependencies": { "helper": "1" } }""",
            ["node_modules/shouty/index.d.ts"] = "export declare function shout(s: string): string;\n",
            ["node_modules/shouty/lib/more.d.ts"] = "export {};\n",
            ["node_modules/shouty/index.js"] = "module.exports = {};\n",
            ["node_modules/shouty/node_modules/inner/index.d.ts"] = "export {};\n",
            ["node_modules/helper/package.json"] = """{ "name": "helper", "types": "h.d.ts" }""",
            ["node_modules/helper/h.d.ts"] = "export {};\n",
            ["node_modules/@scope/kit/package.json"] = """{ "name": "@scope/kit", "typings": "kit.d.ts" }""",
            ["node_modules/@scope/kit/kit.d.ts"] = "export {};\n",
            ["node_modules/@types/node/package.json"] = """{ "name": "@types/node" }""",
            ["node_modules/@types/node/index.d.ts"] = "declare var process: any;\n",
            ["node_modules/@types/node/ts5.6/index.d.ts"] = "declare var process: any;\n",
            ["node_modules/unused/package.json"] = """{ "name": "unused" }""",
            ["node_modules/unused/u.d.ts"] = "export {};\n",
        });
        try
        {
            Assert.True(TsProject.IsJsProject(root));
            var code = TsProject.Code(root);
            Assert.Equal("src/env.d.ts src/main.ts src/util.ts src/view.jsx", string.Join(" ", code.List.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal)));
            Assert.True(!code.Truncated);
            Assert.True(TsProject.Code(root, maxFiles: 2).Truncated);

            var config = TsProject.Config(root)!;
            Assert.Equal("tsconfig.json", config["path"]!.GetValue<string>());
            var o = config["compilerOptions"]!.AsObject();
            Assert.Equal("ES2022", o["target"]!.GetValue<string>());       // its own over what it extends
            Assert.Equal("react-jsx", o["jsx"]!.GetValue<string>());
            Assert.True(o["strict"]!.GetValue<bool>());                    // the package's
            Assert.Equal("src", o["baseUrl"]!.GetValue<string>());          // the base's, relative to the project
            Assert.True(config["error"] == null);

            var packages = TsProject.Packages(root).List.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.Equal(string.Join(" ", new[]
            {
                "node_modules/@scope/kit/kit.d.ts", "node_modules/@scope/kit/package.json", "node_modules/@types/node/index.d.ts", "node_modules/@types/node/package.json",
                "node_modules/helper/h.d.ts", "node_modules/helper/package.json", "node_modules/shouty/index.d.ts", "node_modules/shouty/lib/more.d.ts",
                "node_modules/shouty/package.json", "node_modules/unused/package.json",
            }), string.Join(" ", packages));
            Assert.True(TsProject.Packages(root, maxBytes: 300).Truncated);

            // a config that extends what is not there says so; no config, none; no package.json or config: not a JS project
            File.WriteAllText(Path.Combine(root, "tsconfig.json"), """{ "extends": "./missing.json", "compilerOptions": { "strict": false } }""");
            Assert.Equal("tsconfig.json extends ./missing.json, which is not there", TsProject.Config(root)!["error"]!.GetValue<string>());
            File.Delete(Path.Combine(root, "tsconfig.json"));
            Assert.True(TsProject.Config(root) == null);
            File.Delete(Path.Combine(root, "package.json"));
            Assert.True(!TsProject.IsJsProject(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
