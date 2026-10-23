namespace PyMagic.Core.Syntax;

public enum TokenKind
{
    EndOfFile,
    Newline,      // logical line end
    Indent,
    Dedent,
    Name,
    Keyword,
    Number,
    String,       // includes prefix and quotes; f-strings are one token for now
    Operator,     // operators and delimiters
    Comment,
    Error,
}

/// <summary>A token is a span of the source text plus its kind. Text is sliced on demand.</summary>
public readonly record struct Token(TokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
    public string Text(string source) => source.Substring(Start, Length);
}
