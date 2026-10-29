using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PyMagic.Server;

/// <summary>
/// A real console for the Terminal panel: a shell attached to a Windows pseudo console (ConPTY, Windows 10 1809 and later), so programs see a
/// terminal: the interactive `python` prompt with its line editing, progress bars that redraw a line, full-screen programs, colours, Ctrl+C.
/// What the shell writes comes back as text with its control sequences (the page's terminal draws them); keystrokes go in as text; the size
/// follows the panel. The project's interpreter is first on the PATH and a virtual environment is active, as in the line-based terminal.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConsoleSession : IDisposable
{
    IntPtr _console;
    SafeFileHandle? _input;
    FileStream? _inputStream;
    FileStream? _outputStream;
    IntPtr _process;
    int _processId;
    readonly object _gate = new();
    bool _disposed;

    public int ProcessId => _processId;

    /// <summary>Starts <paramref name="commandLine"/> in a pseudo console of the given size.</summary>
    /// <param name="output">Receives what the console writes, as it comes; called from a background thread.</param>
    /// <param name="exited">Receives the exit code once the program has ended and its last output was read.</param>
    /// <exception cref="Win32Exception">The pseudo console or the process could not be created.</exception>
    public void Start(string commandLine, string cwd, IDictionary<string, string?> environment, int cols, int rows, Action<string> output, Action<int> exited)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) || !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "the pipes of the console could not be created");
        int hr = CreatePseudoConsole(new Coord(Clamp(cols), Clamp(rows)), inputRead, outputWrite, 0, out _console);
        if (hr != 0) throw new Win32Exception(hr, "the pseudo console could not be created (it needs Windows 10 1809 or later)");

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        IntPtr env = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // without this the child takes the host's own redirected handles instead of the console
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.lpAttributeList = attributes;
            env = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            var command = new StringBuilder(commandLine);
            if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, env, cwd, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"'{commandLine}' could not be started");
            CloseHandle(info.hThread);
            _process = info.hProcess;
            _processId = info.dwProcessId;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
            // the console holds its own ends of the pipes now
            inputRead.Dispose();
            outputWrite.Dispose();
        }

        _input = inputWrite;
        _inputStream = new FileStream(inputWrite, FileAccess.Write, 1);
        _outputStream = new FileStream(outputRead, FileAccess.Read, 1);
        var reader = new Thread(() => ReadOutput(output)) { IsBackground = true, Name = "console output" };
        reader.Start();
        var waiter = new Thread(() =>
        {
            WaitForSingleObject(_process, INFINITE);
            GetExitCodeProcess(_process, out uint code);
            // the console keeps the output open until it is closed: what the program wrote last is read first
            Thread.Sleep(150);
            CloseConsole();
            reader.Join(2000);
            exited(unchecked((int)code));
        }) { IsBackground = true, Name = "console exit" };
        waiter.Start();
    }

    void ReadOutput(Action<string> output)
    {
        var decoder = Encoding.UTF8.GetDecoder();   // a character cut between two reads is put together
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int n;
            while (_outputStream != null && (n = _outputStream.Read(bytes, 0, bytes.Length)) > 0)
            {
                int count = decoder.GetChars(bytes, 0, n, chars, 0);
                if (count > 0) output(new string(chars, 0, count));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { /* closed */ }
    }

    /// <summary>Keystrokes, as the console reads them (Enter is "\r", Ctrl+C is "\u0003").</summary>
    public void Write(string text)
    {
        lock (_gate)
        {
            if (_inputStream == null) return;
            try { var b = Encoding.UTF8.GetBytes(text); _inputStream.Write(b, 0, b.Length); _inputStream.Flush(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { /* the program has ended */ }
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_gate)
            if (_console != IntPtr.Zero) ResizePseudoConsole(_console, new Coord(Clamp(cols), Clamp(rows)));
    }

    static short Clamp(int n) => (short)Math.Clamp(n, 2, 1000);

    /// <summary>The variables as Windows wants them: `name=value` each ended by a NUL, sorted by name, the whole ended by one more NUL.</summary>
    public static string EnvironmentBlock(IDictionary<string, string?> environment)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in environment.Where(kv => kv.Value != null && kv.Key.Length > 0 && !kv.Key.Contains('=')).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(k).Append('=').Append(v).Append('\0');
        return sb.Append('\0').ToString();
    }

    void CloseConsole()
    {
        lock (_gate)
        {
            if (_console != IntPtr.Zero) { ClosePseudoConsole(_console); _console = IntPtr.Zero; }
            _inputStream?.Dispose();
            _inputStream = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_process != IntPtr.Zero)
        {
            // the shell and what it started go with the console
            TerminateProcess(_process, 1);
            KillTree(_processId);
        }
        CloseConsole();
        _outputStream?.Dispose();
        if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
    }

    static void KillTree(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { /* already gone */ }
    }

    // ------------------------------------------------------------ Windows

    const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const int STARTF_USESTDHANDLES = 0x00000100;
    const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    struct Coord { public short X, Y; public Coord(short x, short y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? cwd, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
