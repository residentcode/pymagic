using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>isort and ruff import settings: known packages, force-sort-within-sections, combine-as-imports, force-single-line, order-by-type.</summary>
public class ImportOptionTests
{
    static readonly Func<string, ImportSection> Classify = top => top is "os" or "sys" or "json" ? ImportSection.Stdlib : top == "mine" ? ImportSection.FirstParty : ImportSection.ThirdParty;

    static string Sort(string text, ImportOptions options) => (ImportSorter.Sort(text, Classify, 88, options) ?? throw new AssertionException("declined")).TrimEnd((char)10);

    [Fact]
    public void KnownPackagesDecideTheSection()
    {
        string code = "import requests\nimport mine\nimport shared\nimport os\n";
        string plain = Sort(code, ImportOptions.Default);
        Assert.Equal("import os\n\nimport requests\nimport shared\n\nimport mine", plain);
        string configured = Sort(code, new ImportOptions(KnownFirstParty: new[] { "shared" }, KnownThirdParty: new[] { "mine" }));
        Assert.Equal("import os\n\nimport mine\nimport requests\n\nimport shared", configured);
    }

    [Fact]
    public void ForceSortWithinSectionsMixesImportAndFrom()
    {
        string code = "from os import path\nimport sys\nimport json\nfrom json import loads\n";
        Assert.Equal("import json\nimport sys\nfrom json import loads\nfrom os import path", Sort(code, ImportOptions.Default));
        Assert.Equal("import json\nfrom json import loads\nfrom os import path\nimport sys", Sort(code, new ImportOptions(ForceSortWithinSections: true)));
    }

    [Fact]
    public void CombineAsImportsKeepsOneStatement()
    {
        string code = "from os import path as p\nfrom os import sep\n";
        Assert.Equal("from os import sep\nfrom os import path as p", Sort(code, ImportOptions.Default));
        Assert.Equal("from os import path as p, sep", Sort(code, new ImportOptions(CombineAsImports: true)));
    }

    [Fact]
    public void ForceSingleLineWritesOneNamePerLine()
    {
        string code = "from os import path, sep, getcwd\n";
        Assert.Equal("from os import getcwd, path, sep", Sort(code, ImportOptions.Default));
        Assert.Equal("from os import getcwd\nfrom os import path\nfrom os import sep", Sort(code, new ImportOptions(ForceSingleLine: true)));
    }

    [Fact]
    public void OrderByTypeCanBeSwitchedOff()
    {
        string code = "from m import b, CONSTANT, a, Class\n";
        Assert.Equal("from m import CONSTANT, Class, a, b", Sort(code, ImportOptions.Default));
        Assert.Equal("from m import a, b, Class, CONSTANT", Sort(code, new ImportOptions(OrderByType: false)));
    }

    // ------------------------------------------------------------ the configuration

    [Fact]
    public void RuffAndIsortTablesAreRead()
    {
        var ruff = FormatConfig.FromToml("[tool.ruff]\nline-length = 90\n[tool.ruff.lint.isort]\nknown-first-party = [\"mine\", \"other\"]\nforce-sort-within-sections = true\ncombine-as-imports = true\n")!.Imports!;
        Assert.True(ruff.ForceSortWithinSections && ruff.CombineAsImports && !ruff.ForceSingleLine && ruff.OrderByType);
        Assert.Equal(2, ruff.KnownFirstParty!.Count);

        var isort = FormatConfig.FromToml("[tool.black]\nline-length = 100\n[tool.isort]\nprofile = \"black\"\nknown_third_party = [\"vendor\"]\nforce_single_line = true\norder_by_type = false\n")!.Imports!;
        Assert.True(isort.ForceSingleLine && !isort.OrderByType);
        Assert.Equal("vendor", isort.KnownThirdParty![0]);

        var toml = FormatConfig.FromToml("[lint.isort]\nforce-sort-within-sections = true\n", isRuffFile: true)!.Imports!;
        Assert.True(toml.ForceSortWithinSections);
        Assert.True(FormatConfig.FromToml("[tool.black]\nline-length = 100\n")!.Imports == null, "no isort table");
    }
}
