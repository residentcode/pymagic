using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PostgreSQL and MySQL servers in the Database panel (item 232): the project's list of them with the password kept apart, and
/// pm_dbserver.py's answers. The live checks run only when PYMAGIC_TEST_POSTGRES / PYMAGIC_TEST_MYSQL name a server
/// ("host:port:user:password:database", an empty password allowed) and PYMAGIC_PYTHON a Python with psycopg / PyMySQL.
/// </summary>
public class DbServerTests
{
    static string Root() { string r = Path.Combine(Path.GetTempPath(), "pm_dbs_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(r); return r; }

    static JsonObject Form(string kind = "postgres", string host = "db.example.org", string port = "5433", string user = "me", string password = "s3cret", string database = "shop", string name = "") => new()
    {
        ["kind"] = kind, ["host"] = host, ["port"] = port, ["user"] = user, ["password"] = password, ["database"] = database, ["name"] = name,
    };

    [Fact]
    public void AServerIsKeptWithItsPasswordInAnotherFile()
    {
        string root = Root();
        try
        {
            var server = DbServers.Parse(Form());
            Assert.Equal("postgres", server.Kind);
            Assert.Equal(5433, server.Port);
            Assert.Equal("me@db.example.org/shop", server.Name);
            DbServers.Save(root, server, "s3cret");
            string settings = File.ReadAllText(Path.Combine(root, ".pymagic", "db-servers.json"));
            Assert.Contains("db.example.org", settings);
            Assert.True(!settings.Contains("s3cret"), "the settings file has no password");
            Assert.Contains("s3cret", File.ReadAllText(Path.Combine(root, ".pymagic", "db-servers.private.json")));
            var connection = DbServers.Connection(root, DbServers.PathOf(server.Id))!;
            Assert.Equal("s3cret", connection["password"]!.GetValue<string>());
            Assert.Equal(5433, connection["port"]!.GetValue<int>());
            // it is listed among the data sources, without its password
            var source = Databases.Find(root).Single(s => s.Server != null);
            Assert.Equal("server:" + server.Id, source.Path);
            Assert.Equal("db.example.org:5433/shop", source.Detail);
            DbServers.Remove(root, server.Id);
            Assert.Equal(0, Databases.Find(root).Count(s => s.Server != null));
            Assert.True(!File.ReadAllText(Path.Combine(root, ".pymagic", "db-servers.private.json")).Contains("s3cret"), "the password goes with the server");
            Assert.True(DbServers.Connection(root, "server:nothing") == null);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheFormIsCheckedAndDefaultsAreFilled()
    {
        var mysql = DbServers.Parse(Form(kind: "mysql", host: "", port: "", user: "", database: ""));
        Assert.Equal("localhost", mysql.Host);
        Assert.Equal(3306, mysql.Port);
        Assert.Equal("root", mysql.User);
        var pg = DbServers.Parse(Form(host: "", port: "", user: "", database: ""));
        Assert.Equal(5432, pg.Port);
        Assert.Equal("postgres", pg.User);
        Assert.Equal("postgres", pg.Database);
        Assert.Throws<ServiceException>(() => DbServers.Parse(Form(kind: "oracle")));
        Assert.Throws<ServiceException>(() => DbServers.Parse(Form(port: "99999")));
        Assert.Throws<ServiceException>(() => DbServers.Parse(Form(host: "a\nb")));
        Assert.Equal("my db", DbServers.Parse(Form(name: " my db ")).Name);
        // the id is a name that is safe on disk and in a URL, whatever was typed
        Assert.True(DbServers.Parse(Form(database: "../x y")).Id.All(c => char.IsLetterOrDigit(c) || c == '-'));
    }

    static (Dictionary<string, string>? Server, string Python) Live(string variable)
    {
        string? spec = Environment.GetEnvironmentVariable(variable);
        string? python = Environment.GetEnvironmentVariable("PYMAGIC_PYTHON");
        if (spec == null || python == null) return (null, "");
        var p = spec.Split(':');
        return (new() { ["host"] = p[0], ["port"] = p[1], ["user"] = p[2], ["password"] = p[3], ["database"] = p[4] }, python);
    }

    static JsonNode Ask(string kind, Dictionary<string, string> s, string python, string op, JsonObject request)
    {
        request["op"] = op;
        request["server"] = new JsonObject
        {
            ["kind"] = kind, ["host"] = s["host"], ["port"] = int.Parse(s["port"]), ["user"] = s["user"], ["password"] = s["password"], ["database"] = s["database"],
        };
        return Databases.Ask(python, request, 60000, "pm_dbserver.py");
    }

    static void Exercise(string kind, string variable)
    {
        var (server, python) = Live(variable);
        if (server == null) return;
        JsonNode A(string op, JsonObject r) => Ask(kind, server, python, op, r);
        string serial = kind == "postgres" ? "serial" : "int AUTO_INCREMENT";
        foreach (var sql in new[]
        {
            "DROP VIEW IF EXISTS pm_pricey", "DROP TABLE IF EXISTS pm_item", "DROP TABLE IF EXISTS pm_maker",
            $"CREATE TABLE pm_maker (id {serial} PRIMARY KEY, name text NOT NULL)",
            kind == "postgres" ? $"CREATE TABLE pm_item (id {serial} PRIMARY KEY, name varchar(40) NOT NULL, price numeric(8,2), maker_id int REFERENCES pm_maker(id))"
                               : $"CREATE TABLE pm_item (id {serial} PRIMARY KEY, name varchar(40) NOT NULL, price decimal(8,2), maker_id int, FOREIGN KEY (maker_id) REFERENCES pm_maker(id))",
            "CREATE INDEX pm_item_name ON pm_item(name)", "CREATE VIEW pm_pricey AS SELECT * FROM pm_item WHERE price > 5",
            "INSERT INTO pm_maker (name) VALUES ('Acme')",
            "INSERT INTO pm_item (name, price, maker_id) VALUES ('pen', 2, 1), ('book', 12.5, 1), ('cup%', 4, NULL)",
        })
            Assert.True(A("run", new JsonObject { ["sql"] = sql })["error"] == null, sql);
        try
        {
            Assert.Contains("", A("test", new JsonObject())["version"]!.GetValue<string>());
            var schema = A("schema", new JsonObject())["objects"]!.AsArray();
            var item = schema.Single(o => o!["name"]!.GetValue<string>() == "pm_item")!;
            Assert.Equal("table", item["kind"]!.GetValue<string>());
            Assert.Equal(1, item["columns"]!.AsArray().Single(c => c!["pk"]!.GetValue<int>() > 0)!["pk"]!.GetValue<int>());
            Assert.True(item["indexes"]!.AsArray().Any(i => i!["name"]!.GetValue<string>() == "pm_item_name"), "the index");
            Assert.Equal("pm_maker", item["foreignKeys"]![0]!["table"]!.GetValue<string>());
            Assert.Equal("view", schema.Single(o => o!["name"]!.GetValue<string>() == "pm_pricey")!["kind"]!.GetValue<string>());

            var rows = A("rows", new JsonObject { ["table"] = "pm_item", ["limit"] = 10 });
            Assert.Equal(3, rows["total"]!.GetValue<int>());
            Assert.True(rows["editable"]!.GetValue<bool>());
            Assert.Equal("2.00", rows["rows"]![0]!["values"]![2]!.GetValue<string>());      // a decimal keeps its digits
            Assert.Equal(1, A("rows", new JsonObject { ["table"] = "pm_item", ["where"] = "name LIKE 'cup%'" })["total"]!.GetValue<int>());   // a % of the user's own
            Assert.Equal("book", A("rows", new JsonObject { ["table"] = "pm_item", ["order"] = "price", ["desc"] = true })["rows"]![0]!["values"]![1]!.GetValue<string>());
            Assert.True(!A("rows", new JsonObject { ["table"] = "pm_pricey" })["editable"]!.GetValue<bool>(), "a view is read only");
            Assert.True(A("rows", new JsonObject { ["table"] = "no_such" })["error"] != null);

            var changed = A("apply", new JsonObject
            {
                ["table"] = "pm_item",
                ["changes"] = new JsonArray
                {
                    new JsonObject { ["kind"] = "update", ["key"] = new JsonObject { ["id"] = 2 }, ["values"] = new JsonObject { ["price"] = "20.5", ["name"] = "novel" } },
                    new JsonObject { ["kind"] = "insert", ["values"] = new JsonObject { ["name"] = "lamp", ["price"] = "9" } },
                    new JsonObject { ["kind"] = "delete", ["key"] = new JsonObject { ["id"] = 3 } },
                },
            });
            Assert.Equal(3, changed["changed"]!.GetValue<int>());
            var after = A("rows", new JsonObject { ["table"] = "pm_item", ["limit"] = 10 })["rows"]!.AsArray();
            Assert.Equal("pen,novel,lamp", string.Join(",", after.Select(r => r!["values"]![1]!.GetValue<string>())));

            // all or none: the second change breaks NOT NULL, so the first is undone
            var broken = A("apply", new JsonObject
            {
                ["table"] = "pm_item",
                ["changes"] = new JsonArray
                {
                    new JsonObject { ["kind"] = "update", ["key"] = new JsonObject { ["id"] = 1 }, ["values"] = new JsonObject { ["name"] = "changed" } },
                    new JsonObject { ["kind"] = "insert", ["values"] = new JsonObject { ["price"] = "1" } },
                },
            });
            Assert.True(broken["error"] != null);
            Assert.Equal("pen", A("rows", new JsonObject { ["table"] = "pm_item", ["limit"] = 1 })["rows"]![0]!["values"]![1]!.GetValue<string>());

            var select = A("run", new JsonObject { ["sql"] = "SELECT name, price FROM pm_item ORDER BY id" });
            Assert.Equal("name,price", string.Join(",", select["columns"]!.AsArray().Select(c => c!.GetValue<string>())));
            Assert.True(A("run", new JsonObject { ["sql"] = "SELEC 1" })["error"] != null);
            Assert.Equal(1, A("run", new JsonObject { ["sql"] = "DELETE FROM pm_item WHERE name = 'lamp'" })["changed"]!.GetValue<int>());

            string csv = Path.Combine(Path.GetTempPath(), "pm_dbs_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllText(csv, "name,price\ncsv1,1.5\ncsv2,\n");
            try { Assert.Equal(2, A("import", new JsonObject { ["table"] = "pm_item", ["csv"] = csv })["imported"]!.GetValue<int>()); }
            finally { File.Delete(csv); }
        }
        finally
        {
            foreach (var sql in new[] { "DROP VIEW IF EXISTS pm_pricey", "DROP TABLE IF EXISTS pm_item", "DROP TABLE IF EXISTS pm_maker" })
                A("run", new JsonObject { ["sql"] = sql });
        }
    }

    [Fact] public void PostgreSqlAnswersEveryRequest() => Exercise("postgres", "PYMAGIC_TEST_POSTGRES");

    [Fact] public void MySqlAnswersEveryRequest() => Exercise("mysql", "PYMAGIC_TEST_MYSQL");

    [Fact]
    public void WithoutTheDriverTheAnswerSaysWhichPackageToInstall()
    {
        string python = Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";
        try
        {
            var probe = Process.Start(new ProcessStartInfo(python, "-c \"import psycopg\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (probe == null || !probe.WaitForExit(20000)) return;
            if (probe.ExitCode == 0) return;     // the driver is there: nothing to show
        }
        catch { return; }
        var answer = Databases.Ask(python, new JsonObject { ["op"] = "test", ["server"] = new JsonObject { ["kind"] = "postgres", ["host"] = "127.0.0.1", ["port"] = 1 } }, 30000, "pm_dbserver.py");
        Assert.Contains("psycopg", answer["error"]!.GetValue<string>());
        Assert.True(answer["missingDriver"]!.GetValue<bool>());
    }
}
