# UnityCSharpFastLSP 実装タスクカード & Hancho 運用ガイド

本ディレクトリでは、`UnityCSharpFastLSP`（設計書: `doc/design.md`）の実装を安全・高速に進めるためのタスクカードおよび、現場監督 CLI である **Hancho (`hancho-cli`)** と連携した実行定義を管理します。

---

## 1. 実装状況とタスクカード一覧

設計書 `doc/design.md` の Phase 1〜Phase 4 およびインクリメンタルパースをすべて実装完了しました。

| フェーズ | 対象プロジェクト | 主な実装内容 | 状態 |
| :--- | :--- | :--- | :---: |
| **Phase 1** | `Protocol/`, `Syntax/`, `Server/` | JSON-RPC 2.0 DTO (Source-generated), Content-Length フレーミング, CST 構文診断, 階層型 DocumentSymbol, LSP ライフサイクル | **完了** |
| **Incremental** | `Syntax/` (`IncrementalParser`) | メソッド・ブロック内編集の高速差分パース & トークンオフセットシフト合成（< 30us） | **完了** |
| **Phase 2** | `Semantics/`, `Workspace/` | `SymbolIndex` (文字列相互化テーブル, ファイル単位の原子的差分索引更新), `Assets/`/`Packages/` 並列インデクサー, `definition`/`references` | **完了** |
| **Phase 3** | `Semantics/` | `DocumentSemanticModel`, `Hover` (Unity ライフサイクルドキュメント), `Completion` (Unity メッセージ・属性補完), `UnityRules` | **完了** |
| **Phase 4** | `Tests/`, `Phase0.Bench/` | 全 11 件の統合テスト, ウォーム状態の 10 倍高速化ゲートベンチマーク（120〜7000倍達成） | **完了** |
| **MCP-00** | `tasks/cards/CARD-MCP-00-Overview.md` | MCP モードの全体方針・互換契約（全カード共通） | 方針確定 |
| **MCP-01** | `tasks/cards/CARD-MCP-01-ToolTable.md` | MCP ツール表（38 ツール、`Mcp/McpToolTable.cs`） | 実装済 |
| **MCP-02** | `tasks/cards/CARD-MCP-02-Bridge.md` | MCP サーバー / LSP ブリッジ（`Mcp/McpServer.cs`, `LspLoopbackClient.cs`, `DocumentSyncTracker.cs`） | 実装済 |
| **MCP-03** | `tasks/cards/CARD-MCP-03-ProgramWiring.md` | `Program.cs` への `--mcp` / `--proxy` / `--workspace` 等の配線 | 実装済 |
| **MCP-04** | `tasks/cards/CARD-MCP-04-Tests.md` | MCP テスト（`tests/UnityCSharpFastLSP.Tests/Mcp/`） | 実装済 |
| **MCP-05** | `tasks/cards/CARD-MCP-05-Docs.md` | MCP ドキュメント（`doc/mcp.md`, README, SKILL.md, `--help`） | 実装中 |

MCP モードの利用者向け説明は [doc/mcp.md](../doc/mcp.md) を参照してください。

---

## 2. Hancho によるパイプライン実行・検証

Hancho を使用して、全レイヤーのビルド、テスト、および 10 倍ゲートベンチマークを一括自動実行できます。

### 2.1 環境準備 (.NET 11)
本プロジェクトは .NET 11 SDK を使用します（`global.json` で指定）。
```powershell
$env:PATH = "C:\Users\bluen\dotnet11;$env:PATH"
$env:DOTNET_ROOT = "C:\Users\bluen\dotnet11"
```

### 2.2 全パイプラインの実行
```powershell
# キューに全パイプラインジョブを登録
hancho-cli.exe --data-dir .hancho taskqueue add --file tasks/hancho/jobs-all-phases.json --taskqueue fastlsp-full-pipeline

# キューの完了まで待機
hancho-cli.exe --data-dir .hancho taskqueue wait fastlsp-full-pipeline --since 0 --timeout-ms 300000

# 状態確認
hancho-cli.exe --data-dir .hancho taskqueue list fastlsp-full-pipeline --json
```

---

## 3. ベンチマーク検証結果 (Phase 4 ゲート)

詳細は [doc/20261121-phase4-results.md](file:///D:/wsgit/UnityCSharpFastLSP/doc/20261121-phase4-results.md) を参照してください。

- **didChange → 診断**: **p50 = 25.3 us**（目標 306.0 us に対して **約 120 倍高速**）
- **Completion**: **p50 = 10.7 us**（目標 1.379 ms に対して **約 1280 倍高速**）
- **Hover**: **p50 = 0.1 us**（目標 73.0 us に対して **約 7300 倍高速**）
