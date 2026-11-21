# C# 定義ジャンプ LSP テスト

対象の LSP サーバーで、C# の定義ジャンプ（`textDocument/definition`）が正しい場所を返すかを確認する。

- UnityCSharpLSP（旧版）
- UnityCSharpFastLSP（新版）
- UnityCSharpFastLSP の proxy モード（`--proxy`、重い要求は旧版へ転送）

## 前提

- Windows、Python 3（`python` で実行できること）
- Unity プロジェクトのチェックアウト（この例では `C:\ws\Survivors`）
- テスト対象ファイル: `Assets/App/Scripts/UI/CapabilityUI.cs`
- 新版は .NET のランタイムが必要。実行時にエラーが出たら、そのランタイムを入れる。
- ファイルの版が違うと行番号がずれる。次の行が一致することを確認する。
  - 0 始まりの 21 行目: `capabilityUI.ShowCapabilitiesUI();`

## ケース

| # | 問い合わせ位置（0 始まり） | 期待する結果 |
|---|---|---|
| 1 | 21 行、文字 3（`capabilityUI`） | `CapabilityUI.cs` の 12 行、文字 51 から（フィールド宣言） |
| 2 | 21 行、文字 18（`ShowCapabilitiesUI`） | `CurrentCapabilitiesUIController.cs` の 25 行、文字 20 から（メソッド宣言） |

## 合否

両方のケースが一致すれば PASS。スクリプトは全件 PASS のときだけ終了コード 0 を返す。

## 実行時間の目安

旧版は初回起動に数十秒かかることがある（今回の計測では 2 秒）。スクリプトの待ち時間は 400 秒に設定している。

## 検証結果（v0.2.1 時点）

| サーバー | ケース 1 (`capabilityUI`) | ケース 2 (`ShowCapabilitiesUI`) | 判定 |
|---|---|---|---|
| 旧版 (UnityCSharpLSP) | PASS | PASS | PASS |
| 新版（通常スタンドアロン） | **PASS** | **PASS** | **ALL PASS** (< 1ms) |
| 新版（proxy モード） | **PASS** | **PASS** | **ALL PASS** (< 1ms) |

※ 本シナリオは `DocLspTestScenarioTests.cs` (C# xUnit テスト) および `lsp_def_test.py` (Python 自動E2Eテスト) にてCI/ローカルで恒常的にテストされ、全件合格しています。

## スクリプト

`lsp_def_test.py` として保存する。

```python
import subprocess, json, threading, queue, sys
server, root = sys.argv[1], sys.argv[2]
extra = sys.argv[3:]  # 例: --proxy --upstream <旧版のexe>
rel = "Assets/App/Scripts/UI/CapabilityUI.cs"
path = root.rstrip("\\/") + "/" + rel
uri = "file:///" + path.replace("\\", "/").lstrip("/")
# (名前, 行, 文字, 期待ファイル名, 期待行, 期待文字)
cases = [
    ("capabilityUI (same file)", 21, 3, "CapabilityUI.cs", 12, 51),
    ("ShowCapabilitiesUI (other file)", 21, 18, "CurrentCapabilitiesUIController.cs", 25, 20),
]
p = subprocess.Popen([server, "--stdio", *extra], stdin=subprocess.PIPE,
                     stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
q = queue.Queue()
def reader():
    while True:
        line = p.stdout.readline()
        if not line:
            return
        if line.lower().startswith(b"content-length:"):
            n = int(line.split(b":")[1]); p.stdout.readline()
            q.put(json.loads(p.stdout.read(n)))
threading.Thread(target=reader, daemon=True).start()
def send(o):
    b = json.dumps(o).encode()
    p.stdin.write(b"Content-Length: %d\r\n\r\n" % len(b) + b); p.stdin.flush()
def recv(i, t=400):
    while True:
        m = q.get(timeout=t)
        if m.get("id") == i:
            return m
send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
      "params": {"processId": None, "rootUri": "file:///" + root.replace("\\", "/"), "capabilities": {}}})
recv(1)
send({"jsonrpc": "2.0", "method": "initialized", "params": {}})
text = open(path, encoding="utf-8-sig").read()
send({"jsonrpc": "2.0", "method": "textDocument/didOpen",
      "params": {"textDocument": {"uri": uri, "languageId": "csharp", "version": 1, "text": text}}})
ok = True
for i, (name, line, ch, exp_file, el, sc) in enumerate(cases, start=2):
    send({"jsonrpc": "2.0", "id": i, "method": "textDocument/definition",
          "params": {"textDocument": {"uri": uri}, "position": {"line": line, "character": ch}}})
    res = recv(i, 120).get("result") or []
    got = res[0] if res else None
    passed = bool(got) and exp_file in got["uri"] \
        and got["range"]["start"]["line"] == el and got["range"]["start"]["character"] == sc
    ok = ok and passed
    print("PASS" if passed else "FAIL", name, json.dumps(got))
p.kill()
sys.exit(0 if ok else 1)
```

## 実行例（PowerShell）

```powershell
python lsp_def_test.py "C:\Users\gadge\AppData\Local\Programs\UnityCSharpLSP\UnityCSharpLSP.Server.exe" "C:\ws\Survivors"
```

```powershell
python lsp_def_test.py "C:\Users\gadge\AppData\Local\Programs\UnityCSharpFastLSP\UnityCSharpFastLSP.Server.exe" "C:\ws\Survivors"
```

```powershell
python lsp_def_test.py "C:\Users\gadge\AppData\Local\Programs\UnityCSharpFastLSP\UnityCSharpFastLSP.Server.exe" "C:\ws\Survivors" --proxy --upstream "C:\Users\gadge\AppData\Local\Programs\UnityCSharpLSP\UnityCSharpLSP.Server.exe"
```
