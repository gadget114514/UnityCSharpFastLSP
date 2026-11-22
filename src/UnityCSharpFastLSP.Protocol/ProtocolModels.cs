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
    [property: JsonPropertyName("hoverProvider")] bool HoverProvider = true,
    [property: JsonPropertyName("completionProvider")] CompletionOptions? CompletionProvider = null,
    [property: JsonPropertyName("definitionProvider")] bool? DefinitionProvider = true,
    [property: JsonPropertyName("declarationProvider")] bool DeclarationProvider = true,
    [property: JsonPropertyName("implementationProvider")] bool ImplementationProvider = true,
    [property: JsonPropertyName("typeDefinitionProvider")] bool TypeDefinitionProvider = true,
    [property: JsonPropertyName("referencesProvider")] bool ReferencesProvider = true,
    [property: JsonPropertyName("documentSymbolProvider")] bool DocumentSymbolProvider = true,
    [property: JsonPropertyName("documentFormattingProvider")] bool DocumentFormattingProvider = true,
    [property: JsonPropertyName("signatureHelpProvider")] SignatureHelpOptions? SignatureHelpProvider = null,
    [property: JsonPropertyName("semanticTokensProvider")] SemanticTokensOptions? SemanticTokensProvider = null,
    [property: JsonPropertyName("documentHighlightProvider")] bool DocumentHighlightProvider = true,
    [property: JsonPropertyName("workspaceSymbolProvider")] bool WorkspaceSymbolProvider = true,
    [property: JsonPropertyName("renameProvider")] bool? RenameProvider = null,
    [property: JsonPropertyName("codeActionProvider")] bool? CodeActionProvider = null,
    [property: JsonPropertyName("experimental")] ServerExperimentalCapabilities? Experimental = null
);

/// <summary>capabilities.experimental。独自拡張の advertise に使う。</summary>
public sealed record ServerExperimentalCapabilities(
    [property: JsonPropertyName("unitylsp")] UnityLspCapabilities? Unitylsp = null
);

/// <summary>
/// capabilities.experimental.unitylsp。対応している <c>unitylsp/*</c> メソッド名の一覧。
/// UnityCSharpLSP など upstream も同じ形で advertise する（プロキシはこれで振り分けを決める）。
/// </summary>
public sealed record UnityLspCapabilities(
    [property: JsonPropertyName("methods")] List<string> Methods
);

public sealed record ReferenceContext(
    [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration = true
);

public sealed record ReferenceParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position,
    [property: JsonPropertyName("context")] ReferenceContext Context = default!
);

public sealed record DeclarationParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position
);

public sealed record ImplementationParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position
);

public sealed record TypeDefinitionParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position
);

public sealed record SignatureHelpOptions(
    [property: JsonPropertyName("triggerCharacters")] string[] TriggerCharacters
);

public sealed record SignatureHelpParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("position")] Position Position
);

public sealed record ParameterInformation(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("documentation")] MarkupContent? Documentation = null
);

public sealed record SignatureInformation(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("documentation")] MarkupContent? Documentation = null,
    [property: JsonPropertyName("parameters")] IReadOnlyList<ParameterInformation>? Parameters = null
);

public sealed record SignatureHelp(
    [property: JsonPropertyName("signatures")] IReadOnlyList<SignatureInformation> Signatures,
    [property: JsonPropertyName("activeSignature")] int ActiveSignature = 0,
    [property: JsonPropertyName("activeParameter")] int ActiveParameter = 0
);

public sealed record SemanticTokensLegend(
    [property: JsonPropertyName("tokenTypes")] string[] TokenTypes,
    [property: JsonPropertyName("tokenModifiers")] string[] TokenModifiers
);

public sealed record SemanticTokensOptions(
    [property: JsonPropertyName("legend")] SemanticTokensLegend Legend,
    [property: JsonPropertyName("full")] bool Full = true
);

public sealed record SemanticTokensParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument
);

public sealed record SemanticTokens(
    [property: JsonPropertyName("resultId")] string? ResultId,
    [property: JsonPropertyName("data")] IReadOnlyList<int> Data
);

public enum DocumentHighlightKind
{
    Text = 1,
    Read = 2,
    Write = 3
}

public sealed record DocumentHighlight(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("kind")] DocumentHighlightKind Kind = DocumentHighlightKind.Text
);

public readonly record struct TextEdit(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("newText")] string NewText
);

public sealed record FormattingOptions(
    [property: JsonPropertyName("tabSize")] int TabSize,
    [property: JsonPropertyName("insertSpaces")] bool InsertSpaces
);

public sealed record DocumentFormattingParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("options")] FormattingOptions Options
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

public sealed record WorkspaceSymbolParams(
    [property: JsonPropertyName("query")] string Query
);

public sealed record SymbolInformation(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] SymbolKind Kind,
    [property: JsonPropertyName("location")] Location Location,
    [property: JsonPropertyName("containerName")] string? ContainerName = null
);

public static class UriHelper
{
    public static string Normalize(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            return parsed.AbsoluteUri;
        }
        return uri;
    }
}

// ---- Unity GUID 拡張（unitylsp/guidDefinition, unitylsp/guidReferences） ----

/// <summary>
/// guid（32 桁 16 進）か asset（file URI / 絶対パス）のどちらかを指定する。UnityCSharpLSP と同じ形。
/// includePatterns / excludePatterns は guidReferences のみ: プロジェクトルート相対パスに対する glob。
/// includePatterns を指定すると既定のテキストアセット拡張子の代わりに使う（*.cs なども走査できる）。
/// excludePatterns に一致するファイルは常に除く。
/// </summary>
public sealed record GuidQueryParams(
    [property: JsonPropertyName("guid")] string? Guid = null,
    [property: JsonPropertyName("asset")] string? Asset = null,
    [property: JsonPropertyName("maxResults")] int? MaxResults = null,
    [property: JsonPropertyName("includePatterns")] string[]? IncludePatterns = null,
    [property: JsonPropertyName("excludePatterns")] string[]? ExcludePatterns = null
);

public sealed record GuidDefinitionResult(
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("assetUri")] string AssetUri,
    [property: JsonPropertyName("meta")] Location Meta
);

public sealed record GuidReference(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("lineText")] string LineText,
    [property: JsonPropertyName("classId")] int? ClassId = null,
    [property: JsonPropertyName("objectFileId")] long? ObjectFileId = null,
    [property: JsonPropertyName("fileId")] long? FileId = null
);

public sealed record GuidReferencesResult(
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("assetUri")] string? AssetUri,
    [property: JsonPropertyName("references")] List<GuidReference> References,
    [property: JsonPropertyName("truncated")] bool Truncated
);
