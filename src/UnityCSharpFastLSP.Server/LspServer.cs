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

                case "textDocument/formatting":
                    HandleFormatting(id, parameters);
                    break;

                case "textDocument/definition":
                    HandleDefinition(id, parameters);
                    break;

                case "textDocument/declaration":
                    HandleDeclaration(id, parameters);
                    break;

                case "textDocument/implementation":
                    HandleImplementation(id, parameters);
                    break;

                case "textDocument/typeDefinition":
                    HandleTypeDefinition(id, parameters);
                    break;

                case "textDocument/references":
                    HandleReferences(id, parameters);
                    break;

                case "textDocument/signatureHelp":
                    HandleSignatureHelp(id, parameters);
                    break;

                case "textDocument/semanticTokens/full":
                    HandleSemanticTokens(id, parameters);
                    break;

                case "textDocument/documentHighlight":
                    HandleDocumentHighlight(id, parameters);
                    break;

                case "workspace/symbol":
                    HandleWorkspaceSymbol(id, parameters);
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
                DocumentSymbolProvider: true,
                DocumentFormattingProvider: true,
                SignatureHelpProvider: new SignatureHelpOptions(["(", ","]),
                SemanticTokensProvider: new SemanticTokensOptions(SemanticTokensProvider.Legend, Full: true),
                DocumentHighlightProvider: true,
                WorkspaceSymbolProvider: true
            ),
            ServerInfo: new ServerInfo("UnityCSharpFastLSP", "0.2.0")
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
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
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
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
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

    private void HandleFormatting(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.DocumentFormattingParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var edits = FastFormatter.FormatDocument(snapshot, @params.Options);
                _writer.WriteResponse(id, edits, LspJsonSerializerContext.Default.ListTextEdit);
                return;
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleDefinition(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.TextDocumentPositionParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var locations = ScopedResolver.ResolveScopedDefinition(model, @params.TextDocument.Uri, @params.Position);
                if (locations.Count > 0)
                {
                    _writer.WriteResponse(id, locations, LspJsonSerializerContext.Default.ListLocation);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleDeclaration(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.DeclarationParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var locations = ScopedResolver.ResolveDeclaration(model, @params.TextDocument.Uri, @params.Position);
                if (locations.Count > 0)
                {
                    _writer.WriteResponse(id, locations, LspJsonSerializerContext.Default.ListLocation);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleImplementation(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.ImplementationParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var locations = ScopedResolver.ResolveImplementation(model, @params.TextDocument.Uri, @params.Position);
                if (locations.Count > 0)
                {
                    _writer.WriteResponse(id, locations, LspJsonSerializerContext.Default.ListLocation);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleTypeDefinition(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.TypeDefinitionParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var locations = ScopedResolver.ResolveTypeDefinition(model, @params.TextDocument.Uri, @params.Position);
                if (locations.Count > 0)
                {
                    _writer.WriteResponse(id, locations, LspJsonSerializerContext.Default.ListLocation);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleReferences(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.ReferenceParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                bool includeDecl = @params.Context?.IncludeDeclaration ?? true;
                var locations = ScopedResolver.ResolveReferences(model, @params.TextDocument.Uri, @params.Position, includeDecl);
                _writer.WriteResponse(id, locations, LspJsonSerializerContext.Default.ListLocation);
                return;
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleSignatureHelp(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.SignatureHelpParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var help = SignatureHelpProvider.GetSignatureHelp(model, @params.Position);
                if (help != null)
                {
                    _writer.WriteResponse(id, help, LspJsonSerializerContext.Default.SignatureHelp);
                    return;
                }
            }
        }

        _writer.WriteRawResponse(id, "null"u8.ToArray());
    }

    private void HandleSemanticTokens(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.SemanticTokensParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var tokens = SemanticTokensProvider.GetSemanticTokens(snapshot);
                _writer.WriteResponse(id, tokens, LspJsonSerializerContext.Default.SemanticTokens);
                return;
            }
        }

        _writer.WriteRawResponse(id, "{\"data\":[]}"u8.ToArray());
    }

    private void HandleDocumentHighlight(object? id, JsonElement parameters)
    {
        var @params = JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.TextDocumentPositionParams);
        if (@params != null)
        {
            var snapshot = _state.GetOrLoadDocument(@params.TextDocument.Uri);
            if (snapshot != null)
            {
                var model = new DocumentSemanticModel(snapshot, _state.SymbolIndex);
                var highlights = DocumentHighlightProvider.GetHighlights(model, @params.Position);
                _writer.WriteResponse(id, highlights, LspJsonSerializerContext.Default.ListDocumentHighlight);
                return;
            }
        }

        _writer.WriteRawResponse(id, "[]"u8.ToArray());
    }

    private void HandleWorkspaceSymbol(object? id, JsonElement parameters)
    {
        string query = "";
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("query", out var queryElem) &&
            queryElem.GetString() is string q)
        {
            query = q;
        }

        var symbols = _state.SymbolIndex.FindWorkspaceSymbols(query);
        _writer.WriteResponse(id, symbols, LspJsonSerializerContext.Default.ListSymbolInformation);
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
