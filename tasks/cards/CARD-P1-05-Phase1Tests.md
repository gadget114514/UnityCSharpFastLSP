# [CARD-P1-05] Phase 1 統合テストと検証

- **対象プロジェクト**: `tests/UnityCSharpFastLSP.Tests/`
- **依存**: CARD-P1-01, CARD-P1-02, CARD-P1-03, CARD-P1-04
- **優先度**: 高（Phase 1 完了ゲート）

## 1. 目的と背景
Phase 1 で実装された LSP サーバー機能群（JSON-RPC 2.0、`initialize`、`didOpen`、`didChange`、構文診断、`documentSymbol`）が、仕様および設計書の要件を満たしているかを網羅的に検証する統合テストを構築・実行する。

## 2. 参照設計書
- `doc/design.md` セクション 9（Phase 1）、セクション 10（計測と検証）
- `doc/20261121-phase0.md`

## 3. 実装要件

### 3.1 テストケース項目
1. **LSP メッセージフレーミング & DTO**:
   - `Content-Length` フレーミングの正確な送受信テスト。
   - 不正なヘッダー・改行コード（`\r\n` vs `\n`）の回復テスト。
2. **ライフサイクルシーケンス**:
   - `initialize` → `initialized` → `shutdown` → `exit` のフロー検証。
3. **didOpen / didChange と構文診断**:
   - 正常な Unity C# コードで診断 0 件が publish されること。
   - 構文エラー（`public class Broken { void M(` 等）で適切に構文診断が発行されること。
   - エラー状態からコードを修正した `didChange` で診断が 0 件に更新されること。
4. **DocumentSymbol 階層検証**:
   - 名前空間、クラス、メソッド、フィールド、プロパティが正しい階層で取得できること。
   - 日本語コメントや絵文字（サロゲートペア）が含まれるソースで `Range` / `SelectionRange` の位置ズレが発生しないこと。
5. **プロセス間通信 / インプロセス通信テスト**:
   - メモリ内パイプ（`Pipe` / `MemoryStream`）を用いて、実際のサーバーインスタンスとメッセージを送受信するテストハーネス。

## 4. 完了条件 (Definition of Done)
1. `dotnet test UnityCSharpFastLSP.sln -c Release` を実行し、全テストがパスすること（失敗 0）。
2. テスト実行時間が短く（全体で数秒以内）、CI や Hancho キューで高速に完走すること。
3. Phase 1 の完了レポートが作成可能であること。
