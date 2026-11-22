# MCP モード（UnityCSharpFastLSP）

UnityCSharpFastLSP を **MCP (Model Context Protocol) サーバー** として起動し、Claude Code / OpenCode などの LLM エージェントからツールとして呼び出せるようにするモードです。

- 仕様（互換性の基準）: [doc/mcp-csharp-lsp.md](mcp-csharp-lsp.md)（UnityCSharpLSP 側の MCP 仕様）
- 互換の範囲: ツール名・引数・結果形式は UnityCSharpLSP の MCP モードと同じです（後述「互換性」）。
- 実装: `src/UnityCSharpFastLSP.Server/Mcp/`（`McpServer.cs`, `McpToolTable.cs`, `LspLoopbackClient.cs`, `DocumentSyncTracker.cs`）

---

## 1. 概要と 2 つの動作モード

| モード | 起動 | 応答するツール | それ以外のツール |
| :--- | :--- | :--- | :--- |
| **スタンドアロン** | `--mcp` | 高速メソッド 12 種（後述 §4） | LSP エラー `-32601` を `isError: true` で返す。メッセージに「`--mcp --proxy` で upstream にフォールバックできる」旨を付与 |
| **プロキシ（フォールバック）** | `--mcp --proxy [--upstream-kind <k>] [--upstream <path>]` | 高速メソッド 12 種は FastLSP が応答 | 残りはすべて選択された **upstream LSP サーバー**（UnityCSharpLSP / csharp-ls / OmniSharp）に委譲 |

- 読み取り中心（定義・参照・シンボル・hover）ならスタンドアロンで十分です。起動が軽く、常に即答します。
- rename・codeAction・型階層/呼び出し階層・コンパイラ診断・`unitycsharp.*` コマンド（アセット・GUID 検索）が必要なら `--proxy` を付けてください。upstream LSP の実行ファイルが別途必要です（`unitycsharp.*` は UnityCSharpLSP 固有）。
- **空結果フォールバック（v0.3.1+、プロキシ時）**: 高速メソッドの結果が空（`null` / `[]` / `{}` / `""`）の場合は、FastLSP の応答を破棄し、同じリクエストを選択された upstream に再委譲します。非空の結果は従来どおり FastLSP が即答するため、高速パスのレイテンシは変わりません。Upstream が未起動・起動失敗・終了済みのときは再委譲せず、空結果をそのまま返します。
- ルーティングの判定は `LspProxy.FastMethods`（`src/UnityCSharpFastLSP.Server/LspProxy.cs`）に一任しており、MCP 層では独自判定していません。

---

## 2. 起動オプション

| オプション | 既定値 | 説明 |
| :--- | :--- | :--- |
| `--mcp` | — | MCP サーバーとして stdio で起動する（stdout は MCP 専用、ログは stderr）。 |
| `--proxy` | 無効 | upstream へのフォールバックを有効にする。`--upstream` / `--upstream-kind` を指定すると自動で有効になる。 |
| `--upstream-kind <k>` | `auto` | フォールバック先を選択: `auto` / `unitycsharp` / `csharp-ls` / `omnisharp`。`auto` は UnityCSharpLSP → csharp-ls → OmniSharp の順に起動できるものを試す。 |
| `--upstream <path>` | 探索 | upstream の実行ファイルを明示する（ファイル名から種別を推定）。 |
| `--workspace <path>` | カレントディレクトリから上方探索 | ワークスペースのルート＋upstream の作業ディレクトリ（OmniSharp には `-s` としても渡る）。省略時はカレントディレクトリから上に辿り、`Assets/` か `ProjectSettings/` を持つ最も近い祖先。どちらも無ければカレントディレクトリ。 |
| `--settings <json>` | なし | 起動時に `workspace/didChangeConfiguration` で送る設定。`@<file>` でファイルから読む。**プロキシモードでのみ効果がある**（§6 参照）。 |
| `--timeout <sec>` | `120` | 1 リクエストあたりのタイムアウト（秒、正の整数）。 |
| `--print-config --mcp [--proxy] [--workspace <path>] [--upstream <path>]` | — | MCP クライアント用の `mcpServers` 設定スニペットを出力して終了する。 |

upstream の探索順（`--proxy` 時）: 各プロバイダーについて `--upstream` → 種別ごとの環境変数（`UNITY_CSHARP_LSP_PATH` / `CSHARP_LS_PATH` / `OMNISHARP_PATH`）→ PATH 上の `UnityCSharpLSP.Server.exe` / `csharp-ls` / `omnisharp` など → 標準インストール先（`%LOCALAPPDATA%\Programs\...`、`~/.dotnet/tools` など）。`auto` ではプロバイダーを順に試し、どれも起動できなければそのリクエストに upstream 起動失敗の理由（`--upstream` / `--upstream-kind` か環境変数を設定する旨）を返します。起動引数はプロバイダーごとに異なります（UnityCSharpLSP: `--stdio`、csharp-ls: なし、OmniSharp: `-lsp [-s <workspace>]`）。

---

## 3. クライアント設定例

### 3.1 設定スニペットの自動生成

```powershell
UnityCSharpFastLSP.Server.exe --print-config --mcp --workspace D:/MyUnityProject
UnityCSharpFastLSP.Server.exe --print-config --mcp --proxy --workspace D:/MyUnityProject
```

出力は次の形式です（実行ファイルのパスは解決済みの絶対パスになります）。

```json
{
  "mcpServers": {
    "unity-csharp-fast-lsp": {
      "command": "<path/to/UnityCSharpFastLSP.Server.exe>",
      "args": ["--mcp", "--workspace", "<path>"]
    }
  }
}
```

### 3.2 Claude Code

```powershell
# スタンドアロン
claude mcp add unity-csharp-fast-lsp -- <path/to/UnityCSharpFastLSP.Server.exe> --mcp --workspace <path>

# プロキシ（upstream LSP にフォールバック。--upstream-kind で選択）
claude mcp add unity-csharp-fast-lsp -- <path/to/UnityCSharpFastLSP.Server.exe> --mcp --proxy --workspace <path>
```

`.mcp.json` に直接書く場合:

```json
{
  "mcpServers": {
    "unity-csharp-fast-lsp": {
      "command": "<path/to/UnityCSharpFastLSP.Server.exe>",
      "args": ["--mcp", "--proxy", "--workspace", "<path>"]
    }
  }
}
```

### 3.3 OpenCode

`opencode.json`:

```json
{
  "mcp": {
    "unity-csharp-fast-lsp": {
      "type": "local",
      "command": ["<path/to/UnityCSharpFastLSP.Server.exe>", "--mcp", "--workspace", "<path>"],
      "enabled": true
    }
  }
}
```

プロキシにする場合は `command` に `"--proxy"` を追加してください。

---

## 4. ツール一覧（全 40 ツール）

ツール名 = LSP メソッド名の `/` を `_` に置換したものです（例: `textDocument/definition` → `textDocument_definition`）。`arguments` は LSP の params オブジェクトそのもの、結果は LSP の result JSON です。

「スタンドアロン」「プロキシ」列の意味:
- **FastLSP**: この実装が直接応答する。
- **upstream**: `--proxy` 時に委譲される（`--upstream-kind` で UnityCSharpLSP / csharp-ls / OmniSharp を選択）。下の表の「UnityCSharpLSP」列は委譲先を指す。
- **−32601**: スタンドアロンでは未対応（`isError` で返る）。

### 4.1 読み取り系（`readOnlyHint: true`）

| MCP ツール | LSP メソッド | スタンドアロン | プロキシ |
| :--- | :--- | :---: | :---: |
| `textDocument_hover` | `textDocument/hover` | FastLSP | FastLSP |
| `textDocument_definition` | `textDocument/definition` | FastLSP | FastLSP |
| `textDocument_declaration` | `textDocument/declaration` | FastLSP | FastLSP |
| `textDocument_typeDefinition` | `textDocument/typeDefinition` | FastLSP | FastLSP |
| `textDocument_implementation` | `textDocument/implementation` | FastLSP | FastLSP |
| `textDocument_references` | `textDocument/references` | FastLSP | FastLSP |
| `textDocument_documentSymbol` | `textDocument/documentSymbol` | FastLSP | FastLSP |
| `textDocument_documentHighlight` | `textDocument/documentHighlight` | FastLSP | FastLSP |
| `textDocument_signatureHelp` | `textDocument/signatureHelp` | FastLSP | FastLSP |
| `textDocument_completion` | `textDocument/completion` | FastLSP | FastLSP |
| `completionItem_resolve` | `completionItem/resolve` | −32601 | UnityCSharpLSP |
| `textDocument_diagnostic` | `textDocument/diagnostic` | −32601 | UnityCSharpLSP |
| `workspace_diagnostic` | `workspace/diagnostic` | −32601 | UnityCSharpLSP |
| `workspace_symbol` | `workspace/symbol` | FastLSP | FastLSP |
| `workspaceSymbol_resolve` | `workspaceSymbol/resolve` | −32601 | UnityCSharpLSP |
| `textDocument_prepareCallHierarchy` | `textDocument/prepareCallHierarchy` | −32601 | UnityCSharpLSP |
| `callHierarchy_incomingCalls` | `callHierarchy/incomingCalls` | −32601 | UnityCSharpLSP |
| `callHierarchy_outgoingCalls` | `callHierarchy/outgoingCalls` | −32601 | UnityCSharpLSP |
| `textDocument_prepareTypeHierarchy` | `textDocument/prepareTypeHierarchy` | −32601 | UnityCSharpLSP |
| `typeHierarchy_supertypes` | `typeHierarchy/supertypes` | −32601 | UnityCSharpLSP |
| `typeHierarchy_subtypes` | `typeHierarchy/subtypes` | −32601 | UnityCSharpLSP |
| `textDocument_codeLens` | `textDocument/codeLens` | −32601 | UnityCSharpLSP |
| `textDocument_documentLink` | `textDocument/documentLink` | −32601 | UnityCSharpLSP |
| `textDocument_inlayHint` | `textDocument/inlayHint` | −32601 | UnityCSharpLSP |
| `textDocument_moniker` | `textDocument/moniker` | −32601 | UnityCSharpLSP |
| `textDocument_prepareRename` | `textDocument/prepareRename` | −32601 | UnityCSharpLSP |

### 4.2 編集提案系（`WorkspaceEdit` / `TextEdit[]` を返すだけ。ファイルは書き換えない）

| MCP ツール | LSP メソッド | スタンドアロン | プロキシ |
| :--- | :--- | :---: | :---: |
| `textDocument_rename` | `textDocument/rename` | −32601 | UnityCSharpLSP |
| `textDocument_codeAction` | `textDocument/codeAction` | −32601 | UnityCSharpLSP |
| `codeAction_resolve` | `codeAction/resolve` | −32601 | UnityCSharpLSP |
| `textDocument_formatting` | `textDocument/formatting` | FastLSP | FastLSP |
| `textDocument_rangeFormatting` | `textDocument/rangeFormatting` | −32601 | UnityCSharpLSP |
| `workspace_willRenameFiles` | `workspace/willRenameFiles` | −32601 | UnityCSharpLSP |
| `workspace_willCreateFiles` | `workspace/willCreateFiles` | −32601 | UnityCSharpLSP |
| `workspace_willDeleteFiles` | `workspace/willDeleteFiles` | −32601 | UnityCSharpLSP |

### 4.3 コマンド・設定・ブリッジ独自

| MCP ツール | 内容 | スタンドアロン | プロキシ |
| :--- | :--- | :--- | :--- |
| `workspace_executeCommand` | `workspace/executeCommand`（`unitycsharp.*` の 7 コマンド） | −32601 | UnityCSharpLSP |
| `workspace_didChangeConfiguration` | 通知を送り `null` を返す | 通知は無視される（§6） | UnityCSharpLSP へ転送 |
| `lsp_reset` | ブリッジ独自。全ドキュメント `didClose` + `unitycsharp.reloadWorkspace` | didClose のみ（reload は実行されない。§6） | didClose + UnityCSharpLSP で reload |
| `lsp_diagnostics_cached` | ブリッジ独自。`publishDiagnostics` のキャッシュを返す（`uri` 省略で全件） | ブリッジ内で完結 | ブリッジ内で完結 |
| `unity_guid_definition` | FastLSP 独自 `unitylsp/guidDefinition`。`guid` または `asset` から `.meta` とアセット本体を返す（`readOnlyHint`） | FastLSP | upstream が advertise していれば upstream、無ければ FastLSP |
| `unity_guid_references` | FastLSP 独自 `unitylsp/guidReferences`。guid を参照するシーン・プレハブ・マテリアル・`.asset`・`.meta`・`.asmdef/.asmref` の位置を返す（`readOnlyHint`） | FastLSP | upstream が advertise していれば upstream、無ければ FastLSP |

#### Unity GUID ツール

仕様（メソッド名・引数・結果の形）は UnityCSharpLSP の同名メソッド・同名ツールと同じ。

- 引数: `guid`（32 桁 16 進）または `asset`（アセット・スクリプト・`.meta` のパス。`file:///` URI / 絶対 / ワークスペース相対）。
  どちらも無い・`guid` が不正・`.meta` が見つからない場合は、エラーにせず `null` を返す。
- `unity_guid_references` だけが取る引数:
  - `maxResults`: 返す参照の上限（既定 500。0 以下も既定値）。超えたら `truncated: true`。
  - `includePatterns`: 走査するファイルの glob（プロジェクトルート相対）。指定すると既定のテキストアセット拡張子の代わりになるので、`*.cs` なども走査できる。例: `["*.prefab", "Assets/Scenes/**", "*.cs"]`
  - `excludePatterns`: 常に除外するファイルの glob。例: `["Library/PackageCache/**", "*.meta"]`
  - glob の書式: `/` を含まないパターンはファイル名と照合（`*.cs` は ripgrep の `-g '*.cs'` と同じ）。`*` は `/` 以外の任意文字列、`**` は任意の階層、`?` は 1 文字、先頭 `!` で否定（後のパターンが優先）、先頭 `~` でパスの部分一致。大文字小文字は区別しない。
- `unity_guid_definition` の結果: `{ guid, assetUri, meta: Location }`（見つからなければ `null`）。
- `unity_guid_references` の結果: `{ guid, assetUri, references: [{ uri, range, lineText, classId?, objectFileId?, fileId? }], truncated }`。
  `classId` / `objectFileId` は YAML 上の所属オブジェクト（`--- !u!<classId> &<objectFileId>`）、`fileId` は同じ行の `fileID:`。
- 索引方式: 起動時に `Assets/`・`Packages/`・`Library/PackageCache/` の `*.meta` から guid → アセットだけを常駐索引する。参照は呼び出しのたびにテキストアセットを並列走査する（Binary 直列化のアセットは対象外）。索引後に追加された `.meta` は未登録 guid の問い合わせ時に再走査して拾う（最短 5 秒間隔）。
- 例: `{"asset":"Assets/Scripts/Player.cs"}` → `Player` MonoBehaviour をアタッチしたプレハブ・シーンの `m_Script` 行の一覧。
  `{"asset":"Assets/Scripts/Player.cs","includePatterns":["Assets/Scenes/**"]}` → シーン内の参照だけ。
- プロキシ（`--proxy`）での担当: upstream が initialize 応答の `capabilities.experimental.unitylsp.methods` でメソッドを advertise していれば upstream だけが答え、FastLSP 側の GUID 機能（`.meta` 索引と応答）は完全に止める（upstream のエラーもそのまま返し、FastLSP にはフォールバックしない）。advertise していない、または upstream が起動しなかった場合は FastLSP が答える。詳細は `doc/proxy-feature.md` §1.2。

`workspace_executeCommand` の `command` に指定できる値（enum）:
`unitycsharp.reloadWorkspace`, `unitycsharp.showWorkspaceInfo`, `unitycsharp.getSceneOrPrefabHierarchy`, `unitycsharp.findSymbolUsagesInAssets`, `unitycsharp.findGuidReferences`, `unitycsharp.listReferencedGuids`, `unitycsharp.assetServerStatus`。

### 4.4 公開しないもの

`semanticTokens/*`, `foldingRange`, `selectionRange`, `linkedEditingRange`, `inlineValue`, `onTypeFormatting`, `codeLens/resolve`, `textDocument/didSave` 系は MCP には公開していません（表示用途でエージェントに価値が薄いため）。

### 4.5 編集の適用

`textDocument_rename` などの編集系ツールは **`WorkspaceEdit` を返すだけで、ファイルは書き換えません**。エージェントが自分のファイル編集ツールで適用してください。一括編集を行った後は `lsp_reset` を呼んで、UnityCSharpLSP のワークスペース状態を更新してください。

---

## 5. ブリッジの責務

MCP クライアント（LLM エージェント）は LSP クライアントの役割を持たないため、ブリッジ（`McpServer`）が次を肩代わりします。

| 責務 | 振る舞い |
| :--- | :--- |
| `initialize` / `initialized` | MCP 起動時に 1 回、LSP の `initialize` / `initialized` を送る。`workspaceFolders` は `--workspace`（§2 の既定値）。 |
| ドキュメント同期 | `textDocument.uri` を含む呼び出しの前に、未オープンならディスク内容で `didOpen`、オープン済みで mtime かサイズが変わっていれば全文 `didChange`。エージェントはファイルを直接編集する前提のため、`didClose` は送らない（全件閉じるのは `lsp_reset` のみ）。 |
| URI 正規化 | `textDocument.uri` は `file:///` URI・絶対パス・ワークスペース相対パスのいずれも受け付け、`WorkspaceManager.PathToFileUri` で `file:///` に正規化する（FastLSP のインデックスと同じ形式のため二重登録が起きない）。戻り値の URI は変換しない。 |
| 位置 | LSP どおり **0-based / UTF-16**。変換しない。 |
| 診断キャッシュ | `textDocument/publishDiagnostics` をキャッシュし、`lsp_diagnostics_cached` で読めるようにする。 |
| ログ | `window/logMessage`・`window/showMessage` は stderr。サーバーからの `workspace/configuration` 等のリクエストには空応答。 |
| 並行性 | `tools/call` は並行処理する（読み取りループを塞がない）。 |
| shutdown | MCP セッション終了時に `shutdown` / `exit` を送る。 |

---

## 6. エラーの形式と既知の制限

### 6.1 エラーの形式

- LSP のエラー応答は MCP のプロトコルエラーにせず、`isError: true` の結果として返します。`content[0].text` は `{"code":<int>,"message":"..."}` の JSON 文字列です。
- 未知のツール名は MCP の JSON-RPC エラー `-32602`（Invalid params）です。
- `null` の結果は `"null"` の文字列で返します（「見つからない」を表す LSP の意味を保つため）。
- タイムアウト（既定 120 秒）は `isError` の `{"code":-32603,"message":"Request '...' timed out after N s."}` で返します。

代表的なメッセージ:

| 状況 | `code` | `message` の例 |
| :--- | :---: | :--- |
| スタンドアロンで未対応のメソッド | -32601 | `Method not found: textDocument/rename (not supported by UnityCSharpFastLSP standalone; run with --mcp --proxy to fall back to an upstream LSP server)` |
| プロキシで upstream が起動できない | -32603 | `No upstream LSP server could be started.` で始まる理由（`--upstream` / `--upstream-kind` か環境変数を設定する） |
| upstream が起動後に終了した | -32603 | `Upstream <選択したプロバイダー名> exited (code N).` |

### 6.2 既知の制限（実装から確認できる差分）

1. **`textDocument_references` の `includePatterns` / `excludePatterns` は FastLSP が解釈しない。** ツールスキーマには載せているが、スタンドアロンでもプロキシでも references は FastLSP が応答するため、この 2 つのフィルタは無視される。フィルタが必要な場合は UnityCSharpLSP 単独の MCP サーバーを使う。
2. **スタンドアロンでは `workspace_didChangeConfiguration` と `--settings` は効果がない。** FastLSP は `workspace/didChangeConfiguration` を処理しないため、通知は無視される。設定が必要な場合は `--proxy` で起動する（upstream に転送される）。
3. **スタンドアロンの `lsp_reset` は reload を行わない。** 全ドキュメントの `didClose` だけを送り、`unitycsharp.reloadWorkspace` は FastLSP が未対応のため失敗するが、その失敗は無視されて成功扱いで `null` が返る。ワークスペースを再読込したい場合は `--proxy` で起動する。
4. **プロキシの高速メソッドは FastLSP の機能範囲に限られる。** completion・hover・definition などは FastLSP の結果がそのまま返る。UnityCSharpLSP（Roslyn）と結果が異なる場合がある（FastLSP は型推論がヒューリスティックなため）。
5. **プロキシの upstream は起動直後は準備中。** 起動前に届いた要求は upstream の準備完了まで保留される。
6. **応答が返らない場合は `--timeout`（既定 120 秒）で打ち切られ、`isError` で返る。** 打ち切られても UnityCSharpLSP 側の処理は止まらない。

---

## 7. 互換性

UnityCSharpLSP の MCP モード（[doc/mcp-csharp-lsp.md](mcp-csharp-lsp.md)）と次の点で互換です。

- ツール名（LSP メソッド名の `/` → `_`）・引数（LSP の params）・結果（LSP の result JSON）が同じ。
- `structuredContent` は結果がオブジェクトのときだけ付く。`content[0].text` は常に LSP result の JSON 文字列。
- LSP エラーは `isError: true`、`{"code","message"}` の JSON 文字列。
- `readOnlyHint` は §4.1 の読み取り系と `lsp_diagnostics_cached` に付与。
- 位置は 0-based / UTF-16。

ただし、**応答する処理系（FastLSP か Roslyn か）と結果の細部は異なる場合があります**。§6.2 の制限を参照してください。

---

## 8. 動作確認

```powershell
# ヘルプ（LLM 向けガイドを含む）
UnityCSharpFastLSP.Server.exe --help

# 設定スニペット
UnityCSharpFastLSP.Server.exe --print-config --mcp --proxy --workspace D:/MyUnityProject

# テスト（MCP 関連は tests/UnityCSharpFastLSP.Tests/Mcp/）
dotnet test tests/UnityCSharpFastLSP.Tests/UnityCSharpFastLSP.Tests.csproj -c Release
```

関連ドキュメント:
- [doc/proxy-feature.md](proxy-feature.md) — LSP のプロキシ仕様（MCP からも同じプロキシを使う）
- [skills/unity-fast-lsp/SKILL.md](../skills/unity-fast-lsp/SKILL.md) — エージェント向けの使い方ガイド
