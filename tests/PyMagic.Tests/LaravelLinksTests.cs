using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Laravel's names as links and completion (LaravelLinks): views, components, routes (a group's name prefix too), config keys, .env, translations, assets.</summary>
public class LaravelLinksTests
{
    static string Project()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_lllinks_" + Guid.NewGuid().ToString("N")[..8]);
        void W(string rel, string text) { string f = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, text); }
        W("artisan", "<?php");
        W("composer.json", "{\"require\":{\"laravel/framework\":\"^12.0\"}}");
        W("resources/views/users/index.blade.php", "<x-alert type=\"info\" />");
        W("resources/views/welcome.blade.php", "@extends('layouts.app')");
        W("resources/views/layouts/app.blade.php", "@yield('content')");
        W("resources/views/components/alert.blade.php", "<div>{{ $slot }}</div>");
        W("resources/views/components/forms/input.blade.php", "<input>");
        W("routes/web.php", string.Join("\n", new[]
        {
            "<?php",                                                                             // 0
            "use Illuminate\\Support\\Facades\\Route;",                                          // 1
            "Route::get('/', fn () => view('welcome'))->name('home');",                         // 2
            "Route::name('admin.')->prefix('admin')->group(function () {",                      // 3
            "    Route::get('/users', fn () => view('users.index'))->name('users');",          // 4
            "});",                                                                               // 5
            "Route::get('/about', fn () => 'about')->name('about');",                           // 6
            "Route::resource('photos', PhotoController::class);",                               // 7
        }));
        W("config/app.php", string.Join("\n", new[]
        {
            "<?php", "return [", "    'name' => env('APP_NAME', 'Laravel'),", "    'locale' => env('APP_LOCALE', 'en'),", "    'maintenance' => [", "        'driver' => 'file',", "    ],", "];",
        }));
        W(".env", "APP_NAME=Shop\nAPP_KEY=base64:abc\nDB_CONNECTION=sqlite\n");
        W("lang/en/auth.php", "<?php\nreturn [\n    'failed' => 'These credentials do not match.',\n    'throttle' => 'Too many tries.',\n];\n");
        W("public/css/site.css", "body {}");
        return root;
    }

    static (string File, int Line)? Go(string root, string text, string at)
    {
        int offset = text.IndexOf(at, StringComparison.Ordinal) + 1;
        return LaravelLinks.At(text, offset) is { } place ? LaravelLinks.Target(root, place) : null;
    }

    static string Rel(string root, (string File, int Line)? t) => t == null ? "none" : Path.GetRelativePath(root, t.Value.File).Replace('\\', '/') + ":" + t.Value.Line;

    [Fact]
    public void NamesInQuotesLeadToTheirFiles()
    {
        string root = Project();
        try
        {
            Assert.Equal("resources/views/users/index.blade.php:0", Rel(root, Go(root, "return view('users.index', ['u' => $u]);", "users.index")));
            Assert.Equal("resources/views/layouts/app.blade.php:0", Rel(root, Go(root, "@extends('layouts.app')", "layouts")));
            Assert.Equal("resources/views/welcome.blade.php:0", Rel(root, Go(root, "@includeWhen($ok, 'welcome')", "welcome")));
            Assert.Equal("resources/views/components/alert.blade.php:0", Rel(root, Go(root, "<x-alert type=\"info\" />", "alert")));
            Assert.Equal("resources/views/components/forms/input.blade.php:0", Rel(root, Go(root, "<x-forms.input name=\"a\" />", "forms")));
            Assert.Equal("routes/web.php:2", Rel(root, Go(root, "return redirect()->route('home');", "home")));
            Assert.Equal("routes/web.php:4", Rel(root, Go(root, "<a href=\"{{ route('admin.users') }}\">", "admin.users")));
            Assert.Equal("routes/web.php:6", Rel(root, Go(root, "to_route('about')", "about")));
            Assert.Equal("routes/web.php:7", Rel(root, Go(root, "route('photos.show', $p)", "photos")));
            Assert.Equal("config/app.php:5", Rel(root, Go(root, "config('app.maintenance.driver')", "app.m")));
            Assert.Equal("config/app.php:2", Rel(root, Go(root, "Config::get('app.name')", "app.name")));
            Assert.Equal(".env:1", Rel(root, Go(root, "env('APP_KEY')", "APP_KEY")));
            Assert.Equal("lang/en/auth.php:3", Rel(root, Go(root, "{{ __('auth.throttle') }}", "auth")));
            Assert.Equal("public/css/site.css:0", Rel(root, Go(root, "<link href=\"{{ asset('css/site.css') }}\">", "css/")));
            // a string that is not one of these, and a view the project has not
            Assert.Equal("none", Rel(root, Go(root, "echo strtoupper('home');", "home")));
            Assert.Equal("none", Rel(root, Go(root, "view('mail::message')", "mail")));
            Assert.Equal("none", Rel(root, Go(root, "view('../../secret')", "secret")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CompletionInTheQuotesGivesTheProjectsNames()
    {
        string root = Project();
        try
        {
            List<string> Labels(string text)
            {
                var place = LaravelLinks.At(text, text.Length - 2);
                Assert.True(place != null, text);
                return LaravelLinks.Complete(root, place!).Select(i => i!["label"]!.GetValue<string>()).ToList();
            }
            var views = Labels("view('')");
            Assert.True(views.Contains("users.index") && views.Contains("layouts.app") && views.Contains("components.alert"), string.Join(",", views));
            var routes = Labels("route('')");
            Assert.True(routes.Contains("home") && routes.Contains("admin.users") && routes.Contains("about") && routes.Contains("photos.index"), string.Join(",", routes));
            Assert.True(!routes.Contains("admin.about"), "the group's prefix ends with the group");
            var config = Labels("config('')");
            Assert.True(config.Contains("app.name") && config.Contains("app.maintenance.driver"), string.Join(",", config));
            var env = Labels("env('')");
            Assert.True(env.SequenceEqual(new[] { "APP_NAME", "APP_KEY", "DB_CONNECTION" }), string.Join(",", env));
            var lang = Labels("__('')");
            Assert.True(lang.Contains("auth.failed") && lang.Contains("auth.throttle"), string.Join(",", lang));
            var assets = Labels("asset('')");
            Assert.True(assets.Contains("css/site.css"), string.Join(",", assets));
            var comps = LaravelLinks.Complete(root, LaravelLinks.At("<x-", 3)!).Select(i => i!["label"]!.GetValue<string>()).ToList();
            Assert.True(comps.Contains("alert") && comps.Contains("forms.input"), string.Join(",", comps));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RouteNamesTakeThePrefixesOfEveryGroupAroundThem()
    {
        string text = string.Join("\n", new[]
        {
            "<?php",                                                                            // 0
            "Route::middleware('auth')",                                                        // 1
            "    ->prefix('admin')",                                                            // 2
            "    ->name('admin.')",                                                             // 3
            "    ->group(function () {",                                                        // 4
            "        // a brace in a comment: { and a name: ->name('nope')",                     // 5
            "        Route::get('/', fn () => '{')->name('home');",                             // 6
            "        Route::name('users.')->group(function () {",                               // 7
            "            Route::get('/users', [UserController::class, 'index'])->name('index');", // 8
            "            Route::controller(X::class)->as('x.')->group(function () {",           // 9
            "                Route::get('/x', 'show')->name('show');",                          // 10
            "            });",                                                                  // 11
            "        });",                                                                      // 12
            "        Route::resource('photos', PhotoController::class)->only(['index', 'show']);", // 13
            "    });",                                                                          // 14
            "Route::group(['prefix' => 'api', 'as' => 'api.'], function () {",                 // 15
            "    Route::apiResource('posts', PostController::class)->except('destroy');",       // 16
            "    Route::resource('tags', TagController::class)->names(['index' => 'tags.all']);", // 17
            "});",                                                                              // 18
            "Route::prefix('v2')->group(base_path('routes/v2.php'));",                          // 19
            "Route::get('/about', fn () => 'about')->name('about');",                           // 20
        });
        var routes = LaravelLinks.RoutesOf(text).Select(r => r.Name + ":" + r.Line).ToList();
        string all = string.Join(",", routes);
        foreach (var want in new[] { "admin.home:6", "admin.users.index:8", "admin.users.x.show:10", "admin.photos.index:13", "admin.photos.show:13",
                                     "api.posts.index:16", "api.posts.update:16", "api.tags.all:17", "api.tags.edit:17", "about:20" })
            Assert.True(routes.Contains(want), want + " in " + all);
        foreach (var not in new[] { "nope", "admin.photos.create", "api.posts.destroy", "api.posts.create", "api.tags.index", "admin.users.", "admin." })
            Assert.True(!routes.Any(r => r.StartsWith(not + ":")), not + " in " + all);
        // home, users.index, x.show; photos' 2; posts' 4 (an API resource has no create and edit); tags' 7; about
        Assert.Equal(17, routes.Count);
    }

    [Fact]
    public void NamesInABladePhpBlockLeadToTheirFilesToo()
    {
        string root = Project();
        try
        {
            string blade = string.Join("\n", new[]
            {
                "@php",
                "    $home = route(",
                "        'home'",
                "    );",
                "    $name = config('app.name');",
                "@endphp",
                "@php($users = route('admin.users'))",
                "<a href=\"{{ $home }}\">{{ __('auth.failed') }}</a>",
            });
            Assert.Equal("routes/web.php:2", Rel(root, Go(root, blade, "'home'")));
            Assert.Equal("config/app.php:2", Rel(root, Go(root, blade, "app.name")));
            Assert.Equal("routes/web.php:4", Rel(root, Go(root, blade, "admin.users")));
            Assert.Equal("lang/en/auth.php:2", Rel(root, Go(root, blade, "auth.failed")));
            // a quote that is a second argument on its line of its own is not the call's name
            Assert.Equal("none", Rel(root, Go(root, "route(\n  'home',\n  'about')", "'about'")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ARoutesFileAGroupIncludesHasTheGroupsPrefix()
    {
        string root = Project();
        void W(string rel, string text) { string f = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, text); }
        try
        {
            // Laravel 11's bootstrap/app.php: withRouting's then: groups a file of its own
            W("bootstrap/app.php", string.Join("\n", new[]
            {
                "<?php",
                "return Application::configure(basePath: dirname(__DIR__))",
                "    ->withRouting(",
                "        web: __DIR__.'/../routes/web.php',",
                "        then: function () {",
                "            Route::middleware('web')",
                "                ->name('admin.')",
                "                ->group(base_path('routes/admin.php'));",
                "        },",
                "    )->create();",
            }));
            W("routes/admin.php", string.Join("\n", new[]
            {
                "<?php",                                                                      // 0
                "Route::get('/admin', fn () => 'x')->name('dashboard');",                     // 1
                "Route::name('reports.')->group(function () {",                               // 2
                "    require __DIR__.'/reports.php';",                                         // 3
                "});",
            }));
            W("routes/reports.php", "<?php\nRoute::get('/daily', fn () => 'd')->name('daily');\n");
            // a file required at the top of web.php: no prefix
            W("routes/auth.php", "<?php\nRoute::get('/login', fn () => 'l')->name('login');\n");
            File.AppendAllText(Path.Combine(root, "routes", "web.php"), "\nrequire __DIR__.'/auth.php';\n");
            var routes = LaravelLinks.Routes(root).Select(r => r.Name + "@" + Path.GetFileName(r.File) + ":" + r.Line).ToList();
            string all = string.Join(", ", routes);
            foreach (var want in new[] { "admin.dashboard@admin.php:1", "admin.reports.daily@reports.php:1", "login@auth.php:1", "home@web.php:2", "admin.users@web.php:4" })
                Assert.True(routes.Contains(want), want + " in " + all);
            Assert.True(!routes.Any(r => r.StartsWith("dashboard@") || r.StartsWith("daily@") || r.StartsWith("reports.daily@")), all);
            Assert.Equal("routes/reports.php:1", Rel(root, Go(root, "route('admin.reports.daily')", "admin.reports")));
        }
        finally { Directory.Delete(root, true); }
    }
}
