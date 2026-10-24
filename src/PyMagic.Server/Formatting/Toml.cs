using System.Globalization;
using System.Text;

namespace PyMagic.Server.Formatting;

/// <summary>
/// A reader for the part of TOML that configuration files use: tables and arrays of tables, dotted and quoted keys,
/// strings (basic, literal, multi-line), integers, floats, booleans, dates (kept as text), arrays and inline tables.
/// Tables become <c>Dictionary&lt;string, object?&gt;</c>, arrays <c>List&lt;object?&gt;</c>.
/// </summary>
public static class Toml
{
    /// <summary>The document as nested dictionaries, or null when it is not valid TOML.</summary>
    public static Dictionary<string, object?>? Parse(string text)
    {
        try { return new Reader(text).Document(); }
        catch (FormatException) { return null; }
    }

    /// <summary>The value at a dotted path such as "tool.black.line-length", or null.</summary>
    public static object? At(Dictionary<string, object?>? root, string path)
    {
        object? cur = root;
        foreach (var part in path.Split('.'))
        {
            if (cur is not Dictionary<string, object?> d || !d.TryGetValue(part, out cur)) return null;
        }
        return cur;
    }

    sealed class Reader
    {
        readonly string _s;
        int _p;

        public Reader(string text) { _s = text.TrimStart('﻿'); }

        public Dictionary<string, object?> Document()
        {
            var root = new Dictionary<string, object?>();
            var current = root;
            while (true)
            {
                SkipBlank();
                if (_p >= _s.Length) return root;
                if (_s[_p] == '[')
                {
                    bool array = _p + 1 < _s.Length && _s[_p + 1] == '[';
                    _p += array ? 2 : 1;
                    var path = KeyPath();
                    SkipSpaces();
                    Expect(']');
                    if (array) Expect(']');
                    current = array ? NewArrayItem(root, path) : Table(root, path);
                    EndOfLine();
                }
                else
                {
                    var path = KeyPath();
                    SkipSpaces();
                    Expect('=');
                    SkipSpaces();
                    var value = Value();
                    var target = current;
                    for (int i = 0; i < path.Count - 1; i++) target = Table(target, new List<string> { path[i] });
                    target[path[^1]] = value;
                    EndOfLine();
                }
            }
        }

        static Dictionary<string, object?> Table(Dictionary<string, object?> from, List<string> path)
        {
            var cur = from;
            foreach (var key in path)
            {
                if (!cur.TryGetValue(key, out var next) || next == null) cur[key] = next = new Dictionary<string, object?>();
                if (next is List<object?> list && list.Count > 0 && list[^1] is Dictionary<string, object?> last) next = last;
                cur = next as Dictionary<string, object?> ?? throw new FormatException("a key is used as a table");
            }
            return cur;
        }

        static Dictionary<string, object?> NewArrayItem(Dictionary<string, object?> root, List<string> path)
        {
            var parent = path.Count > 1 ? Table(root, path.GetRange(0, path.Count - 1)) : root;
            string name = path[^1];
            if (!parent.TryGetValue(name, out var existing) || existing == null) parent[name] = existing = new List<object?>();
            var list = existing as List<object?> ?? throw new FormatException("a key is used as an array of tables");
            var item = new Dictionary<string, object?>();
            list.Add(item);
            return item;
        }

        // ---------------------------------------------------------- lexical helpers

        void SkipSpaces() { while (_p < _s.Length && (_s[_p] == ' ' || _s[_p] == '\t')) _p++; }

        /// <summary>Spaces, line breaks and comments.</summary>
        void SkipBlank()
        {
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (c is ' ' or '\t' or '\r' or '\n') _p++;
                else if (c == '#') { while (_p < _s.Length && _s[_p] != '\n') _p++; }
                else break;
            }
        }

        void EndOfLine()
        {
            SkipSpaces();
            if (_p < _s.Length && _s[_p] == '#') while (_p < _s.Length && _s[_p] != '\n') _p++;
            if (_p < _s.Length && _s[_p] == '\r') _p++;
            if (_p < _s.Length && _s[_p] != '\n') throw new FormatException("unexpected text after a value");
        }

        void Expect(char c)
        {
            if (_p >= _s.Length || _s[_p] != c) throw new FormatException($"expected '{c}'");
            _p++;
        }

        List<string> KeyPath()
        {
            var parts = new List<string>();
            while (true)
            {
                SkipSpaces();
                parts.Add(Key());
                SkipSpaces();
                if (_p < _s.Length && _s[_p] == '.') { _p++; continue; }
                return parts;
            }
        }

        string Key()
        {
            if (_p < _s.Length && _s[_p] == '"') return BasicString();
            if (_p < _s.Length && _s[_p] == '\'') return LiteralString();
            int start = _p;
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] is '_' or '-')) _p++;
            if (_p == start) throw new FormatException("a key was expected");
            return _s[start.._p];
        }

        // ---------------------------------------------------------- values

        object? Value()
        {
            if (_p >= _s.Length) throw new FormatException("a value was expected");
            char c = _s[_p];
            if (c == '"') return StartsWith("\"\"\"") ? MultiLine('"') : BasicString();
            if (c == '\'') return StartsWith("'''") ? MultiLine('\'') : LiteralString();
            if (c == '[') return Array();
            if (c == '{') return InlineTable();
            if (StartsWith("true") && !IsWordChar(4)) { _p += 4; return true; }
            if (StartsWith("false") && !IsWordChar(5)) { _p += 5; return false; }
            return Number();
        }

        bool StartsWith(string text) => string.CompareOrdinal(_s, _p, text, 0, text.Length) == 0;
        bool IsWordChar(int offset) => _p + offset < _s.Length && (char.IsLetterOrDigit(_s[_p + offset]) || _s[_p + offset] == '_');

        object? Number()
        {
            int start = _p;
            while (_p < _s.Length && !(_s[_p] is ',' or ']' or '}' or '\n' or '\r' or '#' or ' ' or '\t')) _p++;
            string token = _s[start.._p].Replace("_", "");
            if (token.Length == 0) throw new FormatException("a value was expected");
            if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
            if (token.StartsWith("0x") && long.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out l)) return l;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            return token;   // a date or time, or inf/nan
        }

        string BasicString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (_p < _s.Length && _s[_p] != '"')
            {
                if (_s[_p] == '\n') throw new FormatException("a string runs over a line break");
                if (_s[_p] == '\\') sb.Append(Escape());
                else sb.Append(_s[_p++]);
            }
            Expect('"');
            return sb.ToString();
        }

        string Escape()
        {
            _p++;   // the backslash
            if (_p >= _s.Length) throw new FormatException("a string ends in a backslash");
            char c = _s[_p++];
            switch (c)
            {
                case 'n': return "\n";
                case 't': return "\t";
                case 'r': return "\r";
                case 'b': return "\b";
                case 'f': return "\f";
                case '"': return "\"";
                case '\\': return "\\";
                case 'u' or 'U':
                {
                    int n = c == 'u' ? 4 : 8;
                    if (_p + n > _s.Length || !int.TryParse(_s.AsSpan(_p, n), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)) throw new FormatException("bad unicode escape");
                    _p += n;
                    return char.ConvertFromUtf32(code);
                }
                default: throw new FormatException("unknown escape");
            }
        }

        string LiteralString()
        {
            Expect('\'');
            int start = _p;
            while (_p < _s.Length && _s[_p] != '\'' && _s[_p] != '\n') _p++;
            string text = _s[start.._p];
            Expect('\'');
            return text;
        }

        string MultiLine(char quote)
        {
            _p += 3;
            if (_p < _s.Length && _s[_p] == '\r') _p++;
            if (_p < _s.Length && _s[_p] == '\n') _p++;       // a line break right after the opening quotes is dropped
            var sb = new StringBuilder();
            string close = new string(quote, 3);
            while (_p < _s.Length && !StartsWith(close))
            {
                if (quote == '"' && _s[_p] == '\\')
                {
                    if (_p + 1 < _s.Length && _s[_p + 1] is '\n' or '\r' or ' ' or '\t')
                    {
                        _p++;      // a backslash at the end of a line swallows the white space after it
                        while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++;
                        continue;
                    }
                    sb.Append(Escape());
                }
                else sb.Append(_s[_p++]);
            }
            if (_p >= _s.Length) throw new FormatException("a multi-line string is not closed");
            _p += 3;
            while (_p < _s.Length && _s[_p] == quote) { sb.Append(quote); _p++; }   // up to two more quotes belong to the text
            return sb.ToString();
        }

        List<object?> Array()
        {
            Expect('[');
            var list = new List<object?>();
            while (true)
            {
                SkipBlank();
                if (_p < _s.Length && _s[_p] == ']') { _p++; return list; }
                list.Add(Value());
                SkipBlank();
                if (_p < _s.Length && _s[_p] == ',') { _p++; continue; }
                SkipBlank();
                Expect(']');
                return list;
            }
        }

        Dictionary<string, object?> InlineTable()
        {
            Expect('{');
            var table = new Dictionary<string, object?>();
            SkipSpaces();
            if (_p < _s.Length && _s[_p] == '}') { _p++; return table; }
            while (true)
            {
                var path = KeyPath();
                SkipSpaces();
                Expect('=');
                SkipSpaces();
                var value = Value();
                var target = table;
                for (int i = 0; i < path.Count - 1; i++) target = Table(target, new List<string> { path[i] });
                target[path[^1]] = value;
                SkipSpaces();
                if (_p < _s.Length && _s[_p] == ',') { _p++; SkipSpaces(); continue; }
                Expect('}');
                return table;
            }
        }
    }
}
