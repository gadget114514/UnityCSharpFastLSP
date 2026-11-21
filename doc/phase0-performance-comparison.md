# Phase 0 性能調査: UnityCSharpLSP / FastLSP / csharp-ls

実施日: 2026-11-21

## 1. 目的と範囲

3 つの C# LSP を**同一ワークスペース・同一スクリプト・同一条件**で計測し、
`doc/design.md` の 10 倍目標（didChange→診断 / completion / hover）の妥当性を確認する。

| 対象 | バージョン / 状態 | 備考 |
| :--- | :--- | :--- |
| UnityCSharpLSP | `src/UnityCSharpLSP.Server/bin/Release/net8.0`（既存ビルド） | 常驻 server、比較元 |
| csharp-ls | 0.28.0（dotnet global tool / .NET 10） | Roslyn ベースの汎用 LSP |
| FastLSP | Phase 0 実装のみ（LSP サーバー未実装） | パース～スナップショット生成のみ計測可能 |

## 2. 計測条件

| 項目 | 値 |
| :--- | :--- |
| ワークスペース | `C:\Users\bluen\AppData\Local\Temp\opencode\perf-workspace`（`DummyProject` の `Assets` / `Packages` / `ProjectSettings` をコピー + `PerfWorkspace.csproj` / `.sln` を追加） |
| 対象ファイル | `Assets/Scripts/PlayerController.cs`（817 文字 / 45 行） |
| ツール | lsbench 0.3.0-next.5（Node 24.14.1） |
| スクリプト | `perf-actions.ts`（open → 診断待ち → hover/completion/definition/references/documentSymbol/semanticTokens/formatting → didChange → 診断待ち → 再リクエスト → close） |
| イテレーション | 10（warmup 3）、サーバーは使い回し（`--restart` なし） |
| 診断待ちタイムアウト | 600 ms |
| ホスト | Windows / 同一マシン、Release ビルド |

補正:

- csharp-ls は初期状態では `workspaceFolders` の Windows URI が不正（`file://C:\...`）で
  ドキュメントが workspace に一致せず、全リクエストが `null` を返した。
  lsbench の `pathToUri` を `pathToFileURL()` ベースに一時パッチし、
  `csharp.solutionPathOverride` を settings / initializationOptions で明示して正常応答させた
  （計測後にパッチは revert 済み）。
- 当初 `didChange` 後の診断待ちが 100% タイムアウトしていたが、これはベンチスクリプトの
  `edit` のフィールド名誤り（`newText` ではなく `text`）が原因だった。
  修正後に観測できた値は第 7 節を参照（第 3〜4 節は修正前の予備計測）。

## 3. 結果

単位: ms（FastLSP のみ in-process 計測）。`median / p95`。

### 3.1 LSP リクエスト（lsbench、ウォーム）

| メソッド | UnityCSharpLSP | csharp-ls | 備考 |
| :--- | ---: | ---: | :--- |
| `lifecycle/initialize` | 771.8 / - | 791.6 / - | コールド 1 回のみ、ゲート外 |
| `diagnostics/open` | **3.06 / 3.85** | 229.5 / 269.9 | csharp-ls はばらつき大（20〜270 ms） |
| `textDocument/hover` | **0.73 / 1.07** | 1.01 / 1.52 | Unity がやや有利 |
| `textDocument/completion` | 13.79 / 19.49 | **3.06 / 7.05** | csharp-ls が約 4.5 倍速い |
| `textDocument/definition` | **0.73 / 0.96** | 0.83 / 1.23 | ほぼ同等 |
| `textDocument/references` | 2.63 / 3.48 | **2.19 / 3.18** | ほぼ同等 |
| `textDocument/documentSymbol` | **0.91 / 1.08** | 1.08 / 4.18 | p95 で Unity が有利 |
| `textDocument/semanticTokens/full` | **1.83 / 2.19** | 2.43 / 6.09 | Unity が有利 |
| `textDocument/formatting` | 1.67 / 3.01 | 1.35 / 3.91 | ほぼ同等 |
| `diagnostics/edit` | timeout | timeout | 計測不能（上記補正） |

### 3.2 FastLSP（Phase 0 ベンチ、parse + 不変スナップショット生成）

| コーパス | p50 | p95 |
| :--- | ---: | ---: |
| `PlayerController.cs`（817 文字、ベースライン同一ファイル） | **0.0445** | 0.0670 |
| 合成 Unity スクリプト ~8 KB | 0.291 | 0.587 |
| 合成 Unity スクリプト ~80 KB | 0.675 | 1.058 |
| 合成 Unity スクリプト ~300 KB | 2.867 | 3.892 |

※ LSP トランスポート / リクエスト処理は未実装のため、上表の LSP リクエスト値とは直接比較できない。

## 4. 考察

- **UnityCSharpLSP の現状値は 2026-11-17 のベースラインよりかなり速い**。
  当時 completion median 38.96 ms だったのが今回 13.79 ms。コーパス（3 ファイル / 4 KB）と
  スクリプト内容の差が大きく、`/10` の目標値は 3.90 ms → **1.38 ms** に引き下げて考えるべき。
- **csharp-ls は completion が突出して速い**（3.06 ms）。Roslyn のキャッシュが効いている。
  一方 diagnostics/open は 229 ms と遅く、初期ロード直後の診断が支配的。
  hover / definition / documentSymbol では UnityCSharpLSP と同程度かやや遅い。
- **FastLSP のパース部分だけなら 0.0445 ms** で、Unity の diagnostics 3.06 ms の 1/10
  （0.306 ms）を余裕で下回る。completion 目標 1.38 ms に対しても 30 倍以上のマージンがある。
  ただし実際の completion 応答には索引参照・スコープ解決・JSON 応答が乗るため、
  Phase 1〜3 実装後にlsbench で再検証が必要。
- 80 KB 以上の巨大ファイルでは FastLSP のパースが 0.675〜2.867 ms に達し、
  completion 目標 1.38 ms を超える。実用上は `didChange` 対象は小さい Unity スクリプトが
  中心だが、巨大ファイル編集時は差分再パース（設計書 11 節の最終手段）の検討材料になる。
- `diagnostics/edit` が両サーバーで観測できなかったため、設計の主ゲートである
  didChange→診断の実測は未達。Phase 4 で専用スクリプト（長いタイムアウト / pull モード）
  を用意し直すこと。

## 5. 成果物

- 生データ: `C:\Users\bluen\AppData\Local\Temp\opencode\results-unity-final.json`
  および `results-csharp-ls-final.json`
- サーバーログ: 同ディレクトリの `unity-server-final.log` / `csharp-ls-server-final.log`
- FastLSP 数値: `doc/20261121-phase0.md`、`benchmarks/Phase0.Bench`

## 6. 注意

- csharp-ls の計測には lsbench の一時パッチが必要だった（Windows の file URI 不具合）。
  パッチを当てない場合の csharp-ls は全リクエストが `null` となり、実質比較不能。
- UnityCSharpLSP の 2026-11-17 ベースライン (`doc/20261117-lsbench.md`) と今回の数値は
  ワークスペース / スクリプトが異なるため直接比較しないこと。

## 7. 実規模コーパスでの再計測（最終判定）

上記は 3 ファイル / 4 KB の予備計測だったため、`DummyProject/Library/PackageCache` の
**6,737 ファイル / 75 MB** を `Packages/PackageCache` にコピーした実規模ワークスペースで
再計測した。スクリプトの `edit` フィールドを `newText` → `text` に修正し、
`diagnostics/edit`（didChange→診断）も正常に計測できている。

条件: 同一ワークスペース / 同一スクリプト / 5 イテレーション（warmup 2）/ サーバー使い回し。
FastLSP はベンチに必要な最小 LSP サーバー（csff パース + フラット索引 + JSON-RPC）を実装して参加。

| メソッド (median, ms) | UnityCSharpLSP | csharp-ls | FastLSP |
| :--- | ---: | ---: | ---: |
| `diagnostics/open` | 4.92 | 463.68 | **0.61** |
| `diagnostics/edit`（didChange→診断） | 3.51 | 405.75 | **0.50** |
| `textDocument/hover` | 0.86 | 0.90 | **0.32** |
| `textDocument/completion` | 135.03 | 26.51 | **0.47** (p95 5.32) |
| `textDocument/definition` | 1.08 | 1.12 | **0.36** |
| `textDocument/references` | 3.06 | 27.05 | **0.33** |
| `textDocument/documentSymbol` | 2.02 | 1.44 | **0.34** |
| `textDocument/semanticTokens/full` | 3.87 | 2.90 | **0.32** |
| `textDocument/formatting` | 3.36 | 2.15 | **0.30**（空 edits） |

### 判定

- **差は明確に出た**。didChange→診断で Unity 対 7.0 倍 / csharp-ls 対 811 倍、
  completion で Unity 対 289 倍 / csharp-ls 対 56 倍。
- ただし設計の 10 倍ゲート（Unity 対比）は **completion のみ達成**。
  didChange→診断は 7.0 倍（目標 0.351 ms に対し実測 0.499 ms）、
  hover は 2.7 倍（目標 0.086 ms に対し 0.323 ms）で未達。
  hover は両既存サーバーが既に 1 ms 前後で、JSON-RPC 往復の下限に近く 10 倍は非現実的。
- FastLSP の応答は最小実装のため仕事量が等しくない（completion は最大 120 件のラベルのみ、
  formatting は空配列、hover は `Kind Name` のみ）。等しい機能規模なら差は縮む。
- コールドの `initialize` は FastLSP 1,142 ms（6,737 ファイルを並列パース）で
  Unity 774 ms / csharp-ls 801 ms より遅いが、設計どおりゲート外。

### 成果物（実規模）

- `results-unity-large2.json` / `results-csharp-ls-large2.json` / `results-fast-large.json`
- `unity-large2-server.log` / `csharp-ls-large2-server.log` / `fast-lsp-large-server.log`
- いずれも `C:\Users\bluen\AppData\Local\Temp\opencode\` 配下。

### 結論

実規模でも速度差は明確に存在し、作る価値はあると判断できる。
ただし 10 倍は指標により達成度が異なるため、hover の目標は再定義が必要。
FastLSP 側は今回ベンチ用の最小実装にとどめており、等価な機能を持たせるには
Phase 1〜3 の追加実装が必要。
