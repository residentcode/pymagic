using System.Text;

namespace PyMagic.Server;

/// <summary>
/// Converts between character offsets (what the analysis uses) and LSP line/character positions: UTF-16 units (the same thing in .NET strings),
/// or UTF-8 bytes while <see cref="Utf8"/> is on, for a client that asked for them (`positionEncoding: "utf-8"`).
/// </summary>
public sealed class LineMap
{
    /// <summary>Whether positions count UTF-8 bytes: set by the server around each message of a client that chose them, and flowing into its tasks.</summary>
    public static readonly AsyncLocal<bool> Utf8 = new();

    readonly List<int> _starts = new() { 0 };
    readonly int _length;
    readonly string _text;

    public LineMap(string text)
    {
        _text = text;
        _length = text.Length;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') _starts.Add(i + 1);
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                _starts.Add(i + 1);
            }
        }
    }

    public int LineCount => _starts.Count;

    public Position PositionOf(int offset)
    {
        offset = Math.Clamp(offset, 0, _length);
        int lo = 0, hi = _starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        int column = offset - _starts[lo];
        return new Position(lo, Utf8.Value ? Encoding.UTF8.GetByteCount(_text.AsSpan(_starts[lo], column)) : column);
    }

    public int OffsetOf(Position p)
    {
        if (p.Line < 0) return 0;
        if (p.Line >= _starts.Count) return _length;
        int lineEnd = p.Line + 1 < _starts.Count ? _starts[p.Line + 1] : _length;
        int start = _starts[p.Line];
        if (!Utf8.Value) return Math.Min(start + Math.Max(p.Character, 0), lineEnd);
        // as many characters as fit in that many bytes (a pair of surrogates is one 4-byte character)
        int bytes = 0, i = start;
        while (i < lineEnd && bytes < p.Character)
        {
            int width = char.IsHighSurrogate(_text[i]) && i + 1 < lineEnd && char.IsLowSurrogate(_text[i + 1]) ? 2 : 1;
            bytes += Encoding.UTF8.GetByteCount(_text.AsSpan(i, width));
            i += width;
        }
        return i;
    }

    public Range RangeOf(int start, int end) => new(PositionOf(start), PositionOf(end));
}
