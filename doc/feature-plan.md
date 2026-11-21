# LSP 機能計画 & 実装可否マトリクス (Feature Plan)

UnityCSharpFastLSP における Language Server Protocol (LSP 3.17) 各機能の実装状況、および「サブミリ秒（0.1ms〜0.5ms）の超高速応答を維持できるか」に基づく実現可能性の分類です。

---

## 1. 3分類の定義と判断基準

| 区分 | 定義 | 主なアプローチ | 目標レイテンシ |
| :--- | :--- | :--- | :--- |
| **🟢 実装済み (Implemented)** | v0.1.0 時点で実装・テスト・ベンチマーク（`lsbench` 0.0% エラー）済みの機能。 | CST (具象構文木) 走査 + In-Memory `SymbolIndex` ハッシュ探索 + 軽量型推論 + Unity 静的辞書 | **0.01 ms 〜 0.5 ms** |
| **🟡 性能を維持したまま実装可能 (Feasible)** | 外部 DLL メタデータ解析や Roslyn セマンティックモデルを使わず、既存の CST 構造解析とインデックス探索のみでサブミリ秒で提供可能な機能。 | CST ノード走査、AST スパン計算、字句トークン解析、局所構文解析 | **0.05 ms 〜 1.0 ms** |
| **🔴 性能劣化するので実装できない (Infeasible)** | 外部アセンブリ（`UnityEngine.dll`, BCL 等）のメタデータロード、完全な型推論・多重オーバーロード解決・暗黙的型変換グラフ探索が必須となり、Roslyn 同等のメモリ消費（数百MB〜数GB）とレイテンシ（数十〜数百ms）が発生するため、FastLSP 単体では意図的に実装しない機能。<br>※これらは **Intelligent Hybrid Proxy (`--proxy`)** により、背後の Roslyn / UnityCSharpLSP へ自動委譲されます。 | Roslyn コンパイラセマンティクス、完全型バインディング、メタデータ DLL 読み込み | **10 ms 〜 500 ms** (FastLSP の設計思想に反するため単体非対応) |

---

## 2. LSP 機能別 分類マトリクス

### (1) ライフサイクル & ドキュメント同期 (Lifecycle & Sync)

| LSP メソッド | 分類 | レイテンシ (実績/見込) | 技術的アプローチ / 理由 | 単体モード | プロキシモード (`--proxy`) |
| :--- | :---: | :---: | :--- | :---: | :---: |
| `initialize` / `initialized` | 🟢 実装済み | < 0.2 ms | サーバー機能 (Capabilities) 返却 & 非同期バックグラウンドインデックス開始 | 自前応答 | FastLSP機能広告 + 上流起動 |
| `shutdown` / `exit` | 🟢 実装済み | < 0.1 ms | リソース解放とプロセス正常終了 | 自前応答 | 上流プロセスも連動終了 |
| `textDocument/didOpen` | 🟢 実装済み | 0.1 〜 0.5 ms | csff CST 構文解析 + シンボル抽出 + 即時構文診断 | 自前処理 | 自前処理 + 上流へ透過転送 |
| `textDocument/didChange` (インクリメンタル) | 🟢 実装済み | **< 30 µs (0.03 ms)** | メソッドブロック内編集のトークン差分シフト。構造変化時のみフルパース | 自前処理 | 自前処理 + 上流へ透過転送 |
| `textDocument/didClose` | 🟢 実装済み | < 0.05 ms | メモリキャッシュ解放 | 自前処理 | 自前処理 + 上流へ透過転送 |
| `textDocument/didSave` | 🟡 実装可能 | < 0.05 ms | 保存通知の受信およびファイル整合性チェック | 自前処理 | 自前処理 + 上流へ透過転送 |

---

### (2) ナビゲーション & シンボル探索 (Navigation & Symbols)

| LSP メソッド | 分類 | レイテンシ (実績/見込) | 技術的アプローチ / 理由 | 単体モード | プロキシモード (`--proxy`) |
| :--- | :---: | :---: | :--- | :---: | :---: |
| `textDocument/definition` (定義ジャンプ) | 🟢 実装済み | **0.26 ms** | スコープ内宣言探索 + `SymbolIndex` ハッシュ探索 + メンバーアクセス対象型解決 | 自前応答 | **FastLSP が高速返答 (0.26ms)** |
| `textDocument/declaration` (宣言ジャンプ) | 🟢 実装済み | **0.29 ms** | インターフェースや基底クラスの基底宣言への解決 | 自前応答 | **FastLSP が高速返答 (0.29ms)** |
| `textDocument/implementation` (実装ジャンプ) | 🟢 実装済み | 0.2 〜 0.4 ms | 基底型から派生型・実装メソッドへの逆引き (`_baseToDerivedTypes`) | 自前応答 | **FastLSP が高速返答 (0.3ms)** |
| `textDocument/typeDefinition` (型定義ジャンプ) | 🟢 実装済み | 0.2 〜 0.4 ms | 変数・パラメータの型シンボルへの直接ジャンプ | 自前応答 | **FastLSP が高速返答 (0.3ms)** |
| `textDocument/references` (参照検索) | 🟢 実装済み | **0.36 ms** | ワークスペース全ファイルの高速単語境界マッチング (`includeDeclaration` 対応) | 自前応答 | **FastLSP が高速返答 (0.36ms)** |
| `textDocument/documentHighlight` | 🟢 実装済み | 0.1 〜 0.2 ms | 同一ファイル内におけるシンボル出現箇所の瞬時ハイライト | 自前応答 | **FastLSP が高速返答** |
| `textDocument/documentSymbol` (アウトライン) | 🟢 実装済み | **0.18 ms** | CST 階層トラバース（名前空間 → クラス → メソッド/プロパティ） | 自前応答 | **FastLSP が高速返答** |
| `workspace/symbol` (ワークスペース全シンボル) | 🟡 実装可能 | 0.5 〜 1.5 ms | `SymbolIndex` に対するプレフィックス/部分一致/Fuzzyインメモリ検索 | 自前返答可能 | 上流未接続時は自前 / 接続時は上流 |

---

### (3) エディタ支援・補完・ヘルプ (Language Features & Completion)

| LSP メソッド | 分類 | レイテンシ (実績/見込) | 技術的アプローチ / 理由 | 単体モード | プロキシモード (`--proxy`) |
| :--- | :---: | :---: | :--- | :---: | :---: |
| `textDocument/completion` (コード補完) | 🟢 実装済み | **0.45 ms** | ドット補完（スコープ内型推論 `x.` + 継承メンバー + Unity静的辞書）、MonoBehaviourメッセージ、属性 | 自前応答 | **FastLSP が高速返答 (0.45ms)** |
| `textDocument/hover` (型・ドキュメント表示) | 🟢 実装済み | **0.43 ms** | シグネチャ定義 + 所属型 + Unity メッセージ解説 Markdown | 自前応答 | **FastLSP が高速返答 (0.43ms)** |
| `textDocument/signatureHelp` (引数ヘルプ) | 🟢 実装済み | 0.1 〜 0.3 ms | `(` や `,` トリガー時のパラメータ名・型・アクティブ引数インデックス追従 | 自前応答 | **FastLSP が高速返答** |
| `textDocument/semanticTokens/full` (配色) | 🟢 実装済み | **0.43 ms** | CST トークン走査による型・メソッド・変数・キーワードの差分エンコード | 自前応答 | **FastLSP が高速返答** |
| `textDocument/formatting` (全体整形) | 🟢 実装済み | **0.30 ms** | 軽量ステートマシンによるインデント正規化 (`FastFormatter`) | 自前応答 | **FastLSP が高速返答** |
| `textDocument/rangeFormatting` (範囲整形) | 🟡 実装可能 | 0.1 〜 0.2 ms | 指定行範囲のみに `FastFormatter` を適用 | 未広告 | 自前実装可能 |
| `textDocument/onTypeFormatting` (タイプ時整形) | 🟡 実装可能 | < 0.05 ms | `;` や `}` 入力時の直前行/ブロックの即時インデント整形 | 未広告 | 自前実装可能 |
| `textDocument/foldingRange` (コード折りたたみ) | 🟡 実装可能 | 0.05 〜 0.1 ms | CST の `{` 〜 `}`, `#region` 〜 `#endregion` スパン返却 | 未広告 | 自前実装可能 |
| `textDocument/selectionRange` (選択範囲拡張) | 🟡 実装可能 | < 0.05 ms | CST ノード親方向への瞬時トラバース (Token → Expr → Stmt → Member) | 未広告 | 自前実装可能 |
| `textDocument/linkedEditingRange` (同名同時編集)| 🟡 実装可能 | < 0.05 ms | 同一スコープ内のローカル変数/識別子の一括編集レンジ | 未広告 | 自前実装可能 |
| `textDocument/semanticTokens/range` (範囲配色) | 🟡 実装可能 | 0.1 〜 0.2 ms | 画面表示範囲行のトークンのみを部分走査 | 未広告 | 自前実装可能 |

---

### (4) 診断・コードアクション・リファクタリング (Diagnostics & Refactoring)

| LSP メソッド / 機能 | 分類 | 単体での実現可能性 / 理由 | 単体モード | プロキシモード (`--proxy`) |
| :--- | :---: | :--- | :---: | :---: |
| 構文診断 (`CSFF001`, `CSFF002`) | 🟢 実装済み | **5 ms デバウンス / 0.1 ms 計算**。括弧不整合や構文崩壊を即時検知 | 自前配信 | 自前配信 |
| コンパイラ意味診断 (`CS0029` 型不一致, `CS0103` 未定義等) | 🔴 **性能劣化のため不可** | **全メタデータ DLL の型解決・暗黙型変換検証が必須**。毎キーストロークで Roslyn 相当（数十〜数百ms、1GB超メモリ）が必要となりコンセプト崩壊 | 非対応 | **上流 Roslyn から透過受信** |
| `textDocument/codeAction` (クイックフィックス) | 🔴 **性能劣化のため不可** | `using` 補完やメソッドスタブ生成、Roslyn アナライザーによる修正提案は重い意味モデルに依存 | 非対応 (広告OFF) | **上流 Roslyn へ転送** |
| `textDocument/rename` (安全な一括リネーム) | 🔴 **性能劣化のため不可** | 外部 DLL 境界やオーバーロード、インターフェース競合を検証しない単純文字列置換はコード破損の危険大 | 非対応 (広告OFF) | **上流 Roslyn へ転送** |
| `textDocument/prepareRename` | 🔴 **性能劣化のため不可** | リネーム可能シンボルかどうかの厳密な意味検証 | 非対応 (広告OFF) | **上流 Roslyn へ転送** |
| `textDocument/inlayHint` (型/引数名インライン表示) | 🔴 **性能劣化のため不可** | 複雑な式・多重オーバーロードメソッドの戻り値型・引数名の完全決定が必要 | 非対応 (広告OFF) | **上流 Roslyn へ転送** |
| `textDocument/codeLens` (参照数・テスト実行表示) | 🔴 **性能劣化のため不可** | バックグラウンドでの常時全シンボル相互参照計算・キャッシュ維持が高負荷 | 非対応 (広告OFF) | **上流 Roslyn へ転送** |

---

### (5) 階層ツリー & 高度な解析 (Hierarchies & Unity Specifics)

| LSP メソッド / 機能 | 分類 | 技術的アプローチ / 理由 | 単体モード | プロキシモード (`--proxy`) |
| :--- | :---: | :--- | :---: | :---: |
| クラス継承ツリー解決 (`Player : BaseCharacter`) | 🟢 実装済み | CST から基底型名を抽出し `SymbolIndex` のメンバーを再帰マージ（深さ数階層で 2〜5 µs） | 自前解決 | 自前解決 |
| Unity API 静的インメモリ辞書 | 🟢 実装済み | `Transform`, `GameObject`, `Vector3`, `Mathf`, `Physics` 等の頻出型スタブを事前インメモリ化 (0.01 ms) | 自前解決 | 自前解決 |
| `callHierarchy/incomingCalls` / `outgoingCalls` | 🟡 実装可能 | 同一ワークスペース内の識別子呼び出し文を構文木から抽出（構文的近似アプローチで < 2 ms） | 未広告 | 上流接続時は上流 |
| `typeHierarchy/supertypes` / `subtypes` | 🟡 実装可能 | 既存の `GetAllBaseTypes` および `_baseToDerivedTypes` インデックスから即座に階層ツリー構築可能 (< 0.2 ms) | 未広告 | 上流接続時は上流 |
| Unity YAML (Prefab / Scene / Mat) 解析 | 🔴 **性能劣化のため不可** | 数百MB〜数GBの YAML GUID 参照解決は言語サーバーの責務外（Unity Asset Daemon 向け） | 非対応 | 非対応 |

---

## 3. Intelligent Hybrid Proxy (`--proxy`) による理想的な役割分担

UnityCSharpFastLSP は、**「日常的な打鍵・閲覧の 95% を占める高速機能」** と **「重厚な意味解析を要する 5% の機能」** を完璧に分離するアーキテクチャを提供します。

```mermaid
flowchart TD
    Client[VS Code / Cursor / Neovim] -->|LSP Request| Proxy[UnityCSharpFastLSP Proxy Router]
    
    subgraph FastPath ["Fast Path (Sub-millisecond: 0.1 ~ 0.5 ms)"]
        Proxy -->|completion, hover, definition| Engine[Fast Engine (csff CST + In-Memory Index)]
        Proxy -->|declaration, implementation, references| Engine
        Proxy -->|formatting, semanticTokens, signatureHelp| Engine
        Proxy -->|didChange (30 us)| Engine
    end
    
    subgraph HeavyPath ["Heavy Path (Semantic / Roslyn: 50 ~ 500 ms)"]
        Proxy -->|rename, codeAction, prepareRename| Roslyn[Upstream UnityCSharpLSP / Roslyn]
        Proxy -->|CS0029, CS0103 compiler diagnostics| Roslyn
    end
    
    Engine --> Client
    Roslyn --> Client
```

### なぜこの切り分けが最強なのか？
1. **タイピングの完全なゼロレイテンシ化**:
   コード補完、定義ジャンプ、ホバー、セマンティックハイライト、引数ヘルプは FastLSP が **0.1〜0.4 ms** で即答するため、Roslyn 特有の「補完ポップアップが出るまでの引っ掛かり」が一切ありません。
2. **安全性の担保**:
   プロジェクト全体に影響を与える「一括リネーム」や「コード修正 (Quick Fix)」のみを Roslyn に任せることで、型安全性を 100% 担保できます。
3. **過大広告の排除**:
   FastLSP 単体で動作させる場合は、動作しない機能（codeAction や rename など）の Capability をクライアントに **一切広告しません**。そのため、エディタ側で無効なコマンドが呼び出されてエラーになる問題を防ぎます。
