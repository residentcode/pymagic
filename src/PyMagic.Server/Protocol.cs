namespace PyMagic.Server;

// Wire types of the Language Server Protocol subset PyMagic speaks. System.Text.Json writes records with camelCase names.

public sealed record Position(int Line, int Character);
public sealed record Range(Position Start, Position End);
public sealed record Location(string Uri, Range Range);

public static class Severity { public const int Error = 1, Warning = 2, Information = 3, Hint = 4; }
public static class DiagnosticTag { public const int Unnecessary = 1; }

public sealed record LspDiagnostic(Range Range, int Severity, string Message, string Source = "pymagic", int[]? Tags = null, string? Code = null);

public sealed record MarkupContent(string Kind, string Value);
public sealed record Hover(MarkupContent Contents, Range Range);

public static class CompletionKind
{
    public const int Method = 2, Function = 3, Field = 5, Variable = 6, Class = 7, Module = 9, Property = 10,
        Keyword = 14, Constant = 21, TypeParameter = 25;
}
public sealed record CompletionItem(string Label, int Kind, string? Detail = null, string? SortText = null);

public static class SymbolKind
{
    public const int Module = 2, Class = 5, Method = 6, Property = 7, Field = 8, Constructor = 9, Function = 12,
        Variable = 13, Constant = 14, TypeParameter = 26;
}
public sealed record DocumentSymbol(string Name, int Kind, Range Range, Range SelectionRange, string? Detail, List<DocumentSymbol> Children);

public sealed record TextEdit(Range Range, string NewText);
public sealed record WorkspaceEdit(Dictionary<string, List<TextEdit>> Changes);
public sealed record PrepareRenameResult(Range Range, string Placeholder);

/// <summary>A request the server understood but cannot satisfy (for example renaming a library symbol).</summary>
public sealed class ServiceException(string message) : Exception(message);

/// <summary>A command the client runs when the action is chosen (the PyMagic page handles pymagic.* commands).</summary>
public sealed record LspCommand(string Title, string Command, object[]? Arguments = null);

/// <summary>A fix: either text edits, or a command, or both.</summary>
public sealed record CodeAction(string Title, string Kind, WorkspaceEdit? Edit, bool IsPreferred = false, LspCommand? Command = null);
