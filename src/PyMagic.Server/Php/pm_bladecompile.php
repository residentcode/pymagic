<?php
// PyMagic, before a Laravel server is debugged: the project's Blade templates compiled as the server compiles them (the app booted, each template
// found by its view name through Laravel's own finder, which spells its path as a request does, then compiled by Laravel's Blade compiler when
// it is out of date), so a breakpoint of a template is in its compiled file for the first request too. Unlike `php artisan view:cache` it clears
// nothing. Run in the project's folder: php pm_bladecompile.php
error_reporting(E_ALL & ~E_DEPRECATED & ~E_NOTICE);
if (!is_file('vendor/autoload.php') || !is_file('bootstrap/app.php')) exit(0);
require 'vendor/autoload.php';
$app = require 'bootstrap/app.php';
$app->make(Illuminate\Contracts\Console\Kernel::class)->bootstrap();
$finder = $app['view']->getFinder();
$blade = $app['blade.compiler'];
$base = $app->resourcePath('views');
if (!is_dir($base)) exit(0);
$compiled = 0;
foreach (new RecursiveIteratorIterator(new RecursiveDirectoryIterator($base, FilesystemIterator::SKIP_DOTS)) as $file) {
    $name = $file->getFilename();
    if (substr($name, -10) !== '.blade.php') continue;
    $rel = substr($file->getPathname(), strlen($base) + 1, -10);
    $view = str_replace(['/', '\\'], '.', $rel);
    try {
        $path = $finder->find($view);
        if ($blade->isExpired($path)) { $blade->compile($path); $compiled++; }
    } catch (Throwable $e) {
        // a template that does not compile: the request tells why
    }
}
echo "compiled $compiled\n";
