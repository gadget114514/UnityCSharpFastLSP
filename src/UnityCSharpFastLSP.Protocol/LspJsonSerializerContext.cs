using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityCSharpFastLSP.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(Range))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(List<Location>))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(List<Diagnostic>))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(DocumentSymbol))]
[JsonSerializable(typeof(List<DocumentSymbol>))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(DocumentSymbolParams))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(HoverParams))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(TextEdit))]
[JsonSerializable(typeof(List<TextEdit>))]
[JsonSerializable(typeof(DocumentFormattingParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(CompletionOptions))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(SignatureHelpParams))]
[JsonSerializable(typeof(SignatureHelpOptions))]
[JsonSerializable(typeof(SignatureInformation))]
[JsonSerializable(typeof(ParameterInformation))]
[JsonSerializable(typeof(SemanticTokens))]
[JsonSerializable(typeof(SemanticTokensParams))]
[JsonSerializable(typeof(SemanticTokensOptions))]
[JsonSerializable(typeof(SemanticTokensLegend))]
[JsonSerializable(typeof(DocumentHighlight))]
[JsonSerializable(typeof(List<DocumentHighlight>))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(ReferenceContext))]
[JsonSerializable(typeof(DeclarationParams))]
[JsonSerializable(typeof(ImplementationParams))]
[JsonSerializable(typeof(TypeDefinitionParams))]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(WorkspaceSymbolParams))]
[JsonSerializable(typeof(SymbolInformation))]
[JsonSerializable(typeof(List<SymbolInformation>))]
[JsonSerializable(typeof(GuidQueryParams))]
[JsonSerializable(typeof(GuidDefinitionResult))]
[JsonSerializable(typeof(GuidReferencesResult))]
public partial class LspJsonSerializerContext : JsonSerializerContext
{
}
