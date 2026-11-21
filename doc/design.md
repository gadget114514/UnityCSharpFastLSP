# Unity 専用 LSP サーバー 設計書（csff ベース）

文書バージョン: 2.1
作成日: 2026-11-21
基盤技術: csff（F# / .NET 11, Native AOT） / C# / LSP (JSON-RPC 2.0)

## 1. 概要・目的

既存の UnityCSharpLSP に対して、**ウォーム状態の per-request レイテンシ**で 10 倍の性能を達成する Unity 専用 LSP を構築する。UnityCSharpLSP はサーバを常驻させ、起動・初期化コストを先に払うことで高速性を担保しているため、比較は両サーバを十分ウォームアップした状態で行う。

10 倍のゲート対象は以下の 3 指標のみである。

- didChange → publishDiagnostics レイテンシ
- completion 応答時間
- hover 応答時間

ワークスペース初期インデックス時間と大量ファイル一括処理スループットは**ゲート外**とし、参考値として計測・記録するに留める。常驻化は「起動コストを先に払う」だけでリクエストごとの計算自体は速くならないため、10 倍はリクエストごとの仕事量を削ることで達成する（6.2 以降）。

解析対象は **C# ソースファイル（*.cs）のみ**とし、.meta / .asmdef / シーン / プレハブ等のアセット解析はスコープ外とする。これによりアセット監視・DB 層・GUID 解決・AssetDaemon との IPC に起因するコストを全て削除する。

## 2. 決定事項と前提

| 項目 | 決定 |
|---|---|
| 実装言語 | C# / .NET（旧設計の C++20 + tree-sitter は破棄） |
| 構文解析 | csff（`thirdparty/csff`）をアセンブリ参照 |
| セマンティック | 軽量近似。UnityCSharpLSP の内部構造（Protocol / Semantics / Workspace / Server の分担）に型の形を合わせる |
| 性能目標 | ウォーム状態の per-request レイテンシ（didChange→診断 / completion / hover）で現状の 1/10。初期インデックス・一括処理はゲート外 |
| 対象ファイル | *.cs のみ |

### Unity ワークスペースの認識範囲

Unity ワークスペースは「フォルダ構造としての判定」と「*.cs の走査」のみを扱う。

- ルート判定: `Assets/` + `Packages/` + `ProjectSettings/ProjectVersion.txt` の存在で Unity プロジェクトとみなす。
- 走査対象: `Assets/**/*.cs` と `Packages/**/*.cs`（PackageCache 配下を含む）。
- `initializationOptions.unityProjectPath` を受領し、`workspace/didChangeWatchedFiles` では .cs ファイルのみ差分更新する。

明示的な範囲外（10 倍レイテンシを優先して削除）:

- `.asmdef` によるアセンブリ境界・参照可視性（全 .cs をフラットに索引化するため、別アセンブリの internal / private も見える）
- `.meta` GUID、シーン / プレハブ参照、未使用スクリプト検出
- `ProjectVersion.txt` による API バージョンヒント、`packages-lock.json` の解決

このため UnityCSharpLSP と比べると Unity アセット連携機能は後退するが、per-request レイテンシの 10 倍達成を優先する。

### 前提条件（ブロッカー）

- 開発環境にインストール済みの SDK は 8.0 / 9.0 / 10.0 のみ（最大 `10.0.203`）。csff は `Directory.Build.props` で `net11.0` をターゲットとしており、**着手前に .NET 11 SDK の導入が必要**。
- `thirdparty/csff` は独自 `.git` を持つ通常ディレクトリであり、`.gitmodules` には tree-sitter-c-sharp しか登録されていない。ProjectReference のまま使用するか submodule 化するかを着手段階で決める。

## 3. 結論（重要）

csff にはセマンティックモデル（シンボル解決・型推論・バインダー）が存在しないため、**「csff のセマンティックモデルに寄せる」ことはできない**。寄せるのは UnityCSharpLSP の型の形・責務分担のみである。Roslyn の `SemanticModel` は Roslyn 固有の `SyntaxTree` / `Compilation` を要求するため、csff の CST では流用不可である。

## 4. csff の実態と利用方法

- F# 製ライブラリ群（`Csff.Lexer` / `Csff.Parser` / `Csff.Printer`）。`net11.0` ターゲット、Native AOT 対応。
- エントリポイント:
  - `Csff.Parser.Parser.parse(source: string, tree: SyntaxTree) : unit`
  - `Csff.Parser.Parser.parseWith(symbols: string[], source: string, tree: SyntaxTree) : unit`
    - `#define` シンボルを注入可能。Unity の `UNITY_EDITOR` 等を渡せる。
- CST 読み取り（公開 API）:
  - `tree.Node(i)`, `tree.Token(i)`, `tree.ChildRange(i)`, `tree.SourceSpan(i)`
  - `tree.NodeCount`, `tree.TokenCount`, `tree.IsComplete`, `tree.RecoveredTokens`
- `NodeKind` / `TokenKind` の数値は **Roslyn `SyntaxKind` と同一**（`IdentifierName = 8616`, `InvocationExpression = 8634` 等）。自前 enum も同じ値にすれば、UnityCSharpLSP 側の知識をそのまま活かせる。
- `Token.Start` / `Token.Length` は C# string の UTF-16 コード単位であり、**LSP の `Position.character` と一致**（サロゲートペアも一致）。
- `SyntaxTree` は可変・プール再利用型で、`Parser.parse` が内部で `Reset()` する。**パース直後に配列を読み出して不変スナップショットへコピー**し、csff ツリーはプールへ戻す。
- インクリメンタルパース API は存在しないため、didChange ごとに全文再パースする（csff はリポジトリ全体を既存フォーマッタの 20 倍で処理する実績があり、1 ファイルの再パースは十分高速）。
- エラー回復は `IncompleteMember` / `BadToken` / `IsComplete` / `RecoveredTokens` により限定的に機能する。

## 5. アーキテクチャ

```
UnityCSharpFastLSP.sln
Directory.Build.props                    net11.0 / nullable / latest
thirdparty/csff/                          Csff.Parser / Csff.Lexer を参照
src/UnityCSharpFastLSP.Protocol/          JSON-RPC 2.0, LSP DTO, Dispatcher
src/UnityCSharpFastLSP.Syntax/            csff アダプタ, DocumentSnapshot, LineMap  ← csff 依存はここだけ
src/UnityCSharpFastLSP.Semantics/         Symbol, SymbolIndex, DocumentSemanticModel, Binder, UnityRules
src/UnityCSharpFastLSP.Workspace/         Workspace, *.cs 走査, ファイル監視
src/UnityCSharpFastLSP.Server/            Program.cs, 依存注入, Providers
tests/UnityCSharpFastLSP.Tests/           xUnit
```

依存方向: `Server → Protocol → {Syntax, Semantics, Workspace} → Syntax → csff`。
`Semantics` は csff 型に直接触れず、自前の `SyntaxKind` / `SyntaxNodeInfo` 経由で CST を受け取る。

## 6. 10 倍性能を実現する設計

### 6.1 コールドパス（ゲート外・実装はするが 10 倍の評価対象にしない）

比較相手が常驻済みであるため、AOT や並列初期索引による起動高速化は 10 倍の点数にならない。以下は「起動を遅くしない」ための実装とし、ゲートには含めない。

- `Channel<string>` + 並列ワーカーで `.cs` を走査し、**パースとシンボル抽出を 1 パスで実施**（2 回走査しない）。
- `SyntaxTree` インスタンスをワーカーごとにプールし、csff の `SharedBuckets` と合わせて配列確保を最小化。
- シンボルを struct + int ID で保持する。
  - `Symbol { nameId, kind, containingTypeId, signature, start, length, flags }`
  - 識別子は相互化テーブル（`string → int`）で O(1) 比較。
- 逆引きは `Dictionary<int, int[]>`（名前 ID → シンボル ID 列）、実体は `Symbol[]` のフラット配列。
- Native AOT publish は任意（起動のみに寄与し、ウォーム時の per-request レイテンシにはほぼ影響しない）。

なお常驻中は、この索引をメモリ上に保持し続け、リクエストごとに再構築しないことが前提である。

### 6.2 ホットパス（didChange → 診断）

- 変更ファイルのみ csff で全文再パースする。**この全文再パースが didChange→診断のクリティカルパスに乗る**。csff はインクリメンタルパース API を持たないため、ウォーム比較ではここが最大のリスク因子となる（11 節参照）。Phase 0 で最初に計測する。
- パース直後にノード / トークン配列を不変スナップショットへコピーする。
  - `(Source, NodeInfo[], TokenInfo[], LineMap, Version)`
  - csff ツリーは可変・プール再利用型であるため、コピーによって読み手をロックフリーにする。
- 診断は **CST から直接生成**する（`BadToken` / `IncompleteMember` / `IsComplete`）。セマンティック解析を通さない。
- シンボル索引は**そのファイルの分だけ差し替え**（旧シンボル ID 削除 + 新規追加）。O(ファイル内シンボル数)。
- 5〜10 ms のデバウンスでキー入力を合体し、バージョンごとに `CancellationToken` を発行して古い診断を破棄する（latest-wins）。

### 6.3 completion / hover

- スナップショットに**ノードのトークン範囲のソート配列**を持たせ、offset から二分探索で最小包含ノードを特定し、`Parent` チェーンで上位へ辿る。O(log n)。
- スコープ解決は enclosing method / class / namespace の**部分木だけ**を走査する（ファイル全体を読まない）。
- 名前解決は相互化済み ID の `Dictionary` 参照のみ。オーバーロード解決・完全な型推論は行わない。
- 結果を `(Version, Position, RequestKind)` でメモ化し、編集時に無効化する。

### 6.4 アロケーション規律（全経路共通）

- ホットパスで LINQ / ラムダ / 一時 `List<T>` を禁止し、`ArrayPool<T>` と struct を使用。
- LSP DTO は source-generated `JsonSerializerContext`（リフレクション零）。
- Content-Length フレーミングは `ArrayPool<byte>` で手動実装。
- 索引は copy-on-write でスワップし、読み手はロック取得なし（書き手のみ同期）。

### 6.5 スレッドモデル

- IO スレッドは JSON-RPC の読み書きのみ。パース・索引更新・要求処理はワーカープールが実行。
- ドキュメント単位で「最新バージョン勝ち」。古いワーカーの処理はキャンセル。
- stdout は LSP 通信専有。ログは stderr に出力（Native AOT でも維持）。

## 7. セマンティックモデル（軽量近似）

UnityCSharpLSP の内部構造と同じ概念を C# で再定義する（正確な型名は移植時に合わせる）。

| 型 | 責務 |
|---|---|
| `Symbol` | kind / name / qualifiedName / containingType / signature / Location / static / Unity 属性フラグ |
| `SymbolIndex` | ワークスペース全体の名前 → 定義一覧と逆引き（参照搜索用） |
| `DocumentSemanticModel` | 位置から `SyntaxNodeInfo` を取得し、スコープ内シンボルを解決 |
| `Binder`（軽量） | ローカル変数 → パラメータ → フィールド / プロパティ → 型名 → 基底型 の順で解決 |
| `UnityRules` | ライフサイクルメソッド検証、`[SerializeField]`、`GetComponent<T>()` 型推論 |

対応範囲外（明記）: ジェネリクス制約、LINQ、拡張メソッド、Source Generator、外部 DLL メタデータの完全解析、アセット GUID 連携。

## 8. LSP 機能と根拠

| 機能 | 根拠 |
|---|---|
| diagnostics | CST の `BadToken` / `IncompleteMember` / `IsComplete` + UnityRules |
| documentSymbol / workspaceSymbol | CST の宣言ノード + SymbolIndex |
| definition / references | SymbolIndex（名前一致 + スコープ絞り込み） |
| hover | Symbol のシグネチャ + Trivia から抽出した XML ドキュメントコメント |
| completion | 構文コンテキスト + SymbolIndex + ライフサイクル / 属性候補 |

データフロー: `didChange → SyntaxAdapter.Parse → DocumentSnapshot 差し替え → SymbolIndex 部分更新 → デバウンス → Provider → publishDiagnostics / 応答`

## 9. 実装フェーズ

1. **Phase 0（Go/No-Go 判定）**: ソリューション雛形、.NET 11 SDK 導入、csff 参照。Unity スクリプトを csff でパースし CST をダンプして宣言ノードの取り方を検証。並行してウォーム状態のベンチハーネスを用意し、**csff の 1 ファイル再パース時間が「現状 / 10」に収まるかを先に測定**する。収まらない場合は 11 節のインクリメンタル API 追加を前提に計画を再構築する。
2. **Phase 1**: JSON-RPC トランスポート、`initialize` / `didOpen` / `didChange`、構文診断、documentSymbol。
3. **Phase 2**: Workspace インデックス（*.cs 並列走査・相互化・逆引き）、definition / references。常驻中の索引保持と差分更新を実装。
4. **Phase 3**: `DocumentSemanticModel` + 軽量 Binder、hover / completion、UnityRules。
5. **Phase 4**: ウォーム状態の per-request ベンチによる 10 倍検証、キャンセル / デバウンスチューニング。必要なら csff フォークによる差分再パースを組み込む。

## 10. 計測と検証

- lsbench 相当のベンチハーネスを新規作成し、Unity 想定の合成 *.cs コーパス（サイズ混在・N ファイル）で計測する。
- **比較条件を揃える**: 現状実装・本実装の両方を十分ウォームアップする（対象ドキュメントを open し、初期インデックス完了・キャッシュ温熱状態にしてから計測開始）。常驻していない状態との比較は行わない。
- ゲート指標は 3 指標のみ: didChange→診断 / completion / hover の p50・p95 を**同一マシン・同一コーパス・同一クライアント**で比較し、「≤ 現状 / 10」を合格とする。
- コールド側（初期インデックス時間 / 大量ファイル一括処理）は参考値として別途記録し、ゲートには含めない。
- 内部計測（parse / index-update / scope-resolve の `Stopwatch`）を stderr に出力し、ボトルネックを常に可視化する。特に parse が全体のどれを占めるかを Phase 0 から追跡する。
- 日本語コメント・絵文字を含むソースで UTF-16 位置がズレないことを確認する。
- 不完全なコード（途中の `class` / `void M(`）で診断が落ちずに出力されることを確認する。
- `UNITY_EDITOR` を seeded symbols に渡した場合としない場合で `#if` 分岐の CST が変化することを確認する。

## 11. リスクと最終手段

- **最大リスク（ウォーム比較特有）**: csff はインクリメンタルパース API を公開していないため、didChange の全文再パースがレイテンシに直撃する。この点では tree-sitter の `TSInputEdit` による部分再パースが有利。Phase 0 の実測で「現状 / 10」に収まらない場合は、csff をフォークして内部の `Checkpoint` / `Restore` と `TokenInput.BorrowFrom` / `CopyFrom` を LSP 用の差分再パース API として公開する（最終手段）。それでも届かない場合は、構文バックエンドの見直し（ハイブリッド構成等）を検討する。
- 比較相手が常驻済みであるため、10 倍の効果は「Roslyn バインド廃止」「差分索引更新」「O(log n) 位置特定」「IPC 廃止」の 4 点に依存する。いずれかが欠けると目標を満たせない。
- 現状のウォーム時レイテンシが既に極小（10 ms 以下等）の場合、10 倍は体感差が薄い。Phase 0 の計測結果を見て目標値の妥当性を再確認する。
- セマンティックは軽量近似のままとし、10 倍目標を優先する。
- .NET 11 SDK は未導入のため、Phase 0 の最初に導入する。
- 構文バックエンドを csff に固定するため、csff の CST 形状に依存したコードは `Syntax` プロジェクト内に閉じ込める。
