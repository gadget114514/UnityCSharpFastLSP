using System.Text.Json;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;

namespace UnityCSharpFastLSP.Server;

public sealed class LspServer
{
    private readonly ServerState _state;
    private readonly LspMessageWriter _writer;
    private bool _isShutdown;

    public LspServer(Stream output)
    {
        _writer = new LspMessageWriter(output);
        _state = new ServerState(_writer);
    }

    public bool HandleMessage(byte[] message)
    {
        using JsonDocument doc = JsonDocument.Parse(message);
        JsonElement root = doc.RootElement;

        string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        bool hasId = root.TryGetProperty("id", out var idElem);
        object? id = hasId ? GetIdValue(idElem) : null;
        JsonElement parameters = root.TryGetProperty("params", out var p) ? p : default;

        try
        {
            switch (method)
            {
                case "initialize":
                    HandleInitialize(id, parameters);
                    break;

                case "initialized":
                    break;

                case "shutdown":
                    _isShutdown = true;
                    _writer.WriteRawResponse(id, "null"u8.ToArray());
                    break;

                case "exit":
                    return !_isShutdown ? false : false;

                case "textDocument/didOpen":
                    HandleDidOpen(parameters);
                    break;

                case "textDocument/didChange":
                    HandleDidChange(parameters);
                    break;

                case "textDocument/didClose":
                    HandleDidClose(parameters);
                    break;

                case "textDocument/documentSymbol":
                    HandleDocumentSymbol(id, parameters);
                    break;

                case "textDocument/hover":
                    HandleHover(id, parameters);
                    break;

                case "textDocument/completion":
                    HandleCompletion(id, parameters);
                    break;

                case "textDocument/definition":
                    HandleDefinition(id, parameters);
                    break;

                case "textDocument/references":
                    HandleReferences(id, parameters);
                    break;

                default:
                    if (hasId)
                    {
                        _writer.WriteRawResponse(id, "null"u8.ToArray());
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error handling method {method}: {ex}");
            if (hasId)
            {
                _writer.WriteRawResponse(id, "null"u8.ToArray());
            }
        }

        return true;
    }

    private void HandleInitialize(object? id, JsonElement parameters)
    {
        string rootPath = "";
        if (parameters.TryGetProperty("rootUri", out var rootUriElem) && rootUriElem.GetString() is string rootUri)
        {
            if (Uri.TryCreate(rootUri, UriKind.Absolute, out var uriObj) && uriObj.IsFile)
            {
                rootPath = uriObj.LocalPath;
            }
        }
        else if (parameters.TryGetProperty("rootPath", out var rootPathElem))
        {
            rootPath = rootPathElem.GetString() ?? "";
        }

        if (!string.IsNullOrEmpty(rootPath))
        {
            Task.Run(() => _state.WorkspaceManager.IndexWorkspace(rootPath));
        }

        var result = new InitializeResult(
            Capabilities: new ServerCapabilities(
                TextDocumentSync: 2, // Incremental
                HoverProvider: true,
                CompletionProvider: new CompletionOptions(ResolveProvider: false, TriggerCharacters: [".", " "]),
                DefinitionProvider: true,
                ReferencesProvider: true,
                DocumentSymbolProvider: true
            ),
            ServerInfo: new ServerInfo("UnityCSharpFastLSP", "1.0.0-net11")
        );

        _writer.WriteResponse(id, result, LspJsonSerializerContext.Default.InitializeResult);
    }

    private void HandleDidOpen(JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.DidOpenTextDocumentParams);
        if (@params != null)
        {
            _state.OpenDocument(@params.TextDocument.Uri, @params.TextDocument.Text, @params.TextDocument.Version);
        }
    }

    private void HandleDidChange(JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.DidChangeTextDocumentParams);
        if (@params != null)
        {
            _state.ChangeDocument(@params.TextDocument.Uri, @params.TextDocument.Version, @params.ContentChanges);
        }
    }

    private void HandleDidClose(JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.DidCloseTextDocumentParams);
        if (@params != null)
        {
            _state.CloseDocument(@params.TextDocument.Uri);
        }
    }

    private void HandleDocumentSymbol(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.DocumentSymbolParams);
        if (@params != null)
        {
            var snapshot = _state.GetDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var symbols = DocumentSymbolProvider.GetDocumentSymbols(snapshot);
                _writer.WriteResponse(id, symbols, LspJsonSerializerContext.Default.ListDocumentSymbol);
                return;
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleHover(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.HoverParams);
        if (@params != null)
        {
            var snapshot = _state.GetDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var hover = HoverProvider.GetHover(model, @params.Position);
                if (hover != null)
                {
                    _writer.WriteResponse(id, hover, LspJsonSerializerContext.Default.Hover);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "null"u8.ToArray());
    }

    private void HandleCompletion(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.CompletionParams);
        if (@params != null)
        {
            var snapshot = _state.GetDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var list = CompletionProvider.GetCompletions(model, @params.Position);
                _writer.WriteResponse(id, list, LspJsonSerializerContext.Default.CompletionList);
                return;
            }
        }

        _writer.WriteRawResponse(id, "{\"isIncomplete\":false,\"items\":[]}"u8.ToArray());
    }

    private void HandleDefinition(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.TextDocumentPositionParams);
        if (@params != null)
        {
            var snapshot = _state.GetDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                string? word = model.GetWordAtPosition(@params.Position);
                if (!string.IsNullOrEmpty(word))
                {
                    var locations = _state.SymbolIndex.FindDefinitions(word);
                    _writer.WriteResponse(id, locations, LspJsonSerializerContext.Default.ListLocation);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleReferences(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.TextDocumentPositionParams);
        if (@params != null)
        {
            var snapshot = _state.GetDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                string? word = model.GetWordAtPosition(@params.Position);
                if (!string.IsNullOrEmpty(word))
                {
                    var locations = _state.SymbolIndex.FindReferences(word);
                    _writer.WriteResponse(id, locations, LspJsonSerializerContext.Default.ListLocation);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private static object? GetIdValue(JsonElement idElem)
    {
        return idElem.ValueKind switch
        {
            JsonValueKind.Number when idElem.TryGetInt64(out long l) => l,
            JsonValueKind.String => idElem.GetString(),
            _ => null,
        };
    }
}
