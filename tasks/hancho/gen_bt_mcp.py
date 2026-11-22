"""MCP カード列 (CARD-MCP-01..05) の Hancho ビヘイビアツリーを生成する。

usage: python tasks/hancho/gen_bt_mcp.py  ->  tasks/hancho/bt-mcp.json
"""
import json
from pathlib import Path

ROOT = "D:/wsgit/UnityCSharpFastLSP"
DOTNET = "C:/Users/bluen/dotnet11/dotnet.exe"
ENV = {"DOTNET_ROOT": "C:\\Users\\bluen\\dotnet11"}
ENGINES = [
    {"engine": "claude", "model": "haiku"},
    {"engine": "opencode", "model": "opencode/hy4-flash-free"},
]

CARDS = [
    ("mcp01", "CARD-MCP-01-ToolTable.md", "Implement MCP tool table (McpToolTable.cs)"),
    ("mcp02", "CARD-MCP-02-Bridge.md", "Implement MCP stdio server and LSP bridge"),
    ("mcp03", "CARD-MCP-03-ProgramWiring.md", "Wire --mcp mode into Program.cs"),
    ("mcp04", "CARD-MCP-04-Tests.md", "Add MCP mode tests"),
    ("mcp05", "CARD-MCP-05-Docs.md", "Write MCP mode documentation"),
]

BUILD = f"{DOTNET} build UnityCSharpFastLSP.sln -c Release"
TEST = f"{DOTNET} test tests/UnityCSharpFastLSP.Tests/UnityCSharpFastLSP.Tests.csproj -c Release"

PROMPT = (
    "Stay inside the project (cwd): do not read or write files outside it. "
    "If the task requires one, add it to needs and request a permit instead. "
    "You are implementing one task card of the UnityCSharpFastLSP MCP mode. "
    "First read tasks/cards/CARD-MCP-00-Overview.md (common rules and the compatibility contract) "
    "and doc/mcp-csharp-lsp.md (the spec), then implement tasks/cards/{card} completely. "
    "Read the existing code you touch (src/UnityCSharpFastLSP.Server/Mcp/LspLoopbackClient.cs, "
    "LspServer.cs, LspProxy.cs, Program.cs) before editing. "
    "Build with: " + DOTNET + " build UnityCSharpFastLSP.sln -c Release (env DOTNET_ROOT=C:\\Users\\bluen\\dotnet11) "
    "and fix every warning/error until it builds clean. Do not git commit. Do not add NuGet packages. "
    "When done, write a JSON object {{\"card\": \"{card}\", \"filesChanged\": [...], \"notes\": \"...\"}} to the file named by env PC_BT_RESULT_FILE."
)

FIX_PROMPT = (
    "Stay inside the project (cwd): do not read or write files outside it. "
    "The command `{cmd}` failed after task card tasks/cards/{card} was implemented. "
    "Read the log ${{{node}.logFile}} and fix the code so it passes. "
    "Follow tasks/cards/CARD-MCP-00-Overview.md. Do not weaken or delete tests to make them pass; fix the implementation. "
    "Do not git commit. Write {{\"fixed\": true, \"notes\": \"...\"}} to the file named by env PC_BT_RESULT_FILE."
)


def handlers(node_id: str, question: str):
    return [
        {"class": ["startup_hang", "idle_stall"], "do": {"type": "set", "var": "hang", "value": "engine-switch-handled"}},
        {"class": "rate_limited", "do": {"type": "wait", "sec": 600}, "thenRetry": True},
        {"class": "permission_denied", "do": {"type": "set", "var": "permit", "value": "requested"}, "thenRetry": False},
        {
            "class": "*",
            "do": {
                "type": "escalate",
                "question": question,
                "guidance": "The worker failed after all approved engine candidates were tried.",
                "expect": ["continue", "stop", "takeover"],
                "include": {"logs": [node_id], "logLines": 100},
            },
        },
    ]


def guarded_worker(node_id: str, prompt: str, summary: str, question: str, timeout: int):
    return {
        "type": "catch",
        "id": f"{node_id}_guard",
        "maxRetries": 2,
        "otherwise": "fail",
        "child": {
            "type": "worker",
            "id": node_id,
            "engines": ENGINES,
            "switchEngineOn": ["startup_hang", "auth_error", "probe_failed"],
            "prompt": prompt,
            "summary": summary,
            "timeoutSec": timeout,
        },
        "handlers": handlers(node_id, question),
    }


def job(node_id: str, cmd: str, summary: str, timeout: int):
    return {"type": "job", "id": node_id, "command": cmd, "summary": summary, "timeoutSec": timeout, "env": ENV}


def gate(card_id: str, card: str, kind: str, cmd: str, timeout: int):
    """cmd を実行し、失敗したら worker に 1 回直させて再実行する。"""
    first = f"{kind}_{card_id}"
    return {
        "type": "fallback",
        "children": [
            job(first, cmd, f"{kind} after {card_id}", timeout),
            {
                "type": "sequence",
                "children": [
                    guarded_worker(
                        f"fix_{kind}_{card_id}",
                        FIX_PROMPT.format(cmd=cmd, card=card, node=first),
                        f"Fix {kind} failure after {card_id}",
                        f"{kind} still fails after {card_id}; how should I proceed?",
                        2700,
                    ),
                    job(f"re{kind}_{card_id}", cmd, f"Re-run {kind} after fixing {card_id}", timeout),
                ],
            },
        ],
    }


children = []
for card_id, card, summary in CARDS:
    children.append(
        guarded_worker(
            f"card_{card_id}",
            PROMPT.format(card=card),
            summary,
            f"Card {card} failed on every approved engine. Continue, stop, or take over?",
            5400,
        )
    )
    children.append(gate(card_id, card, "build", BUILD, 900))
    if card_id in ("mcp02", "mcp03", "mcp04"):
        children.append(gate(card_id, card, "test", TEST, 1200))

plan = {
    "id": "fastlsp-mcp",
    "cwd": ROOT,
    "intent": "Implement UnityCSharpFastLSP --mcp mode compatible with UnityCSharpLSP MCP, with --proxy fallback",
    "summary": "MCP mode cards CARD-MCP-01..05 with build/test gates",
    "defaults": {"worker": {"idle": {"firstEventSec": 180, "outputSec": 600}, "probe": True}},
    "root": {"type": "sequence", "children": children},
}

out = Path(__file__).with_name("bt-mcp.json")
out.write_text(json.dumps(plan, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(out)
