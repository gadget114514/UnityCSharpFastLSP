# Worker Instruction: CARD-P1-04 Server ライフサイクルとホットパス統合

## 目的
`src/UnityCSharpFastLSP.Server/` を整理し、Protocol・Syntax 層を結合した高速 LSP サーバーを構築する。

## 要件
1. `Program.cs` の解体・リファクタリング:
   - `DocumentStore`: URI ごとの最新 `DocumentSnapshot` の保持。
   - `LspServer`: `initialize`, `initialized`, `shutdown`, `exit`, `textDocument/didOpen`, `textDocument/didChange`, `textDocument/didClose`, `textDocument/documentSymbol` をハンドリング。
2. ホットパス最適化:
   - `didChange` 受信時に全文再パースを行い、不変スナップショットを安全に差し替える。
   - ドキュメントごとに `CancellationTokenSource` を管理し、古い診断タスクを速やかにキャンセル（latest-wins）。
   - デバウンスまたは即時パースで `textDocument/publishDiagnostics` を stdout へ送信。
3. `dotnet build src/UnityCSharpFastLSP.Server/ -c Release` がパスすること。
