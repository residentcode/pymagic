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

    // ------------------------------------------------------------ item 217: the rest of ruff's and isort's import settings (checked against ruff 0.16.4)

    static ImportOptions Ruff(string settings) => FormatConfig.FromToml("[tool.ruff.lint.isort]\n" + settings)!.Imports!;

    [Fact]
    public void LinesAfterImports()
    {
        string code = "import os\nimport sys\nx = 1\n";
        Assert.Equal("import os\nimport sys\n\n\nx = 1", Sort(code, Ruff("lines-after-imports = 2\n")));
        Assert.Equal("import os\nimport sys\n\ndef f():\n    pass", Sort("import os\nimport sys\ndef f():\n    pass\n", Ruff("lines-after-imports = 1\n")));
        // not set: the blank lines are the formatter's business, as before
        Assert.Equal("import os\nimport sys\nx = 1", Sort(code, ImportOptions.Default));
        Assert.Equal(1, FormatConfig.FromToml("[tool.isort]\nlines_after_imports = 1\n")!.Imports!.LinesAfterImports);
    }

    [Fact]
    public void SectionOrder()
    {
        string code = "import requests\nimport mine\nimport os\nfrom . import here\n";
        Assert.Equal("import mine\n\nimport os\n\nimport requests\n\nfrom . import here",
            Sort(code, Ruff("section-order = [\"future\", \"first-party\", \"standard-library\", \"third-party\", \"local-folder\"]\n")));
        // isort's names, and sections left out keep their usual place after those named
        Assert.Equal("import requests\n\nimport os\n\nimport mine\n\nfrom . import here", Sort(code, FormatConfig.FromToml("[tool.isort]\nsections = [\"THIRDPARTY\"]\n")!.Imports!));
    }

    [Fact]
    public void ForceWrapAliases()
    {
        string code = "from os import path as p, sep\n";
        Assert.Equal("from os import (\n    path as p,\n    sep,\n)", Sort(code, Ruff("force-wrap-aliases = true\ncombine-as-imports = true\n")));
        // one name, or no alias, fits on its line
        Assert.Equal("from os import path as p", Sort("from os import path as p\n", Ruff("force-wrap-aliases = true\ncombine-as-imports = true\n")));
    }

    [Fact]
    public void FromFirstAndRelativeOrder()
    {
        Assert.Equal("from json import loads\nimport os\nimport sys", Sort("import sys\nfrom json import loads\nimport os\n", Ruff("from-first = true\n")));
        string relative = "from .. import parent\nfrom . import sibling\n";
        Assert.Equal("from .. import parent\nfrom . import sibling", Sort(relative, ImportOptions.Default));
        Assert.Equal("from . import sibling\nfrom .. import parent", Sort(relative, Ruff("relative-imports-order = \"closest-to-furthest\"\n")));
    }

    [Fact]
    public void SplitOnTrailingComma()
    {
        string code = "from typing import (\n    Any,\n    List,\n)\n";
        Assert.Equal("from typing import (\n    Any,\n    List,\n)", Sort(code, ImportOptions.Default));
        Assert.Equal("from typing import Any, List", Sort(code, Ruff("split-on-trailing-comma = false\n")));
        Assert.Equal("from typing import Any, List", Sort("from typing import (Any, List)\n", ImportOptions.Default));
    }
}
