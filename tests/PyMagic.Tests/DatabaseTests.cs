using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The Database panel's engine side (item 225): SQLite files found in a project, and pm_db.py's answers, with a real Python.</summary>
public class DatabaseTests
{
    static readonly bool HavePython = Probe();

    static bool Probe()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    sealed class Project : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_db_" + Guid.NewGuid().ToString("N"));
        public readonly string Db;
        public Project()
        {
            Directory.CreateDirectory(Path.Combine(Root, "data"));
            Db = Path.Combine(Root, "data", "shop.sqlite3");
            Databases.AddSource(Root, Db, "python");   // made empty
            foreach (var sql in new[]
            {
                "CREATE TABLE item (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price REAL DEFAULT 0)",
                "CREATE TABLE tag (item_id INTEGER REFERENCES item(id), label TEXT, PRIMARY KEY (item_id, label)) WITHOUT ROWID",
                "CREATE INDEX item_name ON item(name)",
                "CREATE VIEW cheap AS SELECT name FROM item WHERE price < 3",
                "INSERT INTO item (name, price) VALUES ('pen', 2), ('book', 12.5), ('cup', 4)",
                "INSERT INTO tag VALUES (1, 'office')",
            })
                Assert.True(Ask("run", new JsonObject { ["sql"] = sql })["error"] == null, sql);
        }

        public JsonNode Ask(string op, JsonObject request)
        {
            request["op"] = op;
            request["path"] = Db;
            return Databases.Ask("python", request);
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Fact]
    public void SqliteFilesOfTheProjectAreFoundByTheirFirstBytes()
    {
        if (!HavePython) return;
        using var p = new Project();
        File.WriteAllText(Path.Combine(p.Root, "notes.db"), "not a database");
        Directory.CreateDirectory(Path.Combine(p.Root, ".venv"));
        File.Copy(p.Db, Path.Combine(p.Root, ".venv", "cache.sqlite"));
        File.Copy(p.Db, Path.Combine(p.Root, "db.sqlite3"));
        var found = Databases.Find(p.Root);
        Assert.Equal("data/shop.sqlite3,db.sqlite3", string.Join(",", found.Select(s => s.Path)));
        Assert.True(found[0].Added, "the file made by hand is marked as added");
        Assert.Throws<ServiceException>(() => Databases.AddSource(p.Root, Path.Combine(p.Root, "notes.db"), "python"));
        Databases.RemoveSource(p.Root, "data/shop.sqlite3");
        Assert.Equal("data/shop.sqlite3,db.sqlite3", string.Join(",", Databases.Find(p.Root).Select(s => s.Path)));   // still found by the scan
        Assert.True(!Databases.Find(p.Root)[0].Added, "no longer added by hand");
    }

    [Fact]
    public void TheSchemaListsTablesViewsColumnsKeysAndIndexes()
    {
        if (!HavePython) return;
        using var p = new Project();
        var objects = p.Ask("schema", new JsonObject())["objects"]!.AsArray();
        Assert.Equal("item,tag,cheap", string.Join(",", objects.Select(o => o!["name"]!.GetValue<string>())));
        var item = objects[0]!;
        Assert.Equal("id:INTEGER:1,name:TEXT:0,price:REAL:0", string.Join(",", item["columns"]!.AsArray().Select(c => $"{c!["name"]}:{c["type"]}:{c["pk"]}")));
        Assert.True(item["columns"]![1]!["notNull"]!.GetValue<bool>());
        Assert.Equal("item_name", item["indexes"]![0]!["name"]!.GetValue<string>());
        var tag = objects[1]!;
        Assert.True(!tag["rowid"]!.GetValue<bool>(), "WITHOUT ROWID");
        Assert.Equal("item_id -> item.id", $"{tag["foreignKeys"]![0]!["column"]} -> {tag["foreignKeys"]![0]!["table"]}.{tag["foreignKeys"]![0]!["to"]}");
        Assert.Equal("view", objects[2]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void RowsArePagedSortedAndFiltered()
    {
        if (!HavePython) return;
        using var p = new Project();
        var page = p.Ask("rows", new JsonObject { ["table"] = "item", ["order"] = "price", ["desc"] = true, ["limit"] = 2 });
        Assert.Equal(3, page["total"]!.GetValue<int>());
        Assert.Equal("book,cup", string.Join(",", page["rows"]!.AsArray().Select(r => r!["values"]![1]!.GetValue<string>())));
        Assert.Equal(2, page["rows"]![0]!["key"]!["rowid"]!.GetValue<int>());
        Assert.True(page["editable"]!.GetValue<bool>());
        var filtered = p.Ask("rows", new JsonObject { ["table"] = "item", ["where"] = "price < 5" });
        Assert.Equal(2, filtered["total"]!.GetValue<int>());
        var tag = p.Ask("rows", new JsonObject { ["table"] = "tag" });
        Assert.Equal("{\"item_id\":1,\"label\":\"office\"}", tag["rows"]![0]!["key"]!.ToJsonString());   // its primary key
        Assert.True(!p.Ask("rows", new JsonObject { ["table"] = "cheap" })["editable"]!.GetValue<bool>(), "a view is read only");
        Assert.Contains("no such column", p.Ask("rows", new JsonObject { ["table"] = "item", ["where"] = "nope = 1" })["error"]!.GetValue<string>());
    }

    [Fact]
    public void TheDataEditorsChangesAreAppliedTogetherOrNotAtAll()
    {
        if (!HavePython) return;
        using var p = new Project();
        var changes = new JsonArray(
            new JsonObject { ["kind"] = "update", ["key"] = new JsonObject { ["rowid"] = 1 }, ["values"] = new JsonObject { ["price"] = "2.5" } },
            new JsonObject { ["kind"] = "insert", ["values"] = new JsonObject { ["name"] = "lamp", ["price"] = "30" } },
            new JsonObject { ["kind"] = "delete", ["key"] = new JsonObject { ["rowid"] = 3 } });
        Assert.Equal(3, p.Ask("apply", new JsonObject { ["table"] = "item", ["changes"] = changes })["changed"]!.GetValue<int>());
        var rows = p.Ask("run", new JsonObject { ["sql"] = "SELECT name, price, typeof(price) FROM item ORDER BY id" });
        Assert.Equal("[[\"pen\",2.5,\"real\"],[\"book\",12.5,\"real\"],[\"lamp\",{\"real\":\"30.0\"},\"real\"]]", rows["rows"]!.ToJsonString());   // text typed in a cell takes the column's type; 30.0 stays a REAL on the page

        // a refused change (NOT NULL) leaves everything as it was
        var bad = new JsonArray(
            new JsonObject { ["kind"] = "delete", ["key"] = new JsonObject { ["rowid"] = 1 } },
            new JsonObject { ["kind"] = "insert", ["values"] = new JsonObject { ["name"] = null } });
        Assert.Contains("nothing was changed", p.Ask("apply", new JsonObject { ["table"] = "item", ["changes"] = bad })["error"]!.GetValue<string>());
        Assert.Equal(3, p.Ask("rows", new JsonObject { ["table"] = "item" })["total"]!.GetValue<int>());
    }

    [Fact]
    public void TheConsoleRunsAStatementAndSaysWhatItDid()
    {
        if (!HavePython) return;
        using var p = new Project();
        var select = p.Ask("run", new JsonObject { ["sql"] = "SELECT name, x'00ff' AS raw FROM item ORDER BY name", ["limit"] = 2 });
        Assert.Equal("name,raw", string.Join(",", select["columns"]!.AsArray().Select(c => c!.GetValue<string>())));
        Assert.True(select["more"]!.GetValue<bool>(), "three rows, two shown");
        Assert.Equal("{\"blob\":\"00ff\",\"size\":2}", select["rows"]![0]![1]!.ToJsonString());
        Assert.Equal(2, p.Ask("run", new JsonObject { ["sql"] = "UPDATE item SET price = price + 1 WHERE price < 5" })["changed"]!.GetValue<int>());
        Assert.Contains("syntax error", p.Ask("run", new JsonObject { ["sql"] = "SELEC 1" })["error"]!.GetValue<string>());
        Assert.Contains("one statement", p.Ask("run", new JsonObject { ["sql"] = "SELECT 1; SELECT 2" })["error"]!.GetValue<string>());
    }
}
