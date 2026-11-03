using PyMagic.Server;

// pymagic-server [--typeshed <dir>] [--root <dir>]... [--lib <dir>]...
// Speaks LSP over stdin/stdout. Everything else (logging) goes to stderr.
var service = new LanguageService();
// the syntax trees of typeshed's stubs and the libraries, kept between starts (PYMAGIC_PARSED_CACHE elsewhere)
PyMagic.Core.Syntax.ParsedFileCache.Folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyMagic", "parsed");
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--typeshed": service.TypeshedPath = args[++i]; break;
        case "--root": service.SourceRoots.Add(args[++i]); break;
        case "--lib": service.LibraryRoots.Add(args[++i]); break;
    }
}

var channel = new MessageChannel(Console.OpenStandardInput(), Console.OpenStandardOutput());
var server = new Server(service, msg => channel.Write(msg));

// The evaluator recurses deeply on real-world code, so run on a thread with a large stack.
int exit = 0;
var thread = new Thread(() => exit = server.Run(channel), 512 * 1024 * 1024);
thread.Start();
thread.Join();
return exit;
