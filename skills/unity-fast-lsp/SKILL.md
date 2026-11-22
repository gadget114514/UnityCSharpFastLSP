---
name: unity-fast-lsp
description: Guide for LLMs and AI agents (OpenCode, Claude Code, Cursor, Antigravity) on discovering, configuring, and leveraging UnityCSharpFastLSP for ultra-fast C# intelligence in Unity projects. Covers both the LSP server mode and the MCP server mode (--mcp, MCP tool calls such as textDocument_definition, workspace_symbol, lsp_reset), including the --proxy fallback to UnityCSharpLSP for rename, codeAction, call/type hierarchy and unitycsharp.* commands. Use this skill when configuring a lightweight C# language server or MCP server, registering it with Claude Code or OpenCode, querying symbol definitions or references, extracting document outlines, calling MCP tools, or integrating with Unity project codebases.
---

# UnityCSharpFastLSP — LLM & AI Agent Usage Guide

`UnityCSharpFastLSP` is an ultra-fast, lightweight C# Language Server Protocol (LSP) server tailored specifically for Unity projects. It operates independently of the Unity Editor and Roslyn, providing sub-millisecond completion, hover, and syntax diagnostics. It can run as an LSP server (`--stdio`) or as an MCP server (`--mcp`). Section 5 covers MCP.

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
| **Autocompletion** | `textDocument/completion` | Offers dot-member completion (`obj.`) via lightweight scoped type inference, 13 built-in Unity message templates, 11 common Unity attributes (e.g. `[SerializeField]`), and local symbols. |
| **Jump to Definition** | `textDocument/definition` | Navigates to declarations across the workspace and local scopes. |
| **Find References** | `textDocument/references` | Locates usages across all indexed `.cs` files in `Assets/` and `Packages/`. |
| **Signature Help** | `textDocument/signatureHelp` | Displays active parameter index and parameter definitions while typing method calls. |
| **Document Formatting** | `textDocument/formatting` | Fast bracket indentation and whitespace normalization (< 1 ms). |
| **Semantic Highlighting** | `textDocument/semanticTokens/full` | High-speed delta-encoded semantic coloring for types, members, and keywords. |
| **Document Highlight** | `textDocument/documentHighlight` | Highlights all occurrences of the identifier under cursor. |
| **Syntax Diagnostics** | `textDocument/publishDiagnostics` | Live syntax checking with sub-millisecond incremental updates during typing. |

---

## 4. Key Differences from Heavy Roslyn Servers

- **Speed**: Completion response in **~0.15 ms**, Hover in **~0.1 ms**, Keystroke incremental update in **< 30 us**.
- **No Heavy Background Compilations**: Does not freeze CPU or consume gigabytes of RAM scanning Roslyn analyzers.
- **Unity Built-ins**: Direct support for common Unity lifecycle methods and serialization attributes without needing project `.csproj` or Unity compilation.
- **Limitations**: Does not evaluate deep generic type constraints or compiler type-mismatch diagnostics.

---

## 5. MCP Server Mode (`--mcp`)

The same server can run as an MCP server. Tools map 1:1 to LSP methods, so the tool names, arguments, and result format are the same as UnityCSharpLSP's MCP mode.

### 5.1 Mode selection

| Mode | Launch | Use when | Tools answered |
| :--- | :--- | :--- | :--- |
| **LSP** | `UnityCSharpFastLSP.Server.exe --stdio` (default) | The editor or agent speaks LSP natively. | Standard LSP methods |
| **MCP standalone** | `--mcp --workspace <path>` | Read-only navigation: definition, references, symbols, hover, completion, outlines. Fastest, no upstream needed. | `hover`, `definition`, `declaration`, `typeDefinition`, `implementation`, `references`, `documentSymbol`, `documentHighlight`, `signatureHelp`, `completion`, `workspace_symbol`, `textDocument_formatting` |
| **MCP proxy** | `--mcp --proxy [--upstream <path>] --workspace <path>` | Also needs rename, codeAction, type/call hierarchy, compiler diagnostics, or `unitycsharp.*` commands. Requires UnityCSharpLSP. | Everything above, plus the rest via UnityCSharpLSP |

Pick **standalone** first. Switch to **proxy** only when a tool returns error `-32601` with a `--proxy` hint.

### 5.2 Registering the MCP server

Generate the snippet with the resolved binary path:

```powershell
UnityCSharpFastLSP.Server.exe --print-config --mcp --workspace <path>
UnityCSharpFastLSP.Server.exe --print-config --mcp --proxy --workspace <path>
```

Claude Code:

```powershell
claude mcp add unity-csharp-fast-lsp -- <path/to/UnityCSharpFastLSP.Server.exe> --mcp --workspace <path>
```

OpenCode (`opencode.json`):

```json
{
  "mcp": {
    "unity-csharp-fast-lsp": {
      "type": "local",
      "command": ["<path/to/UnityCSharpFastLSP.Server.exe>", "--mcp", "--workspace", "<path>"],
      "enabled": true
    }
  }
}
```

Add `"--proxy"` to `args`/`command` for the UnityCSharpLSP fallback.

### 5.3 Tool quick reference

Tool name = LSP method with `/` replaced by `_`. Positions are 0-based (line, UTF-16 character). `textDocument.uri` accepts `file:///` URIs, absolute paths, or workspace-relative paths. Files are synced from disk automatically, so `didOpen` is not needed.

| Goal | Tool | Minimal arguments |
| :--- | :--- | :--- |
| Jump to definition | `textDocument_definition` | `{"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":10,"character":8}}` |
| File outline | `textDocument_documentSymbol` | `{"textDocument":{"uri":"Assets/Scripts/Player.cs"}}` |
| Search symbols by name | `workspace_symbol` | `{"query":"Player"}` |
| Find references | `textDocument_references` | `{"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":10,"character":8},"context":{"includeDeclaration":true}}` |
| Hover info | `textDocument_hover` | `{"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":10,"character":8}}` |
| Rename (proxy) | `textDocument_rename` | `{"textDocument":{"uri":"Assets/Scripts/Player.cs"},"position":{"line":10,"character":8},"newName":"PlayerController"}` |
| Asset usages (proxy) | `workspace_executeCommand` | `{"command":"unitycsharp.findSymbolUsagesInAssets","arguments":[...]}`. The `arguments` shape is defined in the UnityCSharpLSP documentation. Check it there rather than guessing. |
| Where is a script/asset used in scenes & prefabs | `unity_guid_references` | `{"asset":"Assets/Scripts/Player.cs"}` or `{"guid":"0123456789abcdef0123456789abcdef"}` |
| What asset does a GUID point to | `unity_guid_definition` | `{"guid":"0123456789abcdef0123456789abcdef"}` |
| Reload after bulk edits | `lsp_reset` | `{}` |
| Cached diagnostics | `lsp_diagnostics_cached` | `{}` or `{"uri":"Assets/Scripts/Player.cs"}` |

Rename, codeAction, and formatting tools return a `WorkspaceEdit` or `TextEdit[]` only. They do not modify files. Apply the edit with your own file editing tool.

### 5.4 Recommended workflow

1. `workspace_symbol` to find the symbol and its location.
2. `textDocument_definition` or `textDocument_references` at that position.
3. Edit files with your own tools.
4. After a bulk edit, call `lsp_reset` to reload the workspace (proxy mode reloads UnityCSharpLSP too).

### 5.5 Errors and fallback

| Result | Meaning | Action |
| :--- | :--- | :--- |
| `isError: true`, text `{"code":-32601,"message":"... run with --mcp --proxy ..."}` | Tool is not supported by standalone mode | Restart the server with `--mcp --proxy` |
| `isError: true`, text `{"code":-32603,"message":"Upstream Roslyn LSP server could not be started..."}` | UnityCSharpLSP not found | Set `--upstream <path>` or `UNITY_CSHARP_LSP_PATH` |
| `isError: true`, text `{"code":-32603,"message":"Upstream UnityCSharpLSP exited (code N)."}` | UnityCSharpLSP crashed | Restart the MCP server |
| `isError: true`, text `{"code":-32603,"message":"Request '...' timed out after N s."}` | No response within `--timeout` (default 120 s) | Retry, or raise `--timeout` |
| `null` as text | No result (LSP meaning: "not found") | Not an error |
| JSON-RPC error `-32602` | Unknown tool name | Check the tool name against section 5.3 |

### 5.6 Known differences in standalone mode

- `textDocument_references` ignores `includePatterns` and `excludePatterns` (FastLSP does not interpret them).
- `workspace_didChangeConfiguration` and `--settings` have no effect in standalone mode. Use `--proxy`.
- `lsp_reset` in standalone mode closes documents but does not reload the workspace.

Full reference: `doc/mcp.md` in the repository.
