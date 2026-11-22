using System.Text.Json;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;
using UnityCSharpFastLSP.Workspace;

namespace UnityCSharpFastLSP.Server;

public sealed class LspServer
{
    private readonly ServerState _state;
    private readonly LspMessageWriter _writer;
    private readonly bool _isProxy;
    private bool _isShutdown;
    private bool _isInitialized;

    // 定義ジャンプ系の要求は、ワークスペースのインデックス完了を待ってから解決する。
    // 大規模プロジェクトでは初回インデックスに時間がかかるため、上限付きで待つ。
    private static readonly TimeSpan IndexWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>独自拡張: guid（またはアセット）の .meta とアセット本体を返す。</summary>
    public const string GuidDefinitionMethod = "unitylsp/guidDefinition";

    /// <summary>独自拡張: guid を参照しているシーン・プレハブ等の位置を返す。</summary>
    public const string GuidReferencesMethod = "unitylsp/guidReferences";

    public LspServer(Stream output, bool isProxy = false)
    {
        _writer = new LspMessageWriter(output);
        _state = new ServerState(_writer);
        _isProxy = isProxy;
    }

    public ServerState State => _state;

    /// <summary>
    /// 以降の応答をクライアントへ書かずに捕捉する。プロキシが FastLSP の結果を検査し、
    /// 空なら Upstream へ委譲するために使う。
    /// </summary>
    public void BeginCapture() => _writer.BeginCapture();

    /// <summary>捕捉を終了し、捕捉したメッセージ本文を返す。</summary>
    public List<byte[]> EndCapture() => _writer.EndCapture();

    public bool HandleMessage(byte[] message)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            // 仕様: パースに失敗した場合は id = null で -32700 を返す
            _writer.WriteError(null, JsonRpcErrorCode.ParseError, "Parse error");
            return true;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _writer.WriteError(null, JsonRpcErrorCode.InvalidRequest, "Invalid Request");
                return true;
            }

            return Dispatch(root);
        }
    }

    private bool Dispatch(JsonElement root)
    {
        string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        bool hasId = root.TryGetProperty("id", out var idElem);
        object? id = hasId ? GetIdValue(idElem) : null;
        JsonElement parameters = root.TryGetProperty("params", out var p) ? p : default;

        // 仕様: initialize 以外のリクエストは初期化前なら -32002、shutdown 後なら -32600 を返す
        // (shutdown は初期化前でも受け付ける。通知には応答しないので対象外)
        if (hasId && method is not ("initialize" or "shutdown"))
        {
            if (!_isInitialized)
            {
                _writer.WriteError(id, JsonRpcErrorCode.ServerNotInitialized, "Server not initialized");
                return true;
            }

            if (_isShutdown)
            {
                _writer.WriteError(id, JsonRpcErrorCode.InvalidRequest, "Invalid request: server is shut down");
                return true;
            }
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    HandleInitialize(id, parameters);
                    _isInitialized = true;
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

                case GuidDefinitionMethod:
                    HandleGuidDefinition(id, parameters);
                    break;

                case GuidReferencesMethod:
                    HandleGuidReferences(id, parameters);
                    break;

                default:
                    // 仕様: 未実装のリクエストは -32601。通知（id なし）には応答しない。
                    if (hasId)
                    {
                        _writer.WriteError(id, JsonRpcErrorCode.MethodNotFound, $"Method not found: {method}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error handling method {method}: {ex}");
            if (hasId)
            {
                _writer.WriteError(id, JsonRpcErrorCode.InternalError, "Internal error");
            }
        }

        return true;
    }

    private void HandleInitialize(object? id, JsonElement parameters)
    {
        string rootPath = "";
        if (parameters.TryGetProperty("workspaceFolders", out var wfElem) && wfElem.ValueKind == JsonValueKind.Array && wfElem.GetArrayLength() > 0)
        {
            var firstFolder = wfElem[0];
            if (firstFolder.TryGetProperty("uri", out var uriElem) && uriElem.GetString() is string wfUri)
            {
                if (Uri.TryCreate(wfUri, UriKind.Absolute, out var uriObj) && uriObj.IsFile)
                {
                    rootPath = uriObj.LocalPath;
                }
            }
        }
        else if (parameters.TryGetProperty("rootUri", out var rootUriElem) && rootUriElem.GetString() is string rootUri)
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
        else
        {
            // インデックス対象が無いので、待機している定義要求をすぐ解放する
            _state.WorkspaceManager.MarkIndexReady();
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
                WorkspaceSymbolProvider: true,
                RenameProvider: _isProxy ? true : null,
                CodeActionProvider: _isProxy ? true : null,
                Experimental: new ServerExperimentalCapabilities(
                    new UnityLspCapabilities([GuidDefinitionMethod, GuidReferencesMethod]))
            ),
            ServerInfo: new ServerInfo("UnityCSharpFastLSP", "0.3.1")
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
        _state.WorkspaceManager.WaitForIndex(IndexWaitTimeout);
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
        _state.WorkspaceManager.WaitForIndex(IndexWaitTimeout);
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
        _state.WorkspaceManager.WaitForIndex(IndexWaitTimeout);
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
        _state.WorkspaceManager.WaitForIndex(IndexWaitTimeout);
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
        _state.WorkspaceManager.WaitForIndex(IndexWaitTimeout);
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

    /// <summary>
    /// guid、または asset の .meta から guid を求める。UnityCSharpLSP と同じく、
    /// 引数が無い・不正・解決できない場合はエラーにせず null（応答も null）とする。
    /// FastLSP の GUID 機能が止まっている（プロキシで upstream が担当）場合は MethodNotFound を返して false。
    /// </summary>
    private bool TryResolveGuidQuery(object? id, JsonElement parameters, out string? guid, out GuidQueryParams? query)
    {
        guid = null;
        query = null;
        if (!_state.WorkspaceManager.GuidIndex.Enabled)
        {
            _writer.WriteError(id, JsonRpcErrorCode.MethodNotFound, "Unity GUID methods are served by the upstream LSP server.");
            return false;
        }

        try
        {
            query = parameters.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize(parameters, LspJsonSerializerContext.Default.GuidQueryParams)
                : null;
        }
        catch (JsonException)
        {
            query = null;
        }

        if (query != null && (!string.IsNullOrWhiteSpace(query.Guid) || !string.IsNullOrWhiteSpace(query.Asset)))
        {
            _state.WorkspaceManager.WaitForIndex(IndexWaitTimeout);
            guid = _state.WorkspaceManager.GuidIndex.ResolveGuid(query.Guid, query.Asset);
        }
        return true;
    }

    private void HandleGuidDefinition(object? id, JsonElement parameters)
    {
        if (!TryResolveGuidQuery(id, parameters, out string? guid, out _)) return;

        GuidDefinitionResult? result = guid == null ? null : _state.WorkspaceManager.GuidIndex.FindDefinition(guid);
        if (result != null)
        {
            _writer.WriteResponse(id, result, LspJsonSerializerContext.Default.GuidDefinitionResult);
            return;
        }

        _writer.WriteRawResponse(id, "null"u8.ToArray());
    }

    private void HandleGuidReferences(object? id, JsonElement parameters)
    {
        if (!TryResolveGuidQuery(id, parameters, out string? guid, out GuidQueryParams? query)) return;

        if (guid == null)
        {
            _writer.WriteRawResponse(id, "null"u8.ToArray());
            return;
        }

        GuidReferencesResult result = _state.WorkspaceManager.GuidIndex.FindReferences(
            guid, query!.MaxResults, query.IncludePatterns, query.ExcludePatterns);
        _writer.WriteResponse(id, result, LspJsonSerializerContext.Default.GuidReferencesResult);
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
