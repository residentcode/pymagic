<?php
// PyMagic's start of Phpactor (phpactor.phar, named by PYMAGIC_PHPACTOR_PHAR) with a PHP that has no posix extension, as Windows' PHP has
// none: what Phpactor's own start (bin/phpactor) does, without its check of that extension. Phpactor calls posix only where Windows does not
// go (amphp's POSIX process runner, Monolog's signal handler) and for the user id of the XDG folders (posix_getuid): that one, and
// posix_isatty, are given here. The arguments are Phpactor's (language-server).
if (!function_exists('posix_getuid')) {
    function posix_getuid(): int { return 0; }
}
if (!function_exists('posix_geteuid')) {
    function posix_geteuid(): int { return 0; }
}
if (!function_exists('posix_getpid')) {
    function posix_getpid(): int { return getmypid() ?: 0; }
}
if (!function_exists('posix_isatty')) {
    function posix_isatty($stream): bool { return function_exists('stream_isatty') && is_resource($stream) ? stream_isatty($stream) : false; }
}

// the phar is named by PYMAGIC_PHPACTOR_PHAR: the processes Phpactor starts itself (its index workers, `php <its program> index:…`) are
// started with this file as its program, and so get past the same check
$phar = getenv('PYMAGIC_PHPACTOR_PHAR') ?: '';
if ($phar === '' || !is_file($phar)) {
    fwrite(STDERR, "pm_phpactor: phpactor.phar not found (PYMAGIC_PHPACTOR_PHAR): $phar\n");
    exit(255);
}

error_reporting(\E_ALL ^ \E_DEPRECATED ^ \E_NOTICE);
ini_set('display_errors', 'stderr');

$autoloadFile = 'phar://' . str_replace('\\', '/', realpath($phar)) . '/vendor/autoload.php';
require_once $autoloadFile;

$application = new \Phpactor\Application(dirname($autoloadFile), __FILE__);
$output = new \Symfony\Component\Console\Output\ConsoleOutput();
try {
    $application->run(null, $output);
} catch (\Exception $e) {
    $application->renderThrowable($e, $output);
    exit(255);
}
