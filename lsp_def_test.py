import subprocess, json, threading, queue, sys

if len(sys.argv) < 3:
    print("Usage: python lsp_def_test.py <server_exe> <root_path> [extra_args...]")
    sys.exit(1)

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
