namespace PyMagic.Core;

/// <summary>
/// The cancellation of the request being worked out (the language server's `$/cancelRequest` for the request that is running): long work
/// checks it between files and between functions and stops with <see cref="OperationCanceledException"/>. It flows with the request onto the
/// threads it starts (an <see cref="AsyncLocal{T}"/>), and work started apart from the request (the project's problems beside the loop) has none.
/// </summary>
public static class RequestCancellation
{
    static readonly AsyncLocal<CancellationToken> _current = new();

    /// <summary>The token of the request being worked out on this flow; none outside a request.</summary>
    public static CancellationToken Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>Stops the work when its request was cancelled.</summary>
    public static void Check() => _current.Value.ThrowIfCancellationRequested();
}
