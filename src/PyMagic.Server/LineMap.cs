namespace PyMagic.Server;

/// <summary>Converts between character offsets (what the analysis uses) and LSP line/character positions (UTF-16 units, the same thing in .NET strings).</summary>
public sealed class LineMap
{
    readonly List<int> _starts = new() { 0 };
    readonly int _length;

    public LineMap(string text)
    {
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
        return new Position(lo, offset - _starts[lo]);
    }

    public int OffsetOf(Position p)
    {
        if (p.Line < 0) return 0;
        if (p.Line >= _starts.Count) return _length;
        int lineEnd = p.Line + 1 < _starts.Count ? _starts[p.Line + 1] : _length;
        return Math.Min(_starts[p.Line] + Math.Max(p.Character, 0), lineEnd);
    }

    public Range RangeOf(int start, int end) => new(PositionOf(start), PositionOf(end));
}
