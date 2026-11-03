using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>`TypeForm[T]` (PEP 747): the argument is a type expression, and T is the type it spells, as pydantic's `TypeAdapter` takes it.</summary>
public class TypeFormTests
{
    static bool HaveTypeshed => Workspace.DefaultTypeshedPath != null && Directory.Exists(Path.Combine(Workspace.DefaultTypeshedPath, "stdlib"));

    static (List<string> Types, List<string> Found) Check(string src, params string[] libraries)
    {
        var ws = new Workspace(new WorkspaceOptions { TypeshedPath = Workspace.DefaultTypeshedPath, LibraryRoots = libraries });
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var types = m.Ast.Body.OfType<ExprStmt>().Select(s => ws.Evaluator.TypeOf(m, s.Value).ToString()).ToList();
        return (types, ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList());
    }

    [Fact]
    public void TheVariableIsTheTypeTheArgumentSpells()
    {
        if (!HaveTypeshed) return;   // TypeForm is in typeshed's typing_extensions
        var (types, found) = Check("from typing import Generic, Literal, Optional, TypeVar\nfrom typing_extensions import TypeForm\n\nT = TypeVar('T')\n\n\n"
            + "def cast_to(form: TypeForm[T], value: object) -> T: ...\n\n\n"
            + "class Adapter(Generic[T]):\n    def __init__(self, form: TypeForm[T]) -> None: ...\n    def check(self, value: object) -> T: ...\n\n\n"
            + "def plain(form: TypeForm[int]) -> None: ...\n\n\n"
            + "class Item:\n    pass\n\n\n"
            + "alias = dict[str, int]\n"
            + "cast_to(list[int], 1)\ncast_to(int | None, 1)\ncast_to(Optional[str], 1)\ncast_to('Item', 1)\ncast_to(Literal['a', 'b'], 1)\n"
            + "Adapter(Item).check(1)\nAdapter(alias).check(1)\nAdapter(tuple[int, str]).check(1)\nplain(list[str])\n");
        Assert.Equal("list[int]|int | None|str | None|Item|Literal['a', 'b']|Item|dict[str, int]|tuple[int, str]|None", string.Join("|", types));
        Assert.Equal(0, found.Count);
    }

    [Fact]
    public void PydanticTypeAdapterValidatesToTheTypeItWasGiven()
    {
        // the library scan's Python (the runner points TEMP at a folder of its own, so the user's temp folder is found by name)
        string site = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "pm-scan-venv", "Lib", "site-packages");
        if (!HaveTypeshed || !File.Exists(Path.Combine(site, "pydantic", "type_adapter.py"))) return;   // needs pydantic 2.14 installed there
        var (types, found) = Check("from typing import Optional\nfrom pydantic import BaseModel, TypeAdapter\n\n\n"
            + "class M(BaseModel):\n    x: int\n\n\n"
            + "TypeAdapter(list[int]).validate_python([1])\nTypeAdapter(M).validate_python({})\nTypeAdapter(int | None).validate_python(1)\n"
            + "TypeAdapter(Optional[str]).validate_json('1')\nTypeAdapter(dict[str, list[M]]).validate_strings({})\nTypeAdapter(list[int])\n", site);
        Assert.Equal("list[int]|M|int | None|str | None|dict[str, list[M]]|TypeAdapter[list[int]]", string.Join("|", types));
        Assert.True(!found.Any(f => f.Contains("TypeAdapter") || f.Contains("TypeForm")), string.Join("; ", found));
    }
}
