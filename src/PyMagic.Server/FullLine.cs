using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using LLama;
using LLama.Common;
using LLama.Native;

namespace PyMagic.Server;

/// <summary>
/// Full line completion (a plugin of Settings > Plugins, as JetBrains' Full Line Code Completion is): a small code model that comes with PyMagic,
/// Qwen2.5-Coder 0.5B (models/fetch.mjs), run on the CPU by llama.cpp, writes the rest of the line at the caret from the code before and after it
/// (fill in the middle), and FullLineChecks drops what is likely wrong. Nothing leaves the computer. The model is loaded at the first suggestion
/// (about half a second, 600 MB of memory) and freed when the plugin is switched off. One suggestion is made at a time: a newer one cancels the
/// one being written, and the work done for the text before the caret is kept for the next one (typing on adds only the new characters).
/// </summary>
public static class FullLine
{
    public const string ModelFile = "qwen2.5-coder-0.5b-q8_0.gguf";
    const int ContextSize = 4096;
    const int MaxTokens = 48;

    static readonly SemaphoreSlim Gate = new(1, 1);
    static CancellationTokenSource? _current;
    static LLamaWeights? _weights;
    static LLamaContext? _context;
    static readonly List<LLamaToken> _evaluated = new();   // the prompt tokens whose work is in the context (positions 0..Count-1)
    static string? _error;
    static int _resolverSet;

    /// <summary>A suggestion: the rest of the line, or null; how long it took; why there is none when that is an error.</summary>
    public record Suggestion(string? Text, long Ms, string? Error = null);

    /// <summary>The bundled model's file: PYMAGIC_FULLLINE_MODEL, or models/ in a folder above the engine (the repo in development, the app's resources).</summary>
    public static string? ModelPath()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_FULLLINE_MODEL") is { Length: > 0 } env) return File.Exists(env) ? env : null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "models", ModelFile);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The build of llama.cpp this CPU runs best: AVX-512, AVX2, AVX, or none.</summary>
    public static string Variant() => Avx512F.IsSupported ? "avx512" : Avx2.IsSupported ? "avx2" : Avx.IsSupported ? "avx" : "noavx";

    /// <summary>What the settings show: whether the model is there, loaded, the CPU build, the last error.</summary>
    public static object Status() => new { available = ModelPath() != null, loaded = _context != null, model = ModelFile, variant = Variant(), error = _error };

    /// <summary>
    /// LLamaSharp is built for .NET Standard here, where it looks for llama.dll beside the program only; the backend package puts one build per CPU
    /// feature under runtimes/win-x64/native/{avx512,avx2,avx,noavx}. The best one this CPU runs is loaded (with its ggml DLLs, from its folder).
    /// </summary>
    static void SetResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1) return;
        string[] order = ["avx512", "avx2", "avx", "noavx"];
        var variants = order.SkipWhile(v => v != Variant()).ToArray();
        NativeLibrary.SetDllImportResolver(typeof(LLamaWeights).Assembly, (name, _, _) =>
        {
            string file = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
            foreach (string v in variants)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", v, file);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)) return handle;
            }
            return IntPtr.Zero;
        });
    }

    static void Load()
    {
        if (_context != null) return;
        string path = ModelPath() ?? throw new FileNotFoundException("The model of full line completion is not here: " + ModelFile);
        SetResolver();
        NativeLibraryConfig.All.WithLogCallback((_, _) => { });
        var p = new ModelParams(path) { ContextSize = ContextSize, GpuLayerCount = 0, BatchSize = 512, Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        _weights = LLamaWeights.LoadFromFile(p);
        _context = _weights.CreateContext(p);
        _evaluated.Clear();
    }

    /// <summary>Frees the model (the plugin was switched off).</summary>
    public static void Unload()
    {
        Interlocked.Exchange(ref _current, null)?.Cancel();
        Gate.Wait();
        try
        {
            _context?.Dispose();
            _weights?.Dispose();
            _context = null;
            _weights = null;
            _evaluated.Clear();
        }
        finally { Gate.Release(); }
    }

    /// <summary>The rest of the line at the caret, or null (none fits, the caret is not where a line can be completed, or a newer request came).</summary>
    public static async Task<Suggestion> SuggestAsync(string text, int offset, string language, CancellationToken aborted)
    {
        var watch = Stopwatch.StartNew();
        if (!Plugins.On("fullline")) return new(null, 0, "off");
        if (!FullLineChecks.CanComplete(text, offset, language)) return new(null, 0);
        var mine = new CancellationTokenSource();
        Interlocked.Exchange(ref _current, mine)?.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, mine.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        try { await Gate.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { return new(null, watch.ElapsedMilliseconds); }
        try
        {
            var (prefix, suffix) = FullLineChecks.Window(text, offset);
            string? raw = await Task.Run(() => Generate(prefix, suffix, linked.Token));
            _error = null;
            return new(raw == null ? null : FullLineChecks.Clean(raw, prefix, suffix, language), watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { return new(null, watch.ElapsedMilliseconds); }
        catch (Exception e)
        {
            _error = e.Message;
            return new(null, watch.ElapsedMilliseconds, e.Message);
        }
        finally { Gate.Release(); }
    }

    /// <summary>What the model writes between the prefix and the suffix, up to the end of the line (greedy: the likeliest token each time).</summary>
    static string? Generate(string prefix, string suffix, CancellationToken cancel)
    {
        Load();
        var context = _context!;
        var handle = context.NativeHandle;
        var tokens = context.Tokenize("<|fim_prefix|>" + prefix + "<|fim_suffix|>" + suffix + "<|fim_middle|>", false, true);
        if (tokens.Length + MaxTokens > ContextSize) return null;

        // the work kept from the last prompt, up to where this one differs; the last token is always run again for its logits
        int same = 0;
        while (same < _evaluated.Count && same < tokens.Length - 1 && _evaluated[same] == tokens[same]) same++;
        handle.MemorySequenceRemove((LLamaSeqId)0, same, -1);
        _evaluated.RemoveRange(same, _evaluated.Count - same);

        var batch = new LLamaBatch();
        int logitAt = 0;
        for (int start = same; start < tokens.Length; start += 512)
        {
            cancel.ThrowIfCancellationRequested();
            batch.Clear();
            int end = Math.Min(tokens.Length, start + 512);
            for (int i = start; i < end; i++) batch.Add(tokens[i], i, (LLamaSeqId)0, i == tokens.Length - 1);
            if (context.Decode(batch) != DecodeResult.Ok) return null;
            for (int i = start; i < end; i++) _evaluated.Add(tokens[i]);
            logitAt = batch.TokenCount - 1;
        }

        var vocab = _weights!.NativeHandle.Vocab;
        var decoder = new StreamingTokenDecoder(context);
        var written = new System.Text.StringBuilder();
        int pos = tokens.Length;
        for (int n = 0; n < MaxTokens; n++)
        {
            cancel.ThrowIfCancellationRequested();
            var logits = handle.GetLogitsIth(logitAt);
            int best = 0;
            for (int i = 1; i < logits.Length; i++)
                if (logits[i] > logits[best]) best = i;
            var token = (LLamaToken)best;
            if (token.IsEndOfGeneration(vocab) || token.IsControl(vocab)) break;
            decoder.Add(token);
            written.Append(decoder.Read());
            if (written.ToString().Contains('\n')) break;
            batch.Clear();
            batch.Add(token, pos++, (LLamaSeqId)0, true);
            logitAt = 0;
            if (context.Decode(batch) != DecodeResult.Ok) break;
        }
        return written.ToString();
    }
}
