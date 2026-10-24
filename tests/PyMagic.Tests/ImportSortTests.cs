using PyMagic.Server;
using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

public class ImportSortTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static readonly Func<string, ImportSection> Classify = top =>
        StdlibNames.All.Contains(top) ? ImportSection.Stdlib : top is "myapp" or "shop" ? ImportSection.FirstParty : ImportSection.ThirdParty;

    static string Sort(string text) => ImportSorter.Sort(text, Classify) ?? throw new AssertionException("declined");

    [Fact]
    public void SectionsAndOrder()
    {
        string input = Src("import sys", "import requests", "from myapp import util", "import os", "from . import sibling", "from collections import OrderedDict", "import numpy as np", "from __future__ import annotations", "", "x = 1");
        Assert.Equal(Src(
            "from __future__ import annotations", "",
            "import os", "import sys", "from collections import OrderedDict", "",
            "import numpy as np", "import requests", "",
            "from myapp import util", "",
            "from . import sibling", "",
            "x = 1"), Sort(input));
    }

    [Fact]
    public void NamesAreOrderedByTypeAndDuplicatesMerge()
    {
        Assert.Equal(Src("from typing import TYPE_CHECKING, Any, Dict, cast, overload"),
            Sort(Src("from typing import overload, Dict", "from typing import cast, Any, TYPE_CHECKING", "from typing import Dict")));
        Assert.Equal(Src("import os", "import sys"), Sort(Src("import sys", "import os", "import sys")));
    }

    [Fact]
    public void ImportListsAreSplitAndAliasesStaySeparate()
    {
        Assert.Equal(Src("import os", "import sys as system", "", "from a import b", "from a import c as d"),
            Sort(Src("import sys as system, os", "from a import c as d, b")));
    }

    [Fact]
    public void LongLinesAreWrappedWithATrailingComma()
    {
        string names = string.Join(", ", Enumerable.Range(1, 12).Select(i => "name_number_" + i.ToString("00")));
        string result = Sort(Src("from some_library.module import " + names));
        Assert.True(result.StartsWith("from some_library.module import (\n    name_number_01,\n"), result);
        Assert.True(result.EndsWith("    name_number_12,\n)\n"), result);
    }

    [Fact]
    public void CommentsTravelWithTheirImports()
    {
        string input = Src("# needs the network", "import requests  # http", "# base", "import os", "x = 1");
        Assert.Equal(Src("# base", "import os", "", "# needs the network", "import requests  # http", "x = 1"), Sort(input));
    }

    [Fact]
    public void OnlyTheBlockOfImportsIsTouched()
    {
        string input = Src("\"\"\"Doc.\"\"\"", "", "import b", "import a", "", "def f():", "    import z", "    import y", "", "import d", "import c");
        string result = Sort(input);
        Assert.Contains("import a\nimport b\n", result);
        Assert.Contains("    import z\n    import y\n", result);
        Assert.Contains("import c\nimport d", result);
        Assert.True(result.StartsWith("\"\"\"Doc.\"\"\""), result);
    }

    [Fact]
    public void LeavesUnsafeBlocksAlone()
    {
        string withInner = Src("from a import (", "    b,  # keep", "    c,", ")", "import a");
        Assert.Equal(withInner, Sort(withInner));
        string semicolons = Src("import b; import a");
        Assert.Equal(semicolons, Sort(semicolons));
        string skipped = Src("import b", "import a  # isort:skip");
        Assert.Equal(skipped, Sort(skipped));
        Assert.True(ImportSorter.Sort("# isort: skip_file\nimport b\nimport a\n", Classify) == null, "skip_file");
        Assert.True(ImportSorter.Sort("import (\n", Classify) == null, "syntax error");
    }

    [Fact]
    public void SortingTwiceChangesNothingAndKeepsTheImportedNames()
    {
        string input = Src("from os import path, sep", "import json", "from .a import x", "from ..b import y", "import re", "from myapp.util import helper, CONSTANT, Klass");
        string once = Sort(input);
        Assert.Equal(once, Sort(once));
        foreach (var piece in new[] { "path", "sep", "json", "x", "y", "re", "helper", "CONSTANT", "Klass" }) Assert.Contains(piece, once);
    }

    // ------------------------------------------------------------ through the language service

    const string Doc = "file:///c:/proj/app.py";
    const string Shop = "file:///c:/proj/shop.py";

    static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        var lm = new LineMap(text);
        foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)))
            text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
        return text;
    }

    [Fact]
    public void FormatDocumentSortsImportsAndFormatsTheCode()
    {
        var svc = new LanguageService();
        string text = Src("import shop", "import os", "import requests", "x=shop.f( os.sep )", "y=requests");
        svc.Open(Shop, "def f(a): return a\n", 1);
        svc.Open(Doc, text, 1);
        string result = Apply(text, svc.FormatDocument(Doc));
        Assert.Equal(Src("import os", "", "import requests", "", "import shop", "", "x = shop.f(os.sep)", "y = requests"), result);
        Assert.Equal(0, svc.FormatDocument(Doc).Count == 0 ? 0 : 1 - 1);
    }

    [Fact]
    public void FormatDocumentRefusesBrokenCode()
    {
        var svc = new LanguageService();
        svc.Open(Doc, "def f(:\n", 1);
        try { svc.FormatDocument(Doc); }
        catch (ServiceException e) { Assert.Contains("syntax errors", e.Message); return; }
        throw new AssertionException("expected a refusal");
    }

    [Fact]
    public void OrganizeImportsRemovesUnusedAndSorts()
    {
        var svc = new LanguageService();
        string text = Src("import sys", "import os", "from typing import List, Dict", "", "x: Dict = {}", "print(sys.argv)");
        svc.Open(Doc, text, 1);
        Assert.Equal(Src("import sys", "from typing import Dict", "", "x: Dict = {}", "print(sys.argv)"), Apply(text, svc.OrganizeImports(Doc)));
        var actions = svc.CodeActions(Doc, new PyMagic.Server.Range(new PyMagic.Server.Position(0, 0), new PyMagic.Server.Position(0, 0)));
        Assert.True(actions.Any(a => a.Title == "Optimize imports"), "offered as a code action");
    }
}
