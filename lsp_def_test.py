import os
import sys
import subprocess
import json
import threading
import queue

# 相対パス対応: 引数未指定時はリポジトリ内の相対パスをデフォルト値として使用
default_server = os.path.join(".", "artifacts", "publish", "UnityCSharpFastLSP.Server.exe")
default_root = os.path.join(".", "test-fixtures", "Survivors")

extra_args = []
server_arg = default_server
root_arg = default_root

args = sys.argv[1:]
pos_args = []
for a in args:
    if a.startswith("--"):
        extra_args.append(a)
    else:
        pos_args.append(a)

if len(pos_args) >= 1:
    server_arg = pos_args[0]
if len(pos_args) >= 2:
    root_arg = pos_args[1]

# 相対パスを絶対パスへ正規化
server_path = os.path.abspath(server_arg)
root_path = os.path.abspath(root_arg)

rel_file = os.path.join("Assets", "App", "Scripts", "UI", "CapabilityUI.cs")
file_abs_path = os.path.join(root_path, rel_file)

if not os.path.exists(file_abs_path):
    print(f"[ERROR] Target test file not found: {file_abs_path}", file=sys.stderr)
    print(f"Please check root path '{root_arg}' or provide a valid Unity project directory.", file=sys.stderr)
    sys.exit(1)

# file:/// URI の生成
def to_file_uri(p):
    norm = os.path.abspath(p).replace("\\", "/")
    if not norm.startswith("/"):
        norm = "/" + norm
    return "file://" + norm

root_uri = to_file_uri(root_path)
file_uri = to_file_uri(file_abs_path)

# (テスト名, 問い合わせ行, 問い合わせ文字, 期待ファイル名, 期待行, 期待開始文字)
cases = [
    ("capabilityUI (same file field)", 21, 3, "CapabilityUI.cs", 12, 51),
    ("ShowCapabilitiesUI (other file method)", 21, 18, "CurrentCapabilitiesUIController.cs", 25, 20),
]

cmd = [server_path, "--stdio", *extra_args]
print(f"Running LSP Definition Test against: {server_path}")
print(f"Workspace Root: {root_path} ({root_uri})")
print(f"Target File: {file_abs_path}")
if extra_args:
    print(f"Extra Options: {extra_args}")

p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
q = queue.Queue()

def reader():
    while True:
        line = p.stdout.readline()
        if not line:
            return
        if line.lower().startswith(b"content-length:"):
            try:
                n = int(line.split(b":")[1])
                p.stdout.readline()  # empty separator line
                raw_payload = p.stdout.read(n)
                q.put(json.loads(raw_payload.decode("utf-8")))
            except Exception:
                pass

threading.Thread(target=reader, daemon=True).start()

def send(obj):
    body = json.dumps(obj).encode("utf-8")
    header = f"Content-Length: {len(body)}\r\n\r\n".encode("ascii")
    p.stdin.write(header + body)
    p.stdin.flush()

def recv(msg_id, timeout=60):
    while True:
        m = q.get(timeout=timeout)
        if m.get("id") == msg_id:
            return m

# 1. initialize
send({
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "processId": None,
        "rootUri": root_uri,
        "workspaceFolders": [{"uri": root_uri, "name": "Survivors"}],
        "capabilities": {}
    }
})
init_res = recv(1)

# 2. initialized
send({"jsonrpc": "2.0", "method": "initialized", "params": {}})

# 3. textDocument/didOpen
text = open(file_abs_path, encoding="utf-8-sig").read()
send({
    "jsonrpc": "2.0",
    "method": "textDocument/didOpen",
    "params": {
        "textDocument": {
            "uri": file_uri,
            "languageId": "csharp",
            "version": 1,
            "text": text
        }
    }
})

# 4. definition requests
ok = True
for idx, (name, line, ch, exp_file, el, sc) in enumerate(cases, start=2):
    send({
        "jsonrpc": "2.0",
        "id": idx,
        "method": "textDocument/definition",
        "params": {
            "textDocument": {"uri": file_uri},
            "position": {"line": line, "character": ch}
        }
    })
    response = recv(idx, 60)
    res = response.get("result") or []
    got = res[0] if (isinstance(res, list) and len(res) > 0) else None

    passed = (
        bool(got) and
        (exp_file in got.get("uri", "")) and
        got.get("range", {}).get("start", {}).get("line") == el and
        got.get("range", {}).get("start", {}).get("character") == sc
    )
    ok = ok and passed
    status_str = "[PASS]" if passed else "[FAIL]"
    print(f"  {status_str} {name}: expected {exp_file}:{el}:{sc} -> got: {json.dumps(got)}")

p.kill()
if ok:
    print("\nALL CASES PASSED SUCCESSFULLY!")
    sys.exit(0)
else:
    print("\nSOME CASES FAILED!")
    sys.exit(1)
