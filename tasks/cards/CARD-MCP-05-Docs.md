# [CARD-MCP-05] MCP モードのドキュメント

- **対象**: `doc/mcp.md`（新規）, `README.md`, `doc/proxy-feature.md`, `tasks/README.md`, `src/UnityCSharpFastLSP.Server/Program.cs`（`HelpDescription` のみ）, `skills/unity-fast-lsp/SKILL.md`
- **依存**: CARD-MCP-00（必読）, CARD-MCP-01〜04

## 1. 要件
1. `doc/mcp.md`（日本語）:
   - 概要と `doc/mcp-csharp-lsp.md`（UnityCSharpLSP の MCP 仕様）との互換性の説明。
   - 起動方法（`--mcp`, `--mcp --proxy`, `--workspace`, `--settings`, `--timeout`, `--print-config --mcp`）と Claude Code / OpenCode の設定例。
   - ツール一覧表: 全 38 ツールについて「スタンドアロンで応答するか / プロキシ時の処理系（FastLSP or UnityCSharpLSP）」。処理系は `LspProxy.FastMethods` と `LspServer.Dispatch` の実装から正確に起こす（推測しない）。
   - ブリッジの責務（initialize、ドキュメント同期、URI 正規化、診断キャッシュ、エラー形式）。
   - 既知の制限: スタンドアロンの FastLSP は `textDocument_references` の `includePatterns` / `excludePatterns` を解釈しない（プロキシでも references は FastLSP が応答する）等、実装から分かる差分。
2. `README.md`: 機能一覧と使い方に MCP モードの節を追加し、`doc/mcp.md` へリンク。
3. `doc/proxy-feature.md`: MCP モードからもプロキシが使える旨を 1 節追記。
4. `tasks/README.md`: タスクカード表に MCP フェーズ（CARD-MCP-00〜05）を追加。

5. **LLM エージェント向けの導線（ユーザー追加要件）**: LLM が README・`--help`・スキルを読むだけで、MCP サーバーとフォールバック（プロキシ）モードを正しく選んで使えるようにする。以下 3 か所で内容を一致させること。

   **5.1 `--help`（`src/UnityCSharpFastLSP.Server/Program.cs` の `HelpDescription`）** に `LLM / AI AGENT GUIDE (MCP):` 節を追加（既存の MCP オプション説明は残す）:
   - モードの選び方: 読み取り中心（定義・参照・シンボル・hover）なら `--mcp`。rename / codeAction / 型階層・呼び出し階層 / コンパイラ診断 / `unitycsharp.*` コマンド（アセット・GUID 検索）が必要なら `--mcp --proxy`（UnityCSharpLSP が必要。探索順 `--upstream` → `UNITY_CSHARP_LSP_PATH` → PATH → 標準インストール先）。
   - 呼び方の要点: position は 0-based（line, UTF-16 character）、`textDocument.uri` は絶対パス・ワークスペース相対パス可、ファイルはディスクから自動同期（didOpen 不要）、編集系ツールは `WorkspaceEdit` を返すだけで適用しない、一括編集後は `lsp_reset`。
   - エラーの読み方: `isError` + `{"code":-32601}` かつ message に `--proxy` → そのツールはスタンドアロン非対応なので `--mcp --proxy` で再起動。Upstream 起動失敗の message → `--upstream` か `UNITY_CSHARP_LSP_PATH` を設定。
   - 80 桁程度で折り返し、既存のヘルプの書式に合わせる。

   **5.2 `README.md`** に「LLM エージェントで使う」節（英語、既存 README の言語に合わせる）:
   - LSP として使う（既存 opencode `lsp` 設定）と MCP サーバーとして使う方法の違いと選び方。
   - MCP 設定例 3 種: Claude Code（`claude mcp add unity-csharp-fast-lsp -- <exe> --mcp --workspace <path>`、および `.mcp.json` の `mcpServers` 形式）、OpenCode（`opencode.json` の `"mcp": {"unity-csharp-fast-lsp": {"type": "local", "command": ["<exe>", "--mcp", "--workspace", "<path>"], "enabled": true}}`）、それぞれ `--proxy` 付きの例も。
   - スタンドアロン / フォールバックのモード比較表（どのツールがどちらで動くか。`doc/mcp.md` へリンク）。
   - `--print-config --mcp [--proxy] [--workspace]` の紹介。

   **5.3 `skills/unity-fast-lsp/SKILL.md`** を LSP と MCP の両方を扱うスキルに更新:
   - frontmatter の `description` に MCP サーバー・`--mcp`・`--proxy` フォールバック・UnityCSharpLSP 互換の語を入れ、MCP 設定やツール呼び出し時にもトリガーされるようにする。
   - 節: (1) 実行ファイルの探し方（既存）、(2) モード選択の判断表（LSP / MCP スタンドアロン / MCP プロキシ）、(3) MCP 登録手順（Claude Code / OpenCode、`--print-config --mcp`）、(4) ツール早見表（目的 → ツール名 → 最小引数の JSON 例。例: 定義ジャンプ `textDocument_definition {"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":10,"character":8}}`、ファイル概要 `textDocument_documentSymbol`、シンボル検索 `workspace_symbol {"query":"Player"}`、参照 `textDocument_references`、rename（proxy）、アセット使用箇所 `workspace_executeCommand {"command":"unitycsharp.findSymbolUsagesInAssets",...}`（proxy、引数形式は推測で書かず「UnityCSharpLSP のドキュメント参照」とする）)、(5) 推奨ワークフロー（workspace_symbol で位置を得る → definition/references → 編集は自分のツールで → 必要なら lsp_reset）、(6) エラーとフォールバックの対処、(7) 既存の機能表・制限（LSP 節）は残す。
   - ツール名・引数・エラーメッセージは実装（`McpToolTable.cs`, `McpServer.cs`）から正確に写す。推測で書かない。

## 2. 完了条件
- 記述がコードと一致している（オプション名・ツール数・ルーティング）。
- `--help`・README・SKILL.md の 3 か所でモード選択・設定例・エラー対処の内容が矛盾しない。
- `UnityCSharpFastLSP.Server.exe --help` が新しい節を表示する（ビルド後に実行して確認）。
