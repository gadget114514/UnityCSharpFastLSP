# [CARD-MCP-03] Program.cs への --mcp モード統合

- **対象**: `src/UnityCSharpFastLSP.Server/Program.cs`
- **依存**: CARD-MCP-00（必読）, CARD-MCP-02
- **仕様**: `doc/mcp-csharp-lsp.md` §2, §7-5, §8

## 1. 実装要件

### 1.1 コマンドライン
| オプション | 内容 |
| :--- | :--- |
| `--mcp` | MCP サーバーとして起動（stdio）。`--proxy` / `--upstream <path>` と併用可能 |
| `--workspace <path>` | ワークスペースルート。省略時 `McpServer.FindWorkspaceRoot(Environment.CurrentDirectory)` |
| `--settings <json>` | 起動時に送る `workspace/didChangeConfiguration` の settings（JSON 文字列。`@<file>` ならファイルから読む）。パース失敗は stderr にエラーを出して exit 1 |
| `--timeout <sec>` | LSP リクエストのタイムアウト秒（既定 120）。不正値は exit 1 |

- `--mcp` のとき: `McpServer(new McpServerOptions(...), Console.OpenStandardInput(), Console.OpenStandardOutput()).RunAsync().GetAwaiter().GetResult()` の戻り値で終了。stdout には MCP 以外を一切書かない（起動ログは stderr: `UnityCSharpFastLSP.Server: MCP mode (standalone|proxy), workspace=<path>`）。
- 既存の LSP モード（`--mcp` なし）の挙動は一切変えない。

### 1.2 --print-config --mcp
`--print-config` と `--mcp` が両方あるとき、Claude Code / OpenCode 用の MCP 設定を出力して exit 0。`--proxy` / `--workspace` 指定があれば args に含める。
```json
{
  "mcpServers": {
    "unity-csharp-fast-lsp": {
      "command": "<exePath>",
      "args": ["--mcp", "--workspace", "<path>"]
    }
  }
}
```
exePath / workspace は `/` 区切り。JSON は手組みせず `JsonObject` で生成して正しくエスケープする。

### 1.3 ヘルプ
`HelpDescription` の MODES / OPTIONS に `--mcp`, `--workspace`, `--settings`, `--timeout` と、`--mcp --proxy` で FastLSP 非対応ツールが UnityCSharpLSP にフォールバックする旨、MCP 設定例を追記。

## 2. 完了条件
1. `dotnet build UnityCSharpFastLSP.sln -c Release` が警告 0 / エラー 0。
2. `UnityCSharpFastLSP.Server.exe --print-config --mcp --workspace D:/x` が妥当な JSON を出力する。
3. 既存テストがすべて通る。
