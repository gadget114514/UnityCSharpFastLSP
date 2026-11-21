# Worker Instruction: CARD-P1-05 Phase 1 統合テスト

## 目的
`tests/UnityCSharpFastLSP.Tests/` に Phase 1 の網羅的な単体・統合テストを追加し、仕様準拠を検証する。

## 要件
1. テスト項目:
   - JSON-RPC フレーミング、シリアライズ/デシリアライズ。
   - `didOpen` / `didChange` と構文診断（正常コード、不完全コード）。
   - `documentSymbol` の階層構造、日本語・絵文字コメント時の位置ズレ防止。
   - サーバーの起動から終了までのライフサイクル。
2. `dotnet test tests/UnityCSharpFastLSP.Tests/UnityCSharpFastLSP.Tests.csproj -c Release` がすべてパスすること。
