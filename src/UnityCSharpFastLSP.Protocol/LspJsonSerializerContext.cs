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
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(CompletionOptions))]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcNotification))]
public partial class LspJsonSerializerContext : JsonSerializerContext
{
}
