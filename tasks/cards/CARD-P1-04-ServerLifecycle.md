# [CARD-P1-04] Serverライフサイクルとホットパス統合

- **対象プロジェクト**: `src/UnityCSharpFastLSP.Server/`
- **依存**: CARD-P1-01, CARD-P1-02, CARD-P1-03
- **優先度**: 高（Phase 1 の中核）

## 1. 目的と背景
現在 `Program.cs` に仮実装されているベンチマーク用最小実装を解体・整理し、`Protocol`、`Syntax`、各 Provider を統合した堅牢かつ超低遅延な LSP サーバーを構築する。
特に、`didChange` 受信から構文診断パブリッシュ（`publishDiagnostics`）までのホットパスを最適化し、キャンセル処理とデバウンスを導入する。

## 2. 参照設計書
- `doc/design.md` セクション 6.2（ホットパス: didChange → 診断）、セクション 6.5（スレッドモデル）
- `doc/phase0-performance-comparison.md`

## 3. 実装要件

### 3.1 ドキュメントストア（DocumentStore）
- 開かれているドキュメントを URI キーで管理する（ロックフリーまたは並行コレクション）。
- `didOpen`:
  - ソース文字列を受領し、即座に `CsffSyntaxAdapter.Parse` を実行。
  - 最新スナップショットをストアに保存し、即座に構文診断を発行する。
- `didChange`:
  - 増分または全文変更（Full text or Incremental content changes）を適用。
  - 全文再パースを実行し、不変スナップショットを原子的（Interlocked / Copy-on-Write）に差し替える。
  - 診断パブリッシュタスクをトリガーする。
- `didClose`:
  - ドキュメントとスナップショットを破棄し、診断をクリア（空の診断配列を発行）する。

### 3.2 ホットパスのデバウンス & キャンセル（Latest-Wins）
- キー入力の連打に対応するため、5〜10ms のデバウンスを設けるか、最新のバージョン番号（Version）を追跡する。
- ドキュメント単位で `CancellationTokenSource` を管理し、新しい `didChange` が来たら直前の診断パブリッシュタスクを速やかにキャンセルする。
- パース・診断生成はワーカースレッドで非同期に実行し、メインの IO スレッドをブロックしない。

### 3.3 サーバーライフサイクル
- `initialize`: capabilities を返却（textDocumentSync=2 (Incremental) または 1 (Full), documentSymbolProvider=true）。
- `initialized`: 準備完了通知。
- `shutdown`: 正常終了準備（レスポンス null を返却）。
- `exit`: プロセスを exit 0 で終了。

## 4. 完了条件 (Definition of Done)
1. `UnityCSharpFastLSP.Server` が `Protocol`、`Syntax` のクラス群を利用してスッキリ構造化されている。
2. 標準入力・標準出力を介した JSON-RPC 通信で、VS Code やテストクライアントからの `initialize` → `didOpen` → `didChange` → `documentSymbol` → `shutdown` → `exit` が正常に通過する。
3. `dotnet build UnityCSharpFastLSP.sln -c Release` が警告 0 / エラー 0 でビルドできる。
