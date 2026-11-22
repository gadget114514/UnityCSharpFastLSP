using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Workspace;

namespace UnityCSharpFastLSP.Server.Mcp;

/// <summary>MCP サーバーの起動オプション。</summary>
public sealed record McpServerOptions(
    string WorkspaceRoot,        // 正規化済み絶対パス
    bool Proxy,
    string? UpstreamPath,
    JsonNode? Settings,          // --settings の JSON（null 可）
    TimeSpan Timeout,            // 既定 120 秒
    UpstreamKind UpstreamKind = UpstreamKind.Auto,
    string? UpstreamWorkspace = null); // upstream の作業ディレクトリ（既定: MCP の workspace root）

/// <summary>
/// MCP クライアント（Claude Code 等）と <see cref="LspLoopbackClient"/> の間に立つ stdio サーバー。
/// tools/list / tools/call を <see cref="McpToolTable"/> に従って LSP リクエストへ変換し、
/// LSP クライアントの責務（initialize、ドキュメント同期、URI 正規化）を肩代わりする。
/// stdout は MCP 専用（改行区切り JSON-RPC）。ログはすべて stderr。
/// </summary>
public sealed class McpServer : IDisposable
{
    private const string ServerName = "unity-csharp-fast-lsp";
    private const string ServerVersion = "0.3.1";
    private const string DefaultProtocolVersion = "2025-06-18";
    private const string StandaloneHint =
        " (not supported by UnityCSharpFastLSP standalone; run with --mcp --proxy to fall back to an upstream LSP server)";

    private static readonly string[] SupportedProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18"];
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly McpServerOptions _options;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly LspLoopbackClient _client;
    private readonly DocumentSyncTracker _sync;
    private readonly object _outputGate = new();
    private readonly object _inflightGate = new();
    private readonly List<Task> _inflight = new();

    /// <param name="options">起動オプション。</param>
    /// <param name="input">MCP の入力ストリーム（UTF-8, 1 行 1 メッセージ）。</param>
    /// <param name="output">MCP の出力ストリーム。</param>
    /// <param name="client">テスト注入用。null なら <see cref="McpServerOptions.Proxy"/> に応じて生成する。</param>
    public McpServer(McpServerOptions options, Stream input, Stream output, LspLoopbackClient? client = null)
    {
        _options = options;
        _input = input;
        _output = output;
        _client = client
            ?? (options.Proxy
                ? LspLoopbackClient.CreateProxy(
                    options.UpstreamPath,
                    options.UpstreamKind,
                    options.UpstreamWorkspace ?? options.WorkspaceRoot)
                : LspLoopbackClient.CreateStandalone());
        _sync = new DocumentSyncTracker(_client, _client.IsProxy);
    }

    /// <summary>
    /// workspace を起点に、Assets/ か ProjectSettings/ を持つ最も近い祖先を返す。無ければ startDir の絶対パス。
    /// </summary>
    public static string FindWorkspaceRoot(string startDir)
    {
        string full = Path.GetFullPath(startDir);
        for (DirectoryInfo? dir = new(full); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Assets"))
                || Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings")))
            {
                return dir.FullName;
            }
        }
        return full;
    }

    /// <summary>
    /// 起動時に LSP を初期化してから MCP メッセージを処理する。入力 EOF（または ct キャンセル）で
    /// 実行中の tools/call の完了を待ち、shutdown / exit を送って 0 を返す。
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        try
        {
            await InitializeLspAsync(ct).ConfigureAwait(false);

            using var reader = new StreamReader(_input, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    break;
                }

                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                HandleLine(line, ct);
            }

            await WaitForInflightAsync().ConfigureAwait(false);
            await ShutdownLspAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            _client.Dispose();
        }
    }

    public void Dispose() => _client.Dispose();

    // ---------- LSP 初期化 / 終了 ----------

    private async Task InitializeLspAsync(CancellationToken ct)
    {
        try
        {
            LspResult res = await _client.RequestAsync("initialize", BuildInitializeParams(), _options.Timeout, ct).ConfigureAwait(false);
            if (res.Error != null)
            {
                Console.Error.WriteLine($"[MCP] initialize failed: {res.Error.Code} {res.Error.Message}");
                return;
            }

            _client.Notify("initialized", new JsonObject());
            if (_options.Settings != null)
            {
                _client.Notify("workspace/didChangeConfiguration", new JsonObject
                {
                    ["settings"] = _options.Settings.DeepClone(),
                });
            }
        }
        catch (Exception ex)
        {
            // MCP 自体は起動し続ける（ツール呼び出し時に LSP エラーとして返る）
            Console.Error.WriteLine($"[MCP] initialize error: {ex.Message}");
        }
    }

    private async Task ShutdownLspAsync()
    {
        try
        {
            await _client.RequestAsync("shutdown", null, ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            _client.Notify("exit", null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] shutdown error: {ex.Message}");
        }
    }

    private JsonObject BuildInitializeParams()
    {
        string root = _options.WorkspaceRoot;
        string rootUri = WorkspaceManager.PathToFileUri(root);
        string name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = root;

        return new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = rootUri,
            ["rootPath"] = root,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = name }),
            ["clientInfo"] = new JsonObject { ["name"] = "UnityCSharpFastLSP-MCP" },
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                    ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                    ["rename"] = new JsonObject { ["prepareSupport"] = true },
                    ["codeAction"] = new JsonObject
                    {
                        ["codeActionLiteralSupport"] = new JsonObject
                        {
                            ["codeActionKind"] = new JsonObject
                            {
                                ["valueSet"] = new JsonArray(
                                    "", "quickfix", "refactor", "refactor.extract", "refactor.inline",
                                    "refactor.rewrite", "source", "source.organizeImports"),
                            },
                        },
                    },
                    ["callHierarchy"] = new JsonObject { ["dynamicRegistration"] = false },
                    ["typeHierarchy"] = new JsonObject { ["dynamicRegistration"] = false },
                    ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false },
                    ["inlayHint"] = new JsonObject { ["dynamicRegistration"] = false },
                },
                ["workspace"] = new JsonObject
                {
                    ["workspaceFolders"] = true,
                    ["configuration"] = true,
                    ["symbol"] = new JsonObject(),
                    ["executeCommand"] = new JsonObject(),
                    ["diagnostics"] = new JsonObject(),
                },
            },
        };
    }

    // ---------- MCP プロトコル ----------

    private void HandleLine(string line, CancellationToken ct)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            WriteError(null, JsonRpcErrorCode.ParseError, "Parse error");
            return;
        }

        if (node is not JsonObject msg)
        {
            WriteError(null, JsonRpcErrorCode.InvalidRequest, "Invalid Request");
            return;
        }

        string? method = GetString(msg["method"]);
        if (method == null) return; // クライアントからの応答など。無視する。

        // 通知（id なし）は無視（notifications/initialized, notifications/cancelled 等）
        if (!msg.ContainsKey("id")) return;

        JsonNode? id = msg["id"]?.DeepClone();
        JsonNode? parameters = msg["params"];

        switch (method)
        {
            case "initialize":
                WriteResult(id, BuildInitializeResult(parameters));
                break;

            case "ping":
                WriteResult(id, new JsonObject());
                break;

            case "tools/list":
                var tools = new JsonArray();
                foreach (McpTool tool in McpToolTable.All)
                {
                    tools.Add(McpToolTable.ToListEntry(tool));
                }
                WriteResult(id, new JsonObject { ["tools"] = tools });
                break;

            case "tools/call":
                StartToolCall(id, parameters, ct);
                break;

            default:
                WriteError(id, JsonRpcErrorCode.MethodNotFound, $"Method not found: {method}");
                break;
        }
    }

    private JsonObject BuildInitializeResult(JsonNode? parameters)
    {
        string? requested = GetString(parameters?["protocolVersion"]);
        string protocolVersion = requested != null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : DefaultProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
            ["instructions"] =
                "UnityCSharpLSP tools: each tool maps 1:1 to an LSP method (name uses '_' for '/'). " +
                "Arguments are the LSP params object; results are the LSP result JSON. " +
                "Positions are 0-based (line, UTF-16 character). " +
                "textDocument.uri may be a file:/// URI, an absolute path, or a workspace-relative path. " +
                "Standalone mode answers only the fast LSP methods; run with --mcp --proxy (optionally --upstream-kind) " +
                "to fall back to an upstream LSP server " +
                "for rename, codeAction and other heavy methods. " +
                "unity_guid_definition / unity_guid_references resolve Unity asset GUIDs (.meta) and find the scenes, " +
                "prefabs and other assets that reference a GUID or an asset path (e.g. a MonoBehaviour script).",
        };
    }

    /// <summary>tools/call は並行処理する（読み取りループを塞がない）。</summary>
    private void StartToolCall(JsonNode? id, JsonNode? parameters, CancellationToken ct)
    {
        string? name = GetString(parameters?["name"]);
        if (name == null)
        {
            WriteError(id, JsonRpcErrorCode.InvalidParams, "Invalid params: 'name' is required.");
            return;
        }

        McpTool? tool = McpToolTable.Find(name);
        if (tool == null)
        {
            WriteError(id, JsonRpcErrorCode.InvalidParams, $"Unknown tool: {name}");
            return;
        }

        JsonNode? argumentsNode = parameters?["arguments"];
        JsonObject arguments;
        if (argumentsNode == null)
        {
            arguments = new JsonObject();
        }
        else if (argumentsNode is JsonObject argumentsObject)
        {
            arguments = (JsonObject)argumentsObject.DeepClone();
        }
        else
        {
            WriteError(id, JsonRpcErrorCode.InvalidParams, "Invalid params: 'arguments' must be an object.");
            return;
        }

        Task task = Task.Run(async () =>
        {
            LspResult outcome;
            try
            {
                outcome = await ExecuteToolAsync(tool, arguments, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 予期しない例外は isError で返し、MCP セッションは継続する
                outcome = new LspResult(null, new LspError(JsonRpcErrorCode.InternalError, ex.Message));
            }

            WriteResult(id, BuildToolResult(outcome));
        });

        lock (_inflightGate)
        {
            _inflight.RemoveAll(t => t.IsCompleted);
            _inflight.Add(task);
        }
    }

    private async Task WaitForInflightAsync()
    {
        Task[] pending;
        lock (_inflightGate)
        {
            pending = _inflight.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 個々の失敗は StartToolCall 内で応答済み。ここでは待機だけが目的。
            Console.Error.WriteLine($"[MCP] tool call ended with error: {ex.Message}");
        }
    }

    // ---------- tools/call 本体 ----------

    private async Task<LspResult> ExecuteToolAsync(McpTool tool, JsonObject arguments, CancellationToken ct)
    {
        switch (tool.Kind)
        {
            case McpToolKind.LspRequest:
                return await CallLspRequestAsync(tool.LspMethod!, arguments, ct).ConfigureAwait(false);

            case McpToolKind.LspNotification:
                PrepareDocument(arguments);
                _client.Notify(tool.LspMethod!, arguments);
                return new LspResult(null, null);

            case McpToolKind.Reset:
                return await ResetAsync(ct).ConfigureAwait(false);

            case McpToolKind.DiagnosticsCached:
                return DiagnosticsCached(arguments);

            default:
                return new LspResult(null, new LspError(JsonRpcErrorCode.InternalError, $"Unsupported tool kind: {tool.Kind}"));
        }
    }

    private async Task<LspResult> CallLspRequestAsync(string method, JsonObject arguments, CancellationToken ct)
    {
        PrepareDocument(arguments);
        NormalizeAssetArgument(arguments);

        string? reason = _client.UnavailableReason(method);
        if (reason != null)
        {
            return new LspResult(null, new LspError(JsonRpcErrorCode.InternalError, reason));
        }

        LspResult res = await _client.RequestAsync(method, arguments, _options.Timeout, ct).ConfigureAwait(false);

        if (res.Error is { Code: JsonRpcErrorCode.MethodNotFound } error && !_client.IsProxy)
        {
            return new LspResult(null, error with { Message = error.Message + StandaloneHint });
        }

        return res;
    }

    /// <summary>textDocument.uri を正規化して置き換え、ドキュメントを同期する。</summary>
    private void PrepareDocument(JsonObject arguments)
    {
        if (arguments["textDocument"] is not JsonObject textDocument) return;
        string? raw = GetString(textDocument["uri"]);
        if (raw == null) return;

        string uri = NormalizeUri(raw);
        textDocument["uri"] = uri;
        _sync.Sync(uri);
    }

    /// <summary>GUID ツールの asset（URI / 絶対 / 相対パス）を file:/// URI に正規化する。ドキュメント同期はしない。</summary>
    private void NormalizeAssetArgument(JsonObject arguments)
    {
        if (GetString(arguments["asset"]) is string raw && raw.Length > 0)
        {
            arguments["asset"] = NormalizeUri(raw);
        }
    }

    private async Task<LspResult> ResetAsync(CancellationToken ct)
    {
        _sync.CloseAll();

        try
        {
            var args = new JsonObject
            {
                ["command"] = "unitycsharp.reloadWorkspace",
                ["arguments"] = new JsonArray(),
            };
            await _client.RequestAsync("workspace/executeCommand", args, _options.Timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] reloadWorkspace error (ignored): {ex.Message}");
        }

        return new LspResult(null, null);
    }

    private LspResult DiagnosticsCached(JsonObject arguments)
    {
        IReadOnlyDictionary<string, JsonNode> cached = _client.CachedDiagnostics;

        string? raw = GetString(arguments["uri"]);
        if (raw != null)
        {
            string uri = NormalizeUri(raw);
            return new LspResult(cached.TryGetValue(uri, out JsonNode? diagnostics) ? diagnostics.DeepClone() : null, null);
        }

        var all = new JsonObject();
        foreach (KeyValuePair<string, JsonNode> entry in cached)
        {
            all[entry.Key] = entry.Value.DeepClone();
        }
        return new LspResult(all, null);
    }

    // ---------- URI 正規化 ----------

    /// <summary>
    /// file:/// URI・絶対パス・ワークスペース相対パスを file:/// URI に正規化する。
    /// それ以外のスキーマ（untitled: 等）はそのまま返す。
    /// </summary>
    private string NormalizeUri(string raw)
    {
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceManager.PathToFileUri(new Uri(raw).LocalPath);
        }

        if (Path.IsPathFullyQualified(raw))
        {
            return WorkspaceManager.PathToFileUri(raw);
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? other) && other.Scheme.Length > 1)
        {
            return raw;
        }

        return WorkspaceManager.PathToFileUri(Path.Combine(_options.WorkspaceRoot, raw));
    }

    // ---------- 結果の整形 ----------

    /// <summary>互換契約（CARD-MCP-00）: text = LSP result の JSON、オブジェクトのときだけ structuredContent、エラーは isError。</summary>
    private static JsonObject BuildToolResult(LspResult outcome)
    {
        var result = new JsonObject();

        if (outcome.Error != null)
        {
            string errorJson = new JsonObject
            {
                ["code"] = outcome.Error.Code,
                ["message"] = outcome.Error.Message,
            }.ToJsonString(WriteOptions);

            result["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = errorJson });
            result["isError"] = true;
            return result;
        }

        string text = outcome.Result?.ToJsonString(WriteOptions) ?? "null";
        result["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        if (outcome.Result is JsonObject)
        {
            result["structuredContent"] = outcome.Result.DeepClone();
        }
        return result;
    }

    // ---------- 入出力 ----------

    private void WriteResult(JsonNode? id, JsonNode? result)
    {
        WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        });
    }

    private void WriteError(JsonNode? id, int code, string message)
    {
        WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });
    }

    private void WriteMessage(JsonObject message)
    {
        byte[] bytes = Utf8NoBom.GetBytes(message.ToJsonString(WriteOptions) + "\n");
        try
        {
            lock (_outputGate)
            {
                _output.Write(bytes, 0, bytes.Length);
                _output.Flush();
            }
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[MCP] Failed to write response: {ex.Message}");
        }
    }

    private static string? GetString(JsonNode? node)
    {
        return node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }
}
