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
    public void TheSqliteDatabaseOfDjangosSettingsIsOfferedBeforeItExists()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_dj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "site", "mysite", "settings"));
        try
        {
            File.WriteAllText(Path.Combine(root, "site", "manage.py"), "import os\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'mysite.settings.base')\nfrom django.core.management import execute_from_command_line\n");
            File.WriteAllText(Path.Combine(root, "site", "mysite", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "site", "mysite", "settings", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "site", "mysite", "settings", "base.py"),
                "DATABASES = {\n    'default': {\n        'ENGINE': 'django.db.backends.sqlite3',\n        'NAME': BASE_DIR / 'data' / 'db.sqlite3',\n    },\n" +
                "    'logs': {'NAME': os.path.join(BASE_DIR, 'logs.sqlite3'), 'ENGINE': 'django.db.backends.sqlite3'},\n" +
                "    'pg': {'ENGINE': 'django.db.backends.postgresql', 'NAME': 'shop'},\n" +
                "    'mem': {'ENGINE': 'django.db.backends.sqlite3', 'NAME': ':memory:'},\n}\n");
            var found = Databases.Find(root).Where(s => s.Offer == null).ToList();
            Assert.Equal("site/data/db.sqlite3,site/logs.sqlite3", string.Join(",", found.Select(s => s.Path)));
            Assert.True(found.All(s => !s.Exists && s.Django == "mysite.settings.base" && s.Manage == "site/manage.py"), "not made yet, named by the settings");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ADatabaseUrlIsReadAsTheFormsFields()
    {
        var (kind, f) = Databases.DatabaseUrl("postgres://shop%40owner:p%2Fw%3Ax@db.example.org:6543/shop%20main?sslmode=verify-full&connect_timeout=5")!.Value;
        Assert.Equal("postgres", kind);
        Assert.Equal("shop@owner|p/w:x|db.example.org|6543|shop main|verify-full", string.Join("|", f["user"], f["password"], f["host"], f["port"], f["database"], f["sslMode"]));
        var (my, m) = Databases.DatabaseUrl("mysql://root@[::1]/legacy")!.Value;
        Assert.Equal("mysql", my);
        Assert.Equal("::1|3306|root||legacy", string.Join("|", m["host"], m["port"], m["user"], m["password"], m["database"]));
        Assert.Equal("postgres", Databases.DatabaseUrl("postgis://u@/geo")!.Value.Kind);
        Assert.Equal("localhost", Databases.DatabaseUrl("postgis://u@/geo")!.Value.Fields["host"]);
        Assert.True(Databases.DatabaseUrl("sqlite:///db.sqlite3") == null, "not a server");
        Assert.True(Databases.DatabaseUrl("not a url") == null);
        Assert.True(!Databases.DatabaseUrl("postgres://h/d?sslmode=nonsense")!.Value.Fields.ContainsKey("sslMode"), "an unknown mode is left out");
    }

    [Fact]
    public void DjangoEnvironsAndDjDatabaseUrlsDatabasesAreOffered()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_dj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "shop"));
        Environment.SetEnvironmentVariable("PM_TEST_REPORTS_URL", "mysql://reporter:r3p@reports.internal:3307/reports");
        try
        {
            File.WriteAllText(Path.Combine(root, "manage.py"), "import os\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'shop.settings')\n");
            File.WriteAllText(Path.Combine(root, "shop", "__init__.py"), "");
            // DATABASE_URL in the project's .env, as environ.Env.read_env(BASE_DIR / ".env") reads it
            File.WriteAllText(Path.Combine(root, ".env"), "# the database\nDEBUG=on\nexport DATABASE_URL=\"postgres://shopper:s3cret@db.internal:5433/shop\"\n");
            File.WriteAllText(Path.Combine(root, "shop", "settings.py"), """
                import environ, dj_database_url
                env = environ.Env()
                environ.Env.read_env()
                DATABASES = {
                    "default": env.db(),
                    "reports": env.db_url("PM_TEST_REPORTS_URL"),
                    "archive": env.db("PM_TEST_UNSET_URL", default="postgres://arch@archive.internal/old?sslmode=require"),
                    "cache": env.db("PM_TEST_CACHE_URL", default="sqlite:///cache.sqlite3"),
                }
                DATABASES["analytics"] = dj_database_url.config(env="PM_TEST_UNSET_URL", default="mysql://an@stats.internal/analytics")
                DATABASES["legacy"] = dj_database_url.parse("postgresql://old:pw@legacy.internal:5544/legacy")
                """);
            var offers = Databases.Find(root).Where(s => s.Offer != null).ToList();
            Assert.Equal("analytics:mysql:stats.internal:3306/analytics,archive:postgres:archive.internal:5432/old,default:postgres:db.internal:5433/shop,"
                + "legacy:postgres:legacy.internal:5544/legacy,reports:mysql:reports.internal:3307/reports",
                string.Join(",", offers.Select(s => $"{s.Offer!["alias"]}:{s.Server}:{s.Detail}").OrderBy(x => x, StringComparer.Ordinal)));
            var main = offers.Single(o => o.Offer!["alias"] == "default");
            Assert.Equal("shopper|s3cret", main.Offer!["user"] + "|" + main.Offer["password"]);
            Assert.Equal("require", offers.Single(o => o.Offer!["alias"] == "archive").Offer!["sslMode"]);
            Assert.Equal("reporter", offers.Single(o => o.Offer!["alias"] == "reports").Offer!["user"]);
            // the environment comes before the .env
            Environment.SetEnvironmentVariable("DATABASE_URL", "postgres://env@from-env.internal/envdb");
            Assert.Equal("from-env.internal:5432/envdb", Databases.Find(root).Single(s => s.Offer?["alias"] == "default").Detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PM_TEST_REPORTS_URL", null);
            Environment.SetEnvironmentVariable("DATABASE_URL", null);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ThePostgresAndMysqlDatabasesOfDjangosSettingsAreOfferedUntilConnected()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_dj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "shop"));
        Environment.SetEnvironmentVariable("PM_TEST_DB_HOST", "db.internal");
        try
        {
            File.WriteAllText(Path.Combine(root, "manage.py"), "import os\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'shop.settings')\nfrom django.core.management import execute_from_command_line\n");
            File.WriteAllText(Path.Combine(root, "shop", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "shop", "settings.py"), """
                import os
                DATABASES = {
                    "default": {
                        "ENGINE": "django.db.backends.postgresql",
                        "NAME": os.environ.get("PM_TEST_DB_NAME", "shop"),   # the default, the variable is not set
                        "USER": "shopper",
                        "PASSWORD": os.getenv("PM_TEST_DB_PASSWORD"),
                        "HOST": os.environ["PM_TEST_DB_HOST"],
                        "PORT": 5433,
                        "OPTIONS": {"sslmode": "require", "other": {"x": 1}},
                    },
                    'legacy': dict(ENGINE='django.db.backends.mysql', NAME='old_shop', USER='root', PASSWORD='pw', HOST='127.0.0.1'),
                    "maps": {"ENGINE": "django.contrib.gis.db.backends.postgis", "NAME": "geo"},
                    "cache": {"ENGINE": "django.db.backends.sqlite3", "NAME": "cache.sqlite3"},
                }
                """);
            var offers = Databases.Find(root).Where(s => s.Offer != null).ToList();
            Assert.Equal("default:postgres:db.internal:5433/shop,legacy:mysql:127.0.0.1:3306/old_shop,maps:postgres:localhost:5432/geo",
                string.Join(",", offers.Select(s => $"{s.Offer!["alias"]}:{s.Server}:{s.Detail}")));
            var pg = offers[0];
            Assert.Equal("shopper", pg.Offer!["user"]);
            Assert.Equal("", pg.Offer["password"]);
            Assert.Equal("shop.settings", pg.Django!);
            Assert.True(!pg.Exists, "not connected: not a source of the console");
            Assert.Equal("pw", offers[1].Offer!["password"]);
            // once connected it is a server of the project, no longer an offer
            DbServers.Save(root, DbServers.Parse(new JsonObject { ["kind"] = "mysql", ["host"] = "127.0.0.1", ["port"] = "3306", ["user"] = "root", ["database"] = "old_shop" }), "pw");
            Assert.Equal("default,maps", string.Join(",", Databases.Find(root).Where(s => s.Offer != null).Select(s => s.Offer!["alias"])));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PM_TEST_DB_HOST", null);
            Directory.Delete(root, true);
        }
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
    public void TheSchemaListsTheTriggersOfTheTablesWithTheirDefinition()
    {
        if (!HavePython) return;
        using var p = new Project();
        Assert.True(p.Ask("run", new JsonObject { ["sql"] = "CREATE TABLE log (what TEXT)" })["error"] == null);
        Assert.True(p.Ask("run", new JsonObject { ["sql"] = "CREATE TRIGGER item_added AFTER INSERT ON item BEGIN INSERT INTO log VALUES (new.name); END" })["error"] == null);
        Assert.True(p.Ask("run", new JsonObject { ["sql"] = "CREATE TRIGGER IF NOT EXISTS \"no update\" INSTEAD OF UPDATE ON cheap BEGIN SELECT 1; END" })["error"] == null);
        Assert.True(p.Ask("run", new JsonObject { ["sql"] = "CREATE TRIGGER gone DELETE ON item BEGIN SELECT 1; END" })["error"] == null);
        var schema = p.Ask("schema", new JsonObject());
        var triggers = schema["triggers"]!.AsArray();
        Assert.Equal("cheap:no update:INSTEAD OF UPDATE,item:gone:BEFORE DELETE,item:item_added:AFTER INSERT",
            string.Join(",", triggers.Select(t => $"{t!["table"]}:{t["name"]}:{t["timing"]} {t["event"]}")));
        Assert.Contains("INSERT INTO log", triggers[2]!["sql"]!.GetValue<string>());
        Assert.Equal(0, schema["routines"]!.AsArray().Count);   // SQLite has no stored procedures
        // a trigger is no table of the tree
        Assert.True(schema["objects"]!.AsArray().All(o => o!["name"]!.GetValue<string>() != "item_added"));
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
    public void ACsvFileIsImportedIntoATableAllOrNone()
    {
        if (!HavePython) return;
        using var p = new Project();
        string csv = Path.Combine(p.Root, "more.csv");
        File.WriteAllText(csv, "﻿Name;PRICE\nlamp;30\n\"desk; oak\";\nchair;7.5\n");   // a BOM, semicolons, a quoted field, an empty one
        Assert.Equal(3, p.Ask("import", new JsonObject { ["table"] = "item", ["csv"] = csv })["imported"]!.GetValue<int>());
        var rows = p.Ask("run", new JsonObject { ["sql"] = "SELECT name, price FROM item WHERE id > 3 ORDER BY id" });
        Assert.Equal("[[\"lamp\",{\"real\":\"30.0\"}],[\"desk; oak\",null],[\"chair\",7.5]]", rows["rows"]!.ToJsonString());

        File.WriteAllText(csv, "name,colour\nx,red\n");
        Assert.Contains("no column colour", p.Ask("import", new JsonObject { ["table"] = "item", ["csv"] = csv })["error"]!.GetValue<string>());
        File.WriteAllText(csv, "name,price\nok,1\nbad\n");
        Assert.Contains("nothing was imported", p.Ask("import", new JsonObject { ["table"] = "item", ["csv"] = csv })["error"]!.GetValue<string>());
        Assert.Equal(6, p.Ask("rows", new JsonObject { ["table"] = "item" })["total"]!.GetValue<int>());   // the first good line was not kept either
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
