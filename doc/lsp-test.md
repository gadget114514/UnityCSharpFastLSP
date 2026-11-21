# C# 定義ジャンプ LSP テスト

対象の LSP サーバーで、C# の定義ジャンプ（`textDocument/definition`）が正しい場所を返すかを確認する。

- UnityCSharpLSP（旧版）
- UnityCSharpFastLSP（新版）
- UnityCSharpFastLSP の proxy モード（`--proxy`、重い要求は旧版へ転送）

## 前提（すべて相対パスで動作可能）

- Windows、Python 3（`python` で実行できること）
- リポジトリ同梱のテストフィクスチャ: `./test-fixtures/Survivors`（相対パス）
- テスト対象ファイル: `Assets/App/Scripts/UI/CapabilityUI.cs`
- 参照先ファイル: `Assets/App/Scripts/UI/CurrentCapabilitiesUIController.cs`
- 次の行が一致することを確認する:
  - 0 始まりの 21 行目: `capabilityUI.ShowCapabilitiesUI();`

## ケース

| # | 問い合わせ位置（0 始まり） | 期待する結果 |
|---|---|---|
| 1 | 21 行、文字 3（`capabilityUI`） | `CapabilityUI.cs` の 12 行、文字 51 から（フィールド宣言） |
| 2 | 21 行、文字 18（`ShowCapabilitiesUI`） | `CurrentCapabilitiesUIController.cs` の 25 行、文字 20 から（メソッド宣言） |

## 合否

両方のケースが一致すれば PASS。スクリプトは全件 PASS のときだけ終了コード 0 を返す。

## 検証結果（v0.2.1 時点）

| サーバー | ケース 1 (`capabilityUI`) | ケース 2 (`ShowCapabilitiesUI`) | 判定 |
|---|---|---|---|
| 旧版 (UnityCSharpLSP) | PASS | PASS | PASS |
| 新版（通常スタンドアロン） | **PASS** | **PASS** | **ALL PASS** (< 1ms) |
| 新版（proxy モード） | **PASS** | **PASS** | **ALL PASS** (< 1ms) |

※ 本シナリオは `DocLspTestScenarioTests.cs` (C# xUnit テスト) および `lsp_def_test.py` (Python 自動E2Eテスト) にてCI/ローカルで恒常的にテストされ、全件合格しています。

---

## テスト対象コードの内容

### 1. `Assets/App/Scripts/UI/CapabilityUI.cs`

```csharp
using UnityEngine;
using System;

namespace App.UI
{
    public class CapabilityUI : MonoBehaviour
    {
        public int ViewIndex;
        public string ViewName;
        private bool _isDirty;
        private float _padding;

        private CurrentCapabilitiesUIController    capabilityUI;

        public void Setup()
        {
        }

        public void Render()
        {
            int dummy = 0;
capabilityUI.ShowCapabilitiesUI();
        }
    }
}
```

- **12 行目（0 始まり）**: 51 文字目から `capabilityUI;`（フィールド宣言）
- **21 行目（0 始まり）**: `capabilityUI.ShowCapabilitiesUI();`
  - 文字 3: `capabilityUI`
  - 文字 18: `ShowCapabilitiesUI`

### 2. `Assets/App/Scripts/UI/CurrentCapabilitiesUIController.cs`

```csharp
using UnityEngine;
using System;

namespace App.UI
{
    public class CurrentCapabilitiesUIController : MonoBehaviour
    {
        public int ControllerId = 1;
        public bool IsActive = true;

        public void Initialize()
        {
        }

        public void ResetUI()
        {
        }

        public void UpdateUI()
        {
        }

        public void Close()
        {
        }
        public void ShowCapabilitiesUI()
        {
            Debug.Log("Capabilities UI shown");
        }
    }
}
```

- **25 行目（0 始まり）**: 20 文字目から `ShowCapabilitiesUI()`（メソッド宣言）

---

## 実行方法（PowerShell / コマンドプロンプト）

相対パスに対応しており、引数なしまたは相対パス指定でそのまま実行できます。

### (A) 引数なし（デフォルトでリポジトリ内相対パスを使用）
```powershell
python lsp_def_test.py
```

### (B) 実行ファイルとプロジェクトを相対パスで明示指定
```powershell
python lsp_def_test.py ./artifacts/publish/UnityCSharpFastLSP.Server.exe ./test-fixtures/Survivors
```

### (C) proxy モードで実行
```powershell
python lsp_def_test.py ./artifacts/publish/UnityCSharpFastLSP.Server.exe ./test-fixtures/Survivors --proxy
```
