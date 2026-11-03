using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>
/// Arguments of the wrong type that PyCharm does not report: a line with `# type: ignore` (or a file with one at its top), and a dataclass where
/// a protocol asks for __dataclass_fields__ (typeshed's DataclassInstance, taken by dataclasses.replace, fields, asdict).
/// </summary>
public class StructuralArgumentTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).Where(x => x.StartsWith("Expected type", StringComparison.Ordinal)).ToList();
    }

    const string Sized = "from typing import Protocol\n\n\nclass HasLen(Protocol):\n    def __len__(self) -> int: ...\n\n\ndef size(x: HasLen) -> int: ...\n\n\nclass Bare:\n    pass\n\n\n";

    [Fact]
    public void ALineWithTypeIgnoreReportsNoArgumentType()
    {
        // found in the library scan: jinja2's `len(self._iterable)  # type: ignore`, requests' `jar.extract_cookies(res, req)  # type: ignore[arg-type]`
        var found = Found(Sized + "size(Bare())  # type: ignore\nsize(Bare())  # type: ignore[arg-type]\nsize(Bare())  #type:ignore\nsize(Bare())\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("got 'Bare'", found[0]);
    }

    [Fact]
    public void TypeIgnoreAtTheTopOfTheFileCoversIt()
    {
        Assert.Equal(0, Found("#!/usr/bin/env python\n# type: ignore\n" + Sized + "size(Bare())\n").Count);
        // below a docstring or code it is a comment of its own line only
        Assert.Equal(1, Found("\"\"\"Doc.\"\"\"\n# type: ignore\n" + Sized + "size(Bare())\n").Count);
    }

    [Fact]
    public void OtherCommentsDoNotSilenceIt()
    {
        Assert.Equal(2, Found(Sized + "size(Bare())  # type: int\nsize(Bare())  # ignore\n").Count);
    }

    [Fact]
    public void TypeIgnoreAlsoSilencesReturnAndAssignmentTypes()
    {
        var svc = new PyMagic.Server.LanguageService();
        const string uri = "file:///c:/proj/main.py";
        svc.Open(uri, "def f() -> int:\n    return 'a'  # type: ignore\n\n\ndef g() -> int:\n    return 'a'\n\n\nx: int = 'a'  # type: ignore[assignment]\ny: int = 'a'\n", 1);
        var codes = svc.Diagnostics(uri).Select(d => d.Code).Where(c => c is "return-type" or "assignment-type").ToList();
        Assert.Equal("return-type assignment-type", string.Join(" ", codes));
    }

    const string Fields = "from typing import Any, ClassVar, Protocol\nimport dataclasses\nfrom dataclasses import dataclass\n\n\n"
        + "class DataclassInstance(Protocol):\n    __dataclass_fields__: ClassVar[dict[str, Any]]\n\n\ndef replace(obj: DataclassInstance) -> None: ...\n\n\n";

    [Fact]
    public void ADataclassHasItsFields()
    {
        // found in the library scan: rich's Column (`replace(self, _cells=[])`) and pydantic's UrlConstraints (`fields(self)`)
        var found = Found(Fields + "@dataclass\nclass A:\n    x: int = 0\n\n\n@dataclasses.dataclass(frozen=True)\nclass B:\n    y: int = 0\n\n\n"
            + "class C(A):\n    pass\n\n\nreplace(A())\nreplace(B())\nreplace(C())\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void APlainClassStillHasNone()
    {
        var found = Found(Fields + "class Plain:\n    x: int = 0\n\n\nreplace(Plain())\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("got 'Plain'", found[0]);
    }

    [Fact]
    public void WithTypeshedDataclassesTakeADataclass()
    {
        if (Workspace.DefaultTypeshedPath == null) return;
        var found = Found("from dataclasses import dataclass, fields, replace, asdict\n\n\n@dataclass\nclass A:\n    x: int = 0\n\n\nclass Plain:\n    x: int = 0\n\n\n"
            + "replace(A(), x=1)\nfields(A())\nfields(A)\nasdict(A())\nasdict(Plain())\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("got 'Plain'", found[0]);
    }
}
