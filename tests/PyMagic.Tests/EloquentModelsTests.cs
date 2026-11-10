using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Eloquent's columns and relations in completion (EloquentModels): a model's file, table, columns from the migrations and relations; and after
/// `$user->` in a copy of Laravel's app (PYMAGIC_TEST_LARAVEL_DIR) Phpactor's items with the users table's columns and the model's relations.
/// </summary>
public class EloquentModelsTests
{
    static string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    static void W(string root, string rel, string text)
    {
        string f = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    const string PostModel = """
        <?php

        namespace App\Models;

        use Illuminate\Database\Eloquent\Model;
        use Illuminate\Database\Eloquent\Relations\BelongsTo;

        class Post extends Model
        {
            public function author(): BelongsTo
            {
                return $this->belongsTo(User::class, 'author_id');
            }

            public function comments()
            {
                return $this->hasMany(\App\Models\Comment::class);
            }

            public function title(): string
            {
                return 'x';
            }
        }
        """;

    [Fact]
    public void ColumnsComeFromTheMigrationsAndRelationsFromTheModel()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_eloquent_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            W(root, "composer.json", "{\"autoload\":{\"psr-4\":{\"App\\\\\":\"app/\"}}}");
            W(root, "app/Models/Post.php", PostModel);
            W(root, "app/Models/Category.php", "<?php\nnamespace App\\Models;\nuse Illuminate\\Database\\Eloquent\\Model;\nclass Category extends Model {}\n");
            W(root, "app/Models/Archived.php", "<?php\nnamespace App\\Models;\nclass Archived extends Post\n{\n    protected $table = 'old_posts';\n}\n");
            W(root, "app/Services/Mailer.php", "<?php\nnamespace App\\Services;\nclass Mailer extends Base {}\n");
            W(root, "database/migrations/2024_01_01_000000_create_posts_table.php", """
                <?php
                return new class extends Migration {
                    public function up(): void
                    {
                        Schema::create('posts', function (Blueprint $table) {
                            $table->id();
                            $table->foreignId('author_id')->constrained('users');
                            $table->string('title');
                            $table->text('body')->nullable();
                            $table->morphs('taggable');
                            $table->unique(['title']);
                            $table->timestamps();
                            $table->softDeletes();
                        });
                        Schema::create('comments', function (Blueprint $t) { $t->id(); $t->string('text'); });
                    }
                    public function down(): void
                    {
                        Schema::dropIfExists('posts');
                    }
                };
                """);
            W(root, "database/migrations/2024_02_01_000000_change_posts.php", """
                <?php
                return new class extends Migration {
                    public function up(): void
                    {
                        Schema::table('posts', function (Blueprint $table) {
                            $table->boolean('published')->default(false);
                            $table->dropColumn('body');
                            $table->renameColumn('title', 'headline');
                        });
                    }
                    public function down(): void
                    {
                        Schema::table('posts', function (Blueprint $table) { $table->text('body'); });
                    }
                };
                """);
            Assert.Equal(Path.Combine(root, "app", "Models", "Post.php"), EloquentModels.FileOf(root, "\\App\\Models\\Post"));
            Assert.Equal("posts", EloquentModels.TableOf(PostModel));
            Assert.Equal("categories", EloquentModels.TableOf(File.ReadAllText(Path.Combine(root, "app", "Models", "Category.php"))));
            string archived = File.ReadAllText(Path.Combine(root, "app", "Models", "Archived.php"));
            Assert.Equal("old_posts", EloquentModels.TableOf(archived));
            Assert.True(EloquentModels.IsModel(root, archived), "a model through a model of the project");
            Assert.True(!EloquentModels.IsModel(root, File.ReadAllText(Path.Combine(root, "app", "Services", "Mailer.php"))), "a class that is no model");

            var columns = EloquentModels.Columns(root, "posts").Select(c => c.Name + ":" + c.Type).ToList();
            Assert.Equal("id:id | author_id:foreignId | headline:string | taggable_type:string | taggable_id:unsignedBigInteger | created_at:timestamp | updated_at:timestamp | deleted_at:timestamp | published:boolean",
                string.Join(" | ", columns));
            Assert.Equal("id | text", string.Join(" | ", EloquentModels.Columns(root, "comments").Select(c => c.Name)));

            var relations = EloquentModels.Relations(PostModel).Select(r => $"{r.Name}:{r.Kind}:{r.Related}").ToList();
            Assert.Equal("author:belongsTo:User | comments:hasMany:Comment", string.Join(" | ", relations));

            var items = EloquentModels.Items(root, "App\\Models\\Post")!;
            Assert.True(items.Any(i => i!["label"]!.GetValue<string>() == "headline" && i["detail"]!.GetValue<string>() == "string column of posts"), items.ToJsonString());
            Assert.True(items.Any(i => i!["label"]!.GetValue<string>() == "author" && i["detail"]!.GetValue<string>() == "belongsTo User relation"), items.ToJsonString());
            Assert.True(EloquentModels.Items(root, "App\\Services\\Mailer") == null, "no items for a class that is no model");
            Assert.Equal("App\\Models\\User | Illuminate\\Support\\Collection", string.Join(" | ", EloquentModels.ClassesIn("```php\n$user: \\App\\Models\\User|null\n```\nor Illuminate\\Support\\Collection")));
            // Phpactor's hover of a variable names its class short: the document's use statement, alias or namespace says which
            Assert.Equal("App\\Models\\User", string.Join(" | ", EloquentModels.ClassesIn("variable user: `Ⓒ User`\n", "<?php\nuse App\\Models\\User;\n")));
            Assert.Equal("App\\Models\\Post", string.Join(" | ", EloquentModels.ClassesIn("variable p: `Ⓒ Article`", "<?php\nuse App\\Models\\Post as Article;\n")));
            Assert.Equal("Shop\\Order", string.Join(" | ", EloquentModels.ClassesIn("variable o: `Ⓒ Order`", "<?php\nnamespace Shop;\n")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AfterAUserArrowPhpactorsItemsHaveItsColumnsAndRelations()
    {
        if (V("PYMAGIC_PHP") is not { } php || V("PYMAGIC_TEST_LARAVEL_DIR") is not { } source || PhpLanguageServer.Phar() == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_eloquent_app_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, f);
            if (rel.StartsWith("node_modules") || rel.StartsWith(".git" + Path.DirectorySeparatorChar) || rel.StartsWith("storage" + Path.DirectorySeparatorChar)) continue;
            string to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        // a relation of the user's: posts
        string userFile = Path.Combine(dir, "app", "Models", "User.php");
        string user = File.ReadAllText(userFile);
        int last = user.LastIndexOf('}');
        File.WriteAllText(userFile, user[..last] + "\n    public function posts()\n    {\n        return $this->hasMany(Post::class);\n    }\n}\n");
        W(dir, "app/Models/Post.php", PostModel);
        string home = Path.Combine(Path.GetTempPath(), "pm_phpactor_home_" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("PYMAGIC_PHPACTOR_HOME", home);
        try
        {
            string file = Path.Combine(dir, "routes", "pm-eloquent.php");
            // line 2: a parameter typed User; line 4: `$user->em` being typed, last
            string text = "<?php\nuse App\\Models\\User;\nfunction shown(User $u) { return $u->name; }\n$user = new User();\n$user->em\n";
            var log = new ConcurrentQueue<string>();
            using var server = new PhpLanguageServer(php, dir, _ => { }, log.Enqueue);
            string uri = LanguageService.UriOfPath(file);
            server.Notify("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "php", ["version"] = 1, ["text"] = text } });
            JsonObject At(int line, int character) => new() { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = line, ["character"] = character } };
            List<JsonNode> Items(int line, int character, string wanted)
            {
                var items = new List<JsonNode>();
                for (var until = DateTime.UtcNow.AddSeconds(300); !items.Any(i => i["label"]!.GetValue<string>() == wanted) && DateTime.UtcNow < until; Thread.Sleep(items.Count == 0 ? 2000 : 500))
                    items = (server.Request("textDocument/completion", At(line, character)).GetAwaiter().GetResult()?["items"] as JsonArray ?? new JsonArray()).Select(i => i!).ToList();
                return items;
            }
            // `$user->em`: the users table's columns (Laravel's own migration) and Phpactor's methods together
            var items = Items(4, 9, "email");
            if (items.Count == 0)
                throw new AssertionException("nothing; completion: " + server.Request("textDocument/completion", At(4, 7)).GetAwaiter().GetResult()?.ToJsonString()[..Math.Min(400, 9999)]
                    + " hover: " + server.Request("textDocument/hover", At(4, 3)).GetAwaiter().GetResult()?.ToJsonString());
            var labels = items.Select(i => i["label"]!.GetValue<string>()).ToList();
            Assert.True(labels.Contains("email") && labels.Contains("email_verified_at") && labels.Contains("remember_token") && labels.Contains("created_at"), string.Join(",", labels.Take(30)) + " | " + string.Join(" | ", log.TakeLast(5)));
            Assert.True(labels.Contains("posts"), string.Join(",", labels.Take(40)));
            // at `$user->` itself: Phpactor's methods (Notifiable's notify) and the columns together
            var both = Items(4, 7, "notify").Select(i => i["label"]!.GetValue<string>()).ToList();
            Assert.True(both.Contains("notify") && both.Contains("email"), string.Join(",", both.Take(40)));
            Assert.Equal("string column of users", items.First(i => i["label"]!.GetValue<string>() == "email")["detail"]!.GetValue<string>());
            // a parameter typed User
            // `$u->` of the parameter (completion at the start of `name`)
            var param = Items(2, 37, "password").Select(i => i["label"]!.GetValue<string>()).ToList();
            Assert.True(param.Contains("password") && param.Contains("name"), string.Join(",", param.Take(30)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYMAGIC_PHPACTOR_HOME", null);
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
