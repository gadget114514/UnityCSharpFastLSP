# UnityCSharpFastLSP

**Ultra-Fast, Lightweight C# Language Server Protocol (LSP) Server for Unity.**  
Built on top of [csff](https://github.com/paq/csff) CST (Concrete Syntax Tree) and .NET 11.

---

## Overview

Traditional Roslyn-based C# language servers (`OmniSharp`, `csharp-ls`) incur noticeable latency spikes (10–50 ms per request), high memory footprints, and heavy compilation pauses when navigating large Unity projects with numerous assemblies and package caches.

**UnityCSharpFastLSP** is engineered to eliminate editing friction:
- **Sub-Millisecond Response Time**: Warm per-request latency of **~0.15 ms for completion** and **~0.10 ms for hover** in real stdio IPC (**~90x faster** than Roslyn baseline).
- **Instant Incremental Parsing**: Keystrokes inside method bodies update in **under 30 microseconds** using token-shifted CST synthesis.
- **Unity-Centric Intelligence**: Built-in awareness of MonoBehaviour lifecycle messages (13 templates), serialization attributes, and `#if UNITY_EDITOR` preprocessor branching.
- **Zero Roslyn Overhead**: Operates without generated `.csproj`/`.sln` files, external DLL binding overhead, or heavy background Roslyn compilations.

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

- **[Feature Matrix](doc/features.md)**: Supported LSP methods, Unity capabilities, and design non-goals.
- **[Feature Comparison vs UnityCSharpLSP](doc/feature-comparison.md)**: In-depth comparison with Roslyn-based UnityCSharpLSP and the practical value of FastLSP.
- **[Performance Comparison Report](doc/performance-comparison.md)**: Benchmarks across in-process logic and real stdio IPC.

- **LSP Methods**: `initialize`, `shutdown`, `exit`, `didOpen`, `didChange` (incremental/full), `didClose`, `publishDiagnostics`, `documentSymbol`, `hover`, `completion`, `definition`, `references`.
- **CST Syntax Diagnostics**: Instantaneous detection of incomplete syntax elements (`CSFF001`) and bad tokens (`CSFF002`).
- **Hierarchical Document Symbols**: Fast extraction of Namespace -> Type -> Member symbol trees.
- **Workspace Indexing**: Background parallel scanning of all `.cs` files across `Assets/` and `Packages/`.
- **LLM Agent Ready**: Self-describing `--print-config` and `--help` for direct integration with AI coding agents.

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
    "unity-csharp-fast": {
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

---

## Command-Line Options

```text
USAGE:
  UnityCSharpFastLSP.Server [options]

OPTIONS:
  --stdio          Use standard input/output for LSP JSON-RPC communication (default: true).
  --print-config   Print LLM agent LSP configuration snippet (opencode.json format) to stdout and exit.
  -h, --help       Show help description and exit.
```

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
