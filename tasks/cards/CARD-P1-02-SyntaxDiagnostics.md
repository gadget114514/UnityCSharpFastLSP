# [CARD-P1-02] Syntax層・不変スナップショットと構文診断

- **対象プロジェクト**: `src/UnityCSharpFastLSP.Syntax/`
- **依存**: `thirdparty/csff` (Csff.Parser / Csff.Lexer)
- **優先度**: 高（didChange→診断のホットパス）

## 1. 目的と背景
csff の高速パース結果（`SyntaxTree`）は可変プール再利用型であるため、パース直後に不変なスナップショット（`DocumentSnapshot`）に素早くコピーし、読み手をロックフリーにする。
また、Phase 0 の検証結果に基づき、CST の `IsComplete` 信号（および `IncompleteMember` / `BadToken` / `RecoveredTokens`）から直接構文診断（`Diagnostic`）を生成する機能を Syntax 層に実装する。

## 2. 参照設計書
- `doc/design.md` セクション 4（csff の実態と利用方法）、セクション 6.2（ホットパス: didChange → 診断）、セクション 8（LSP機能と根拠）
- `doc/20261121-phase0.md` セクション 2（CST ダンプ検証）

## 3. 実装要件

### 3.1 `DocumentSnapshot` と `CsffSyntaxAdapter` の強化
- `DocumentSnapshot`:
  - ソース文字列、`LineMap`、ノード配列 (`SyntaxNodeInfo[]`)、トークン配列 (`SyntaxTokenInfo[]`)、`IsComplete` (bool)、バージョン番号を保持する。
  - UTF-16 オフセットから (Line, Character) への変換、および逆変換を `LineMap` 経由で O(log n) で行えること。
  - 日本語や絵文字などのサロゲートペアが含まれていても、UTF-16 オフセットが LSP の `Position.character` と完全一致すること。
- `CsffSyntaxAdapter`:
  - `ParseSession`（プールされた `SyntaxTree`）を使用して再利用パースを行う。
  - `UNITY_EDITOR` 等の `#define` seeded symbols を注入可能にする。
  - パース直後に必要なノード・トークン情報を抽出し、csff ツリーを即座にプールへ返却する。

### 3.2 構文診断（Syntax Diagnostics）の生成
- CST の状態から、軽量に構文診断リスト（`Diagnostic[]` または `ReadOnlyMemory<Diagnostic>`）を生成する:
  - `IsComplete == false`: 構文不完全エラーを生成。
    - 不完全な末尾位置、あるいは `BadToken` / `IncompleteMember` の位置を特定して範囲を設定。
    - メッセージ: "Syntax error: incomplete syntax element" 等。
  - `RecoveredTokens > 0`: 回復処理が発生したトークン位置に警告またはエラーを付与。
  - セマンティック解決（型解決やバインディング）は一切通さず、パース直後の CST だけで完結させる（< 100us で完了すること）。

### 3.3 包含ノードの二分探索
- 指定されたカーソル位置（Position / UTF-16 offset）を含む最も狭いノード（最小包含ノード）を高速に特定するメソッド `FindNodeAt(int utf16Offset)` を実装する。
- ノード一覧がオフセット順にソートされていることを利用し、二分探索で O(log n) で探索できること。

## 4. 完了条件 (Definition of Done)
1. `DocumentSnapshot` から構文診断を取得するメソッド（例: `GetDiagnostics()`）が実装されている。
2. 不完全なソース（例: `public class C { void M(`）に対して落ちずに構文診断が出力される。
3. 日本語コメント・サロゲートペアを含むソースで `LineMap` の座標変換が完全一致する。
4. `dotnet test -c Release` で単体テストが全件パスする。
