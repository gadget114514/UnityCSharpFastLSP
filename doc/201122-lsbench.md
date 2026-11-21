# lsbench ベンチマーク結果 (2026-11-22)

UnityCSharpFastLSP (新版) と UnityCSharpLSP (旧版) および csharp-ls (Roslyn 汎用) を実規模 Unity コーパスにて `lsbench` (0.3.0-next.5) で計測した性能結果記録。

---

## 1. 実行条件

| 項目 | 設定・環境 |
| :--- | :--- |
| **ツール** | `lsbench` 0.3.0-next.5 (Node v24.14.1 / npm 11.12.1) |
| **ワークスペース** | 実規模 Unity コーパス: `Packages/PackageCache` に **6,737 ファイル / 75 MB** を展開 |
| **対象ファイル** | `Assets/Scripts/PlayerController.cs` (817 文字 / 45 行) |
| **スクリプト** | `perf-actions-large.ts` (open → 診断待ち → hover/completion/definition/references/documentSymbol/semanticTokens/formatting → didChange → 診断待ち → 再リクエスト → close) |
| **イテレーション** | 5 回 (warmup 2 回)、サーバー使い回し (`--restart` なし) |
| **診断タイムアウト** | 600 ms |
| **測定マシン** | Windows 11 / AMD Ryzen / Release ビルド (.NET 11 vs .NET 8 vs .NET 10) |
| **生データファイル** | `results-fast-large.json` / `results-unity-large2.json` / `results-csharp-ls-large2.json` |

---

## 2. 3 サーバー比較サマリー（実規模コーパス / 中央値 `median`）

同一ワークスペース・同一スクリプト・ウォームアップ完了後の各メソッドの応答時間（中央値、単位: ms）。

| メソッド | UnityCSharpLSP (旧版) | csharp-ls (Roslyn汎用) | **UnityCSharpFastLSP (新版)** | 高速化倍率 (vs 旧版) |
| :--- | ---: | ---: | ---: | :---: |
| **`diagnostics/open`** | 4.92 ms | 463.68 ms | **0.61 ms** | **8.1 倍** |
| **`diagnostics/edit` (didChange→診断)** | 3.51 ms | 405.75 ms | **0.50 ms** | **7.0 倍** |
| **`textDocument/hover`** | 0.86 ms | 0.90 ms | **0.32 ms** | **2.7 倍** |
| **`textDocument/completion`** | 135.03 ms | 26.51 ms | **0.47 ms** | **287 倍** |
| **`textDocument/definition`** | 1.08 ms | 1.12 ms | **0.36 ms** | **3.0 倍** |
| **`textDocument/references`** | 3.06 ms | 27.05 ms | **0.33 ms** | **9.3 倍** |
| **`textDocument/documentSymbol`** | 2.02 ms | 1.44 ms | **0.34 ms** | **5.9 倍** |
| **`textDocument/semanticTokens/full`** | 3.87 ms | 2.90 ms | **0.32 ms** | **12.1 倍** |
| **`textDocument/formatting`** | 3.36 ms | 2.15 ms | **0.30 ms** | **11.2 倍** |

---

## 3. UnityCSharpFastLSP 詳細統計値 (`results-fast-large.json`)

| メソッド | 実行回数 | 平均 (ms) | 中央値 (ms) | p95 (ms) | 最小 (ms) | 最大 (ms) | 失敗率 |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `diagnostics/open` | 5 | 0.633 | **0.610** | 0.772 | 0.529 | 0.772 | 0.0% |
| `diagnostics/edit` | 5 | 0.538 | **0.499** | 0.705 | 0.465 | 0.705 | 0.0% |
| `textDocument/hover` | 10 | 0.338 | **0.323** | 0.420 | 0.258 | 0.420 | 0.0% |
| `textDocument/completion` | 10 | 0.979 | **0.465** | 5.322 | 0.330 | 5.322 | 0.0% |
| `textDocument/definition` | 10 | 0.434 | **0.357** | 0.899 | 0.192 | 0.899 | 0.0% |
| `textDocument/references` | 5 | 0.423 | **0.328** | 0.901 | 0.250 | 0.901 | 0.0% |
| `textDocument/documentSymbol` | 10 | 0.353 | **0.341** | 0.458 | 0.264 | 0.458 | 0.0% |
| `textDocument/semanticTokens/full` | 5 | 0.331 | **0.322** | 0.432 | 0.264 | 0.432 | 0.0% |
| `textDocument/formatting` | 5 | 0.292 | **0.298** | 0.361 | 0.230 | 0.361 | 0.0% |

- **全メソッドで `failure_rate = 0.0%`、`timeout = 0` を達成**。
- **イテレーション平均全体時間**: **111.77 ms** (旧版の約 1/3 以下)。

---

## 4. 分析と考察

1. **`textDocument/completion` の圧倒的短縮**:
   - 旧版 Roslyn ベースでは 6,737 ファイルの型・シンボル解決により 135.03 ms かかっていた補完が、FastLSP のインメモリフラット索引と軽量型推論により **0.47 ms (約 287 倍の高速化)** を達成。
2. **`diagnostics/edit` (didChange → 診断発行)**:
   - ファイル更新時の再パース・診断通知が **0.50 ms**。csharp-ls の 405.75 ms に対し約 811 倍、旧版の 3.51 ms に対し約 7 倍のレイテンシ短縮。
3. **主要ナビゲーション (`definition` / `references`)**:
   - `definition`: 0.36 ms、`references`: 0.33 ms と、1ms を大幅に下回るサブミリ秒応答を実現。
4. **ライフサイクル (`initialize`)**:
   - 6,737 ファイルの並列パースを伴うコールドスタート時の初回 `initialize` は FastLSP で 1,141 ms (旧版 774 ms / csharp-ls 801 ms)。
   - 非同期インデックス作成および待機機能（v0.2.1）により、クライアントをブロックせず安全に初期化が完了する。
