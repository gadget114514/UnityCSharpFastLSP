using System.Text.Json.Serialization;

namespace UnityCSharpFastLSP.Protocol;

public readonly record struct Position(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character
);

public readonly record struct Range(
    [property: JsonPropertyName("start")] Position Start,
    [property: JsonPropertyName("end")] Position End
);

public readonly record struct Location(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range
);

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed record Diagnostic(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("severity")] DiagnosticSeverity Severity,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("message")] string Message
);

public sealed record PublishDiagnosticsParams(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int? Version,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<Diagnostic> Diagnostics
);

public enum SymbolKind
{
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

public sealed record DocumentSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("kind")] SymbolKind Kind,
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("selectionRange")] Range SelectionRange,
    [property: JsonPropertyName("children")] List<DocumentSymbol>? Children = null
);

public sealed record TextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri
);

public sealed record VersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int Version
);

public sealed record TextDocumentItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("languageId")] string LanguageId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("text")] string Text
);

public sealed record TextDocumentContentChangeEvent(
    [property: JsonPropertyName("range")] Range? Range,
    [property: JsonPropertyName("rangeLength")] int? RangeLength,
    [property: JsonPropertyName("text")] string Text
);

public sealed record DidOpenTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentItem TextDocument
);

public sealed record DidChangeTextDocumentParams(
    [property: JsonPropertyName("textDocument")] VersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("contentChanges")] List<TextDocumentContentChangeEvent> ContentChanges
);

public sealed record DidCloseTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument
);

public sealed record DocumentSymbolParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument
);

public sealed record TextDocumentPositionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position
);

public sealed record HoverParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position
);

public sealed record MarkupContent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value
);

public sealed record Hover(
    [property: JsonPropertyName("contents")] MarkupContent Contents,
    [property: JsonPropertyName("range")] Range? Range = null
);

public sealed record CompletionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position
);

public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

public sealed record CompletionItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] CompletionItemKind Kind,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("documentation")] MarkupContent? Documentation = null,
    [property: JsonPropertyName("insertText")] string? InsertText = null
);

public sealed record CompletionList(
    [property: JsonPropertyName("isIncomplete")] bool IsIncomplete,
    [property: JsonPropertyName("items")] List<CompletionItem> Items
);

public sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version
);

public sealed record ServerCapabilities(
    [property: JsonPropertyName("textDocumentSync")] int TextDocumentSync,
    [property: JsonPropertyName("hoverProvider")] bool HoverProvider,
    [property: JsonPropertyName("completionProvider")] CompletionOptions? CompletionProvider,
    [property: JsonPropertyName("definitionProvider")] bool DefinitionProvider,
    [property: JsonPropertyName("referencesProvider")] bool ReferencesProvider,
    [property: JsonPropertyName("documentSymbolProvider")] bool DocumentSymbolProvider
);

public sealed record CompletionOptions(
    [property: JsonPropertyName("resolveProvider")] bool ResolveProvider = false,
    [property: JsonPropertyName("triggerCharacters")] string[]? TriggerCharacters = null
);

public sealed record InitializeResult(
    [property: JsonPropertyName("capabilities")] ServerCapabilities Capabilities,
    [property: JsonPropertyName("serverInfo")] ServerInfo ServerInfo
);

public sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] object? Id,
    [property: JsonPropertyName("method")] string Method
);

public sealed record JsonRpcNotification(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method
);
