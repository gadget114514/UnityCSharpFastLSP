# [CARD-MCP-02] MCP stdio サーバーと LSP ブリッジ

- **対象**: `src/UnityCSharpFastLSP.Server/Mcp/McpServer.cs`, `src/UnityCSharpFastLSP.Server/Mcp/DocumentSyncTracker.cs`（新規）
- **依存**: CARD-MCP-00（必読）, CARD-MCP-01
- **仕様**: `doc/mcp-csharp-lsp.md` §3, §6

## 1. 目的
MCP クライアント（Claude Code 等）と `LspLoopbackClient` の間に立ち、`tools/list` / `tools/call` を LSP リクエストに変換する。LSP クライアントの責務（initialize、ドキュメント同期、URI 正規化）を肩代わりする。

## 2. 実装要件

### 2.1 McpServerOptions / McpServer
```csharp
public sealed record McpServerOptions(
    string WorkspaceRoot,        // 正規化済み絶対パス
    bool Proxy,
    string? UpstreamPath,
    JsonNode? Settings,          // --settings の JSON（null 可）
    TimeSpan Timeout);           // 既定 120 秒

public sealed class McpServer : IDisposable
{
    public McpServer(McpServerOptions options, Stream input, Stream output, LspLoopbackClient? client = null); // client はテスト注入用
    public Task<int> RunAsync(CancellationToken ct = default); // 入力 EOF で LSP shutdown/exit して 0 を返す
    public static string FindWorkspaceRoot(string startDir); // Assets/ か ProjectSettings/ を持つ最も近い祖先。無ければ startDir
}
```

### 2.2 起動時の LSP 初期化（RunAsync の最初、MCP メッセージを読む前）
1. `initialize` を送る: `processId`(自プロセス), `rootUri`, `rootPath`, `workspaceFolders: [{uri, name}]`, `clientInfo: {name: "UnityCSharpFastLSP-MCP"}`, `capabilities`（hover の contentFormat `["markdown","plaintext"]`、documentSymbol `hierarchicalDocumentSymbolSupport: true`、rename `prepareSupport: true`、codeAction `codeActionLiteralSupport`、callHierarchy / typeHierarchy / diagnostic / inlayHint、workspace の `workspaceFolders`, `configuration`, `symbol`, `executeCommand`, `diagnostics`）。definition 系の `linkSupport` は付けない（結果を Location に揃える）。
2. `initialized` 通知。
3. `Settings` があれば `workspace/didChangeConfiguration { settings }` 通知。
- initialize がエラー/タイムアウトしても MCP 自体は起動し続け、stderr にログを出す。

### 2.3 MCP プロトコル（stdio, 改行区切り JSON-RPC 2.0）
- 入力は UTF-8 の 1 行 1 メッセージ。空行は無視。パース不能なら `-32700`（id null）。
- 出力は `JsonNode.ToJsonString()` + `\n`（BOM なし UTF-8）。書き込みはロックで直列化。
- `initialize`: `{protocolVersion, capabilities: {tools: {listChanged: false}}, serverInfo: {name: "unity-csharp-fast-lsp", version: "0.2.1"}, instructions}`。`protocolVersion` はクライアントが送った値が `2024-11-05` / `2025-03-26` / `2025-06-18` のいずれかならそれを返し、それ以外は `2025-06-18`。`instructions` はツール概要と「位置は 0-based」「uri はパス可」「--proxy でないと rename/codeAction 等は未対応」を数行で。
- `notifications/initialized`, `notifications/cancelled` 等の通知: 無視。
- `ping`: `{}`。`tools/list`: `{tools: McpToolTable.All.Select(ToListEntry)}`（ページングなし）。
- `tools/call`: 下記 2.4。**各呼び出しは Task で並行処理**し、読み取りループを塞がない。
- 未知のメソッド（id あり）: `-32601`。

### 2.4 tools/call
1. `params.name` で `McpToolTable.Find`。無ければ JSON-RPC エラー `-32602`（`Unknown tool: <name>`）。
2. `arguments`（省略時 `{}`）を DeepClone。
3. Kind 別:
   - **LspRequest**: `arguments.textDocument.uri` が文字列なら URI 正規化（2.5）して置き換え、ドキュメント同期（2.6）。`client.UnavailableReason(method)` が非 null なら LSP エラー `-32603` 相当で即 `isError`。そうでなければ `RequestAsync(method, args, Timeout)`。
   - **LspNotification**: `Notify(method, args)`、結果 `null`。
   - **Reset**: 同期中の全ドキュメントに `textDocument/didClose` を送り、トラッカーを空にし、`workspace/executeCommand {command: "unitycsharp.reloadWorkspace", arguments: []}` を送る（エラーは無視）。結果 `null`。
   - **DiagnosticsCached**: `arguments.uri` があれば正規化してその uri の params（無ければ `null`）、無ければ `{ "<uri>": params, ... }` の全件オブジェクト。
4. 結果の整形は CARD-MCP-00 の互換契約どおり。LSP エラーで code が `-32601` かつ非プロキシのときは message に ` (not supported by UnityCSharpFastLSP standalone; run with --mcp --proxy to fall back to UnityCSharpLSP)` を追記。
5. 予期しない例外は `isError: true`（code `-32603`）にして MCP セッションは継続。

### 2.5 URI 正規化
- `file:` で始まる → `new Uri(s).LocalPath` → `WorkspaceManager.PathToFileUri`。
- 絶対パス → `PathToFileUri`。相対パス → `Path.Combine(WorkspaceRoot, s)` → `PathToFileUri`。
- それ以外（`untitled:` 等）はそのまま。

### 2.6 DocumentSyncTracker（uri → version, mtime, size）
- `textDocument.uri` を含む呼び出しの直前に `Sync(uri)`:
  - ファイルが存在しない → 何もしない。
  - スタンドアロンでは拡張子 `.cs` 以外は同期しない（プロキシでは全拡張子を同期。UnityCSharpLSP は YAML アセットも扱うため）。
  - 未オープン → `didOpen {uri, languageId ("csharp" for .cs, それ以外 "yaml"), version: 1, text}`。
  - オープン済みで mtime かサイズが変化 → version++ して全文 `didChange {textDocument:{uri, version}, contentChanges:[{text}]}`。
  - `didClose` は送らない（`lsp_reset` のみ）。
- スレッドセーフ（同じ uri への並行 Sync で didOpen が二重に出ないこと）。
- ファイル読み込みは共有読み取り（`FileShare.ReadWrite`）で、IOException 時はスキップ。

### 2.7 終了
入力 EOF（または ct キャンセル）で、実行中の tools/call を待ってから `shutdown` リクエスト（タイムアウト 5 秒）→ `exit` 通知 → client Dispose。

## 3. 完了条件
1. `dotnet build UnityCSharpFastLSP.sln -c Release` が警告 0 / エラー 0。
2. 既存テスト `dotnet test tests/UnityCSharpFastLSP.Tests -c Release` がすべて通る。
