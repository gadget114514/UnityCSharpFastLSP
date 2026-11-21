---
name: unity-fast-lsp
description: Guide for LLMs and AI agents (OpenCode, Claude Code, Cursor, Antigravity) on discovering, configuring, and leveraging UnityCSharpFastLSP for ultra-fast C# intelligence in Unity projects. Use this skill when configuring a lightweight C# language server, querying symbol definitions, extracting document outlines, or integrating with Unity project codebases.
---

# UnityCSharpFastLSP — LLM & AI Agent Usage Guide

`UnityCSharpFastLSP` is an ultra-fast, lightweight C# Language Server Protocol (LSP) server tailored specifically for Unity projects. It operates independently of the Unity Editor and Roslyn, providing sub-millisecond completion, hover, and syntax diagnostics.

---

## 1. Finding the Executable

Search in this order:

1. `UnityCSharpFastLSP.Server.exe` on `PATH`
2. `%LOCALAPPDATA%\Programs\UnityCSharpFastLSP\`
3. Repo output: `<repo-root>/src/UnityCSharpFastLSP.Server/bin/Release/net11.0/UnityCSharpFastLSP.Server.exe`

```powershell
where.exe UnityCSharpFastLSP.Server.exe
```

---

## 2. Generating & Verifying LSP Configuration

Run `--print-config` on the server executable to automatically output the ready-to-use OpenCode / AI agent configuration snippet with the resolved binary path:

```bash
UnityCSharpFastLSP.Server.exe --print-config
```

### Config Format for `opencode.json` / `opencode.jsonc`:
```json
{
  "lsp": {
    "unity-csharp-fast-2nek": {
      "command": ["<path/to/UnityCSharpFastLSP.Server.exe>", "--stdio"],
      "extensions": [".cs"]
    }
  }
}
```

- **Standard I/O**: `--stdio` is the default communication mode.
- **Strict Stream Separation**: `stdout` is exclusively reserved for JSON-RPC 2.0 framing. All server logs and diagnostic traces output to `stderr`.

---

## 3. Supported Features & Use Cases

| Goal | LSP Method | Notes |
| :--- | :--- | :--- |
| **Inspect File Outline** | `textDocument/documentSymbol` | Returns hierarchical symbols (Namespace, Class, Struct, Method, Property, Field). |
| **Hover on Symbol** | `textDocument/hover` | Shows symbol signature and MonoBehaviour lifecycle documentation (e.g. `Update()`, `Awake()`). |
| **Autocompletion** | `textDocument/completion` | Offers 13 built-in Unity message templates, 11 common Unity attributes (e.g. `[SerializeField]`), and local symbols. |
| **Jump to Definition** | `textDocument/definition` | Navigates to declarations across the workspace. |
| **Find References** | `textDocument/references` | Locates usages across all indexed `.cs` files in `Assets/` and `Packages/`. |
| **Syntax Diagnostics** | `textDocument/publishDiagnostics` | Live syntax checking with sub-millisecond incremental updates during typing. |

---

## 4. Key Differences from Heavy Roslyn Servers

- **Speed**: Completion response in **~0.15 ms**, Hover in **~0.1 ms**, Keystroke incremental update in **< 30 us**.
- **No Heavy Background Compilations**: Does not freeze CPU or consume gigabytes of RAM scanning Roslyn analyzers.
- **Unity Built-ins**: Direct support for common Unity lifecycle methods and serialization attributes without needing project `.csproj` or Unity compilation.
- **Limitations**: Does not evaluate deep generic type constraints or compiler type-mismatch diagnostics.
