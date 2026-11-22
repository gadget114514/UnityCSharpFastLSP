# [CARD-MCP-04] MCP モードのテスト

- **対象**: `tests/UnityCSharpFastLSP.Tests/Mcp/`（新規）
- **依存**: CARD-MCP-00（必読）, CARD-MCP-01〜03
- **仕様**: `doc/mcp-csharp-lsp.md` §7-6

## 1. ハーネス
- `McpTestSession`: 一時ディレクトリに Unity 風ワークスペース（`Assets/` 配下に複数の .cs）を作り、`McpServer` をプロセス内で起動する。入出力は `System.IO.Pipelines.Pipe` か匿名パイプ等の双方向ストリームで接続し、改行区切り JSON-RPC を送受信する。`CallTool(name, args)` → MCP result の `JsonObject` を返すヘルパー、`Dispose` で入力を閉じて `RunAsync` 完了を待つ。
- 既存 `Spec/SpecHarness.cs` の `LspSession` で同じファイルに直接 LSP 要求を出し、結果比較に使う。
- テストは 1 件あたり 30 秒以内。並列実行で一時ディレクトリが衝突しないこと。

## 2. テストケース（スタンドアロン）
1. `initialize` → `protocolVersion` / `serverInfo` / `capabilities.tools` を返す。
2. `tools/list` が 38 件、名前重複なし、仕様 §4 の全名を含み、§4.4 の名前（`textDocument_semanticTokens_full` 等）を含まない。§4.1 ツールに `readOnlyHint: true`。全 `inputSchema.type == "object"`。
3. `textDocument_definition`（cross-file）の `content[0].text` が、直接 LSP 呼び出しの result と JSON として等価。
4. `textDocument_references` / `workspace_symbol` / `textDocument_hover` / `textDocument_documentSymbol` が非エラーで期待どおりの結果。
5. `textDocument.uri` に **絶対パス** と **ワークスペース相対パス** を渡しても `file:///` と同じ結果。
6. ディスク上のファイルを書き換えた後の呼び出しで、新しい内容が反映される（再同期: 新しく追加したメソッドが `textDocument_documentSymbol` に現れる）。
7. スタンドアロンで `textDocument_rename` → `isError: true`、`content[0].text` を JSON パースすると `code == -32601` で message に `--proxy` を含む。
8. 未知のツール名 → JSON-RPC エラー `-32602`。
9. `workspace_didChangeConfiguration` → `"null"`。`lsp_reset` → 非エラー。`lsp_diagnostics_cached` → 構文エラーを含むファイルを一度開いた後、その uri の診断が取れる（publishDiagnostics は非同期なので最大数秒ポーリング可）。
10. `ping` → `{}`。不正 JSON 行 → `-32700`。

## 3. プロキシのフォールバック
- 実 UnityCSharpLSP は重いので、**偽の Upstream** を使う: テスト用に `LspProxy` が起動できる小さな LSP エコーサーバーが必要なら、テストプロジェクト内のヘルパー（例: `tests/UnityCSharpFastLSP.Tests/Mcp/FakeUpstream/` の別コンソールプロジェクト、または `dotnet <dll>` で起動できる既存 exe の流用）を用意する。作るのが難しければ以下で代替してよい:
  - `--upstream` に存在しないパスを渡した `CreateProxy` で、`textDocument_rename` が **タイムアウトを待たず** `isError` を返し、message に upstream 起動失敗の理由が入ること、かつ `textDocument_definition` は FastLSP が即答すること。
- 実 Upstream（`D:/wsgit/UnityCSharpLSP/artifacts/publish/UnityCSharpLSP.Server.exe` 等）を使う E2E は `Skip` 付き（環境変数 `UNITY_CSHARP_LSP_PATH` があるときだけ実行）にする。

## 4. 完了条件
1. `dotnet test tests/UnityCSharpFastLSP.Tests/UnityCSharpFastLSP.Tests.csproj -c Release` が全件成功（既存 + 新規）。
2. 実装側のバグを見つけたら、テストを緩めずに実装を直す（直した内容をレポートに書く）。
