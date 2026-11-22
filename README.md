# UnityCSharpFastLSP

**Ultra-Fast, Lightweight C# Language Server Protocol (LSP) Server for Unity.**  
Built on top of [csff](https://github.com/paq/csff) CST (Concrete Syntax Tree) and .NET 11.

---

## Overview

Traditional Roslyn-based C# language servers (`UnityCSharpLSP`, `csharp-ls`, `OmniSharp`) incur noticeable latency spikes (10–50 ms per request), high memory footprints, and heavy compilation pauses when navigating large Unity projects with numerous assemblies and package caches.

**UnityCSharpFastLSP** is engineered to eliminate editing friction:
- **Sub-Millisecond Response Time**: Warm per-request latency of **~0.15 ms for completion** and **~0.10 ms for hover** in real stdio IPC (**~90x faster** than Roslyn baseline).
- **Instant Incremental Parsing**: Keystrokes inside method bodies update in **under 30 microseconds** using token-shifted CST synthesis.
- **Unity-Centric Intelligence**: Built-in awareness of MonoBehaviour lifecycle messages (13 templates), serialization attributes, and `#if UNITY_EDITOR` preprocessor branching.
- **Zero Roslyn Overhead**: Operates without generated `.csproj`/`.sln` files, external DLL binding overhead, or heavy background Roslyn compilations.

---

## Express Start with LLM

- install the package with adding to find the executable in PATH
- Talk to LLM, "find UnityCSharpFastLSP and read help" 

---

## Performance Highlights

For the full detailed benchmark methodology, in-process micro-benchmarks, and cold-start analyses, see the [Performance Comparison Report](doc/performance-comparison.md).

### Warm Per-Request Latency (End-to-End Stdio IPC)

| Metric / LSP Request | UnityCSharpLSP (Roslyn) | csharp-ls (Roslyn) | **UnityCSharpFastLSP (Ours)** | Speedup vs Baseline |
| :--- | ---: | ---: | ---: | :---: |
| `textDocument/completion` | 13.79 ms | 3.06 ms | **0.154 ms** | **~90x faster** |
| `textDocument/hover` | 0.73 ms | 1.01 ms | **0.108 ms** | **~7x faster** |
| Cold Start (`initialize`) | 771.80 ms | 791.60 ms | **481.11 ms** | **~38% faster** |

---

## Features & Comparisons

- **[LSP Feature Plan & Feasibility Matrix](doc/feature-plan.md)**: Comprehensive breakdown of implemented, feasible, and infeasible LSP features.
- **[Feature Matrix](doc/features.md)**: Supported LSP methods, Unity capabilities, and design non-goals.
- **[Feature Comparison vs UnityCSharpLSP](doc/feature-comparison.md)**: In-depth comparison with Roslyn-based UnityCSharpLSP and the practical value of FastLSP.
- **[Performance Comparison Report](doc/performance-comparison.md)**: Benchmarks across in-process logic and real stdio IPC.

- **LSP Methods**: `initialize`, `shutdown`, `exit`, `didOpen`, `didChange` (incremental/full), `didClose`, `publishDiagnostics`, `documentSymbol`, `hover`, `completion`, `definition`, `references`, `formatting`, `signatureHelp`, `semanticTokens/full`, `documentHighlight`, `workspace/symbol`.
- **Workspace Symbol Search with Pattern Extensions**: High-speed symbol searching supporting wildcards (`*`, `?`), CamelHump abbreviations (`PC` -> `PlayerController`), and container qualification (`Player.TakeDamage`).
- **CST Syntax Diagnostics**: Instantaneous detection of incomplete syntax elements (`CSFF001`) and bad tokens (`CSFF002`).
- **Hierarchical Document Symbols**: Fast extraction of Namespace -> Type -> Member symbol trees.
- **Workspace Indexing**: Background parallel scanning of all `.cs` files across `Assets/` and `Packages/`.
- **Scoping & Member Filtering**: Dot-member completion (`x.`) narrows candidates strictly to the inferred type's members instead of fuzzy token dumps.
- **Class Inheritance Resolution**: Transitive traversal of base classes (`class Player : BaseCharacter`) merges base members into completions with 2–5 µs overhead.
- **Pre-baked Unity API Dictionary**: In-memory dictionary for common Unity classes (`Transform`, `GameObject`, `Vector3`, `Mathf`, `Time`, `Debug`, `Physics`, `Component`, `MonoBehaviour`, `Color`).
- **Signature Help**: Real-time parameter signatures and active parameter tracking on typing `(` and `,`.
- **Semantic Highlighting**: Standard LSP delta-encoded semantic tokens for classes, structs, methods, properties, and keywords.
- **LLM Agent Ready**: Self-describing `--print-config` and `--help` with `unity-csharp-fast-2nek` keyword for direct integration with AI coding agents.

---

## Pros & Cons: Choosing Between FastLSP and Roslyn

Why does **UnityCSharpFastLSP** exist when Roslyn-based language servers (`UnityCSharpLSP`, `csharp-ls`, `OmniSharp`) already exist?

### The Core Problem with Roslyn in Unity
Roslyn is a full compiler. To provide completions, it must build a complete compilation workspace with all referenced assemblies (`UnityEngine.CoreModule.dll`, package DLLs, etc.) and project definitions (`.csproj`/`.sln`). In real Unity development:
1. **Compilation Latency**: Per-keystroke Roslyn semantic model queries take 10–50 ms.
2. **Cold-Start Penalty**: Booting Roslyn and loading Unity solution projects takes seconds and hundreds of megabytes of RAM.
3. **Fragile Synchronization**: When Unity updates package caches or regenerates `.csproj` files, Roslyn gets desynchronized, stalling the editor.
4. **Agent Friction**: Autonomous AI coding agents (Claude Code, OpenCode, Cursor, Antigravity) execute tight edit-test-check loops where a 10 ms delay and heavy initialization kills turnaround velocity.

### FastLSP Pros (Why FastLSP Wins)
- ⚡ **Sub-Millisecond Response (<0.2 ms)**: Real stdio IPC completion in ~0.15 ms and hover in ~0.10 ms (**~90x faster** than Roslyn baseline).
- ⚡ **Microsecond Incremental Parsing (<30 µs)**: Method-body keystrokes re-synthesize CSTs instantaneously via token shifting.
- 🎯 **Lightweight Type Inference & Member Filtering**: When typing `enemy.`, completions are filtered strictly to members of `Enemy` (methods, properties, fields) using fast syntax-level inference for `var x = new T()`, `T x`, `this.`, parameters, and fields.
- 🛡️ **Zero Project File / MSBuild Dependency**: Operates directly on raw C# source files and folders. Works flawlessly even if `.csproj` is missing, corrupted, or mid-compilation.
- 🎮 **Built-in Unity Intelligence**: Native awareness of 13 MonoBehaviour lifecycle messages (`Awake`, `Start`, `Update`, etc.), serialization attributes (`[SerializeField]`), and `#if UNITY_EDITOR` preprocessors without loading Unity DLLs.
- 🪶 **Minimal Resource Footprint**: Cold-starts in ~480 ms, consumes < 15 MB of RAM, and runs as a self-contained single-file binary.

### FastLSP Cons (When You Should Use Roslyn Instead)
- ❌ **No Deep Compiler Diagnostics**: Does not emit semantic type errors (e.g., `CS0103: name does not exist`, `CS0029: cannot implicitly convert type`). It only emits syntax errors (`CSFF001`/`CSFF002`).
- ❌ **No Complex Overload Resolution**: Cannot resolve complex multi-generic type constraints across foreign external assemblies.
- ❌ **No Solution-Wide Semantic Refactoring**: Does not support Roslyn-style semantic `Rename Symbol` across external compiled DLLs.
- ❌ **Heuristic Inference**: Complex fluent method-chaining type inference (`list.Where(...).Select(...).First().`) relies on best-effort heuristic matching rather than a full bound symbol tree.

### Summary: Which One Should You Use?

| Use Case | Recommended Tool | Reason |
| :--- | :---: | :--- |
| **Interactive Typing & Code Navigation** | **UnityCSharpFastLSP** | Instant typing feel; zero lag on completion and hover. |
| **Autonomous AI Coding Agents** | **UnityCSharpFastLSP** | High-frequency tool execution with negligible CPU/memory overhead. |
| **Fast Prototyping / Broken Codebases** | **UnityCSharpFastLSP** | Always works, even when project compilation is broken. |
| **Final CI / Deep Semantic Refactoring** | **UnityCSharpLSP (Roslyn)** | Full compiler validation and cross-assembly symbol renames. |

---

## Getting Started

### Prerequisites
- [.NET 11 SDK](https://dotnet.microsoft.com/) (`11.0.100` or higher)

### Building from Source

```powershell
# Clone with submodules
git clone --recursive https://github.com/gadget114514/UnityCSharpFastLSP.git
cd UnityCSharpFastLSP

# Build in Release mode
dotnet build UnityCSharpFastLSP.sln -c Release

# Run tests
dotnet test tests/UnityCSharpFastLSP.Tests/UnityCSharpFastLSP.Tests.csproj -c Release
```

The compiled binary will be located at:  
`src/UnityCSharpFastLSP.Server/bin/Release/net11.0/UnityCSharpFastLSP.Server.exe`

---

## Editor & AI Agent Integration

### Generating Configuration Snippet
Run the server with `--print-config` to output a ready-to-use configuration snippet with your resolved binary path:

```bash
UnityCSharpFastLSP.Server.exe --print-config
```

Output:
```json
{
  "lsp": {
    "unity-csharp-fast-2nek": {
      "command": ["D:/path/to/UnityCSharpFastLSP.Server.exe", "--stdio"],
      "extensions": [".cs"]
    }
  }
}
```

### OpenCode (`opencode.json` / `opencode.jsonc`)
Add the snippet above directly into your `opencode.json`.

### Claude Code / Antigravity / Cursor
Refer to the [LLM Agent Skill Guide](skills/unity-fast-lsp/SKILL.md) for full setup instructions and shell invocation patterns.

### Using with LLM Agents via MCP

Besides the LSP server mode above, FastLSP can run as an **MCP (Model Context Protocol) server** (`--mcp`). Each tool maps 1:1 to an LSP method (`textDocument/definition` → `textDocument_definition`), with the same names, arguments, and result format as UnityCSharpLSP's MCP mode.

| Choose | When |
| :--- | :--- |
| **LSP** (`--stdio`, default) | Your editor or agent client speaks LSP natively (e.g. OpenCode `lsp` setting). |
| **MCP standalone** (`--mcp`) | Read-only navigation is enough: definition, references, symbols, hover, completion, document symbols. Fastest, no upstream needed. |
| **MCP proxy** (`--mcp --proxy`) | You also need rename, codeAction, type/call hierarchy, compiler diagnostics, or `unitycsharp.*` commands. Requires UnityCSharpLSP installed. |

Setup examples (replace `<exe>` and `<path>`):

```bash
# Claude Code: standalone / proxy
claude mcp add unity-csharp-fast-lsp -- <exe> --mcp --workspace <path>
claude mcp add unity-csharp-fast-lsp -- <exe> --mcp --proxy --workspace <path>
```

```json
// Claude Code (.mcp.json)
{
  "mcpServers": {
    "unity-csharp-fast-lsp": {
      "command": "<exe>",
      "args": ["--mcp", "--proxy", "--workspace", "<path>"]
    }
  }
}
```

```json
// OpenCode (opencode.json): add --proxy to command for fallback
{
  "mcp": {
    "unity-csharp-fast-lsp": {
      "type": "local",
      "command": ["<exe>", "--mcp", "--workspace", "<path>"],
      "enabled": true
    }
  }
}
```

Generate the snippet with your resolved binary path: `UnityCSharpFastLSP.Server.exe --print-config --mcp [--proxy] [--workspace <path>]`.

Which tools run where (details and known limitations in [doc/mcp.md](doc/mcp.md)):
- **FastLSP (standalone and proxy)**: `hover`, `definition`, `declaration`, `typeDefinition`, `implementation`, `references`, `documentSymbol`, `documentHighlight`, `signatureHelp`, `completion`, `workspace_symbol`, `textDocument_formatting`.
- **Empty-result fallback (proxy only, v0.3.1+)**: when a FastLSP fast-path request returns an empty result (`null`, `[]`, `{}`, or `""`), the proxy discards it and re-issues the request to UnityCSharpLSP. Non-empty fast results are still answered locally in **0.1–0.4 ms**.
- **Standalone returns error `-32601`** (with a hint to use `--proxy`): everything else, including rename and codeAction.
- **UnityCSharpLSP via proxy**: everything not in the FastLSP list above.

---

## Command-Line Options

```text
USAGE:
  UnityCSharpFastLSP.Server [options]

OPTIONS:
  --stdio              Use standard input/output for LSP JSON-RPC communication (default: true).
  --proxy              Enable Intelligent Hybrid Proxy mode.
                       - Supported features are answered immediately by FastLSP (<0.3ms).
                       - Empty FastLSP results (null / [] / {} / "") are retried on upstream.
                       - Unhandled heavy requests are forwarded to upstream Roslyn server.
                       - Upstream discovery: 1) --upstream, 2) UNITY_CSHARP_LSP_PATH,
                         3) OS PATH resolution, 4) Standard directories. (5: Fails with error).
  --upstream <path>    Explicit path to upstream Roslyn LSP server (e.g. UnityCSharpLSP.Server.exe).
                       If omitted with --proxy, automatically searches following priority 2 -> 3 -> 4.
  --mcp                Run as MCP (Model Context Protocol) server over stdio. See "Using with LLM Agents via MCP".
  --workspace <path>   MCP mode: workspace root (default: nearest ancestor containing Assets/ or ProjectSettings/).
  --settings <json>    MCP mode: settings sent with workspace/didChangeConfiguration at startup
                       (effective with --proxy only; @<file> to read a file).
  --timeout <sec>      MCP mode: timeout for each LSP request in seconds (default: 120).
  --print-config       Print LLM agent LSP configuration snippet (opencode.json format) to stdout and exit.
                       With --mcp, print the MCP configuration snippet instead.
  -h, --help           Show help description and exit.
```

### Intelligent Hybrid Proxy Priority
1. **Request Processing**:
   - **Fast Path (Local Answer)**: All features supported by FastLSP (`completion`, `hover`, `definition`, `declaration`, `implementation`, `references`, `formatting`, `signatureHelp`, `semanticTokens`, `documentSymbol`, `workspace/symbol`) are answered directly in **0.1ms – 0.4ms**.
   - **Empty-Result Fallback (v0.3.1+)**: If a FastLSP fast-path response is empty (`null`, `[]`, `{}`, or `""`), the proxy drops it and forwards the same request to upstream Roslyn, so the client still gets a full result. Non-empty responses never touch upstream, preserving the fast path.
   - **Heavy Path (Upstream Forward)**: Heavy operations (`rename`, `codeAction`, compiler diagnostics `CS0029`/`CS0103`) are forwarded to upstream Roslyn.
2. **Upstream Server Discovery Order**:
   1. Explicit path: `--upstream <path>`
   2. Environment variable: `UNITY_CSHARP_LSP_PATH`
   3. OS PATH resolution: OS searches PATH for `UnityCSharpLSP.Server.exe` or `UnityCSharpLSP.exe`
   4. Standard installation directories (`%LOCALAPPDATA%\Programs\UnityCSharpLSP`, etc.)
   5. **FAILED**: If no upstream server is found, terminates immediately with error code 1.

---

## Hancho Orchestration

This repository includes full task cards, worker instructions, and multi-job pipeline definitions managed via [Hancho](tasks/README.md):

```powershell
# Run the complete build, test, and benchmark verification pipeline
hancho-cli.exe --data-dir .hancho taskqueue add --file tasks/hancho/jobs-all-phases.json --taskqueue fastlsp-full-pipeline
hancho-cli.exe --data-dir .hancho taskqueue wait fastlsp-full-pipeline --since 0 --timeout-ms 300000
```

---

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
