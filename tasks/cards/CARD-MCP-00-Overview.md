# [CARD-MCP-00] MCP モード 全体方針（全カード共通）

- **仕様（互換性の基準）**: `doc/mcp-csharp-lsp.md`（UnityCSharpLSP 側 `D:\wsgit\UnityCSharpLSP\doc\mcp-csharp-lsp.md` のコピー）
- **ゴール**: `UnityCSharpFastLSP.Server --mcp` で、UnityCSharpLSP の MCP モードと **完全互換**（同じツール名・同じ引数・同じ結果形式）の MCP サーバーを提供する。
  - スタンドアロン `--mcp`: FastLSP が対応するメソッドは即答、未対応ツールは LSP エラー `-32601` を `isError: true` で返す（メッセージに「`--mcp --proxy` で UnityCSharpLSP にフォールバックできる」旨を添える）。
  - プロキシ `--mcp --proxy [--upstream <path>]`: 既存 `LspProxy` を経由し、FastLSP 非対応メソッドは **UnityCSharpLSP にフォールバック**する。ルーティングは `LspProxy.FastMethods` に一任（MCP 層で独自判定しない）。

## 互換契約（変えてはいけないもの）

| 項目 | 内容 |
| :--- | :--- |
| ツール名 | LSP メソッド名の `/` を `_` に置換（例 `textDocument_definition`）。仕様 §4.1〜4.3 の全ツール + `lsp_reset` + `lsp_diagnostics_cached` |
| 引数 | LSP の params オブジェクトそのもの。`textDocument_references` は拡張 `includePatterns` / `excludePatterns` も受ける |
| 結果 | `content[0].text` = LSP result の JSON 文字列。result が JSON オブジェクトのときだけ `structuredContent` にも同じものを入れる。`null` は `"null"` |
| LSP エラー | `isError: true`、`content[0].text` = `{"code":<int>,"message":"..."}`（MCP プロトコルエラーにしない） |
| 未知のツール名 | MCP の JSON-RPC エラー `-32602` |
| 位置 | LSP どおり 0-based / UTF-16。変換しない |
| URI | `textDocument.uri` は `file:///`・絶対パス・ワークスペース相対パスを受け付け、`WorkspaceManager.PathToFileUri` で正規化（FastLSP のインデックスと同じ形式。二重登録を防ぐ）。戻り値の URI は変換しない |
| readOnlyHint | 仕様 §4.1 のツールと `lsp_diagnostics_cached` に `annotations.readOnlyHint: true` |
| 非公開 | 仕様 §4.4（semanticTokens 等）は公開しない |
| stdout | MCP 専用（改行区切り JSON-RPC）。ログはすべて stderr |

## 実装済み（supervisor 着手分・変更しないこと）

- `src/UnityCSharpFastLSP.Server/Mcp/LspLoopbackClient.cs`
  - `LspLoopbackClient.CreateStandalone()` / `CreateProxy(upstreamPath)`、`RequestAsync(method, JsonNode? params, TimeSpan timeout, ct)` → `LspResult(Result, Error)`、`Notify(method, params)`、`CachedDiagnostics`（uri → publishDiagnostics params）、`UnavailableReason(method)`（プロキシで Upstream が起動できていない場合の理由）、`IsProxy`。
  - サーバー→クライアント要求（`workspace/configuration` 等）には空応答を自動返信、`window/logMessage`/`showMessage` は stderr。
- `LspMessageWriter.Frame()` 追加とフレーム書き込みの不可分化、`LspProxy.IsFastPath(method)` / `LspProxy.UpstreamError`。

## カード構成と依存

```
MCP-01 ツール表 → MCP-02 MCP サーバー/ブリッジ → MCP-03 Program 統合 → MCP-04 テスト → MCP-05 ドキュメント
```

## 共通ルール

- ビルド: `C:/Users/bluen/dotnet11/dotnet.exe build UnityCSharpFastLSP.sln -c Release`（`TreatWarningsAsErrors=true`。警告 0 必須）。環境変数 `DOTNET_ROOT=C:\Users\bluen\dotnet11`。
- NuGet パッケージを追加しない（`ModelContextProtocol` SDK は使わず、MCP stdio は System.Text.Json / `JsonNode` で手実装）。
- 既存 LSP ハンドラー（`LspServer` の各 Handle*）の挙動を変えない。
- namespace は `UnityCSharpFastLSP.Server.Mcp`。コメントは日本語で既存コードの調子に合わせる。
- プロジェクト外のファイルを読み書きしない。git commit しない。
