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
}
