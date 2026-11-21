# [CARD-P1-01] Protocol基盤とJSON-RPC 2.0トランスポート

- **対象プロジェクト**: `src/UnityCSharpFastLSP.Protocol/`
- **依存**: なし（純粋なC# / .NET 11）
- **優先度**: 高（Phase 1 の基盤）

## 1. 目的と背景
`UnityCSharpFastLSP` のウォーム時レイテンシ 10 倍を支えるため、リフレクション不要な Source-generated JSON シリアライザ、アロケーションを抑えた Content-Length フレーミングパーサー、標準入出力（stdout は LSP 通信専有、stderr はログ専有）の厳格なトランスポート層を `Protocol` プロジェクトに実装する。

## 2. 参照設計書
- `doc/design.md` セクション 5（アーキテクチャ）、セクション 6.4（アロケーション規律）、セクション 6.5（スレッドモデル）

## 3. 実装要件

### 3.1 LSP DTO 定義 & Source-generated `JsonSerializerContext`
- `System.Text.Json.Serialization` の `JsonSourceGenerationOptions` を使用する。
- 以下の DTO を定義する（すべて record または readonly struct）:
  - `Position`: `line` (int), `character` (int)
  - `Range`: `start` (Position), `end` (Position)
  - `Location`: `uri` (string), `range` (Range)
  - `Diagnostic`: `range` (Range), `severity` (DiagnosticSeverity), `code` (string?), `source` (string), `message` (string)
  - `PublishDiagnosticsParams`: `uri` (string), `version` (int?), `diagnostics` (IReadOnlyList<Diagnostic>)
  - `DocumentSymbol`: `name` (string), `detail` (string?), `kind` (SymbolKind), `range` (Range), `selectionRange` (Range), `children` (List<DocumentSymbol>?)
  - `InitializeParams`, `InitializeResult`, `ServerCapabilities` 等の基本ライフサイクル DTO
- `LspJsonSerializerContext : JsonSerializerContext` を定義し、上記すべての型に `[JsonSerializable]` を付与。ホットパスでリフレクションが一切発生しないこと。

### 3.2 Content-Length フレーミング & ストリームトランスポート
- `LspMessageReader`:
  - `Stream` から `Content-Length: \d+\r\n\r\n` ヘッダーを解析する。
  - ボディの読み込みには `ArrayPool<byte>.Shared` を使用し、無駄なヒープ確保を避ける。
  - 不正なヘッダーや EOF を安全にハンドリングする。
- `LspMessageWriter`:
  - スレッドセーフに stdout への書き込みを行う（`WriteLock` または単一ライターチャネル）。
  - `Content-Length: {len}\r\n\r\n{json}` を UTF-8 で一括書き込みし、`Flush` する。
  - stdout には JSON-RPC メッセージ以外を一切出力しない。デバッグ/ログはすべて stderr へ出力する。

### 3.3 メッセージディスパッチャ基盤
- `JsonRpcDispatcher`:
  - JSON-RPC 2.0 の `Request`, `Notification`, `Response` を判別。
  - 既知のメソッド（`initialize`, `initialized`, `shutdown`, `exit`, `textDocument/didOpen`, `textDocument/didChange`, `textDocument/didClose`, `textDocument/documentSymbol` 等）を適切なハンドラにルーティングする。

## 4. 完了条件 (Definition of Done)
1. `src/UnityCSharpFastLSP.Protocol/` 配下に DTO、`LspJsonSerializerContext`、`LspMessageReader`、`LspMessageWriter`、`JsonRpcDispatcher` が実装されている。
2. `dotnet build -c Release` で警告 0 / エラー 0（`TreatWarningsAsErrors=true` 準拠）。
3. トランスポートと DTO のシリアライズ/デシリアライズに対する単体テストが `UnityCSharpFastLSP.Tests` に追加され、全件パスする。
4. ホットパスでリフレクション呼び出しが発生していないこと。
