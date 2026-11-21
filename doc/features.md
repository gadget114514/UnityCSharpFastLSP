# Feature Matrix: UnityCSharpFastLSP

This document provides a comprehensive overview of supported Language Server Protocol (LSP) methods, Unity-specific capabilities, and explicit non-goals (out-of-scope items) in **UnityCSharpFastLSP**.

For a detailed classification of all LSP 3.17 features into **Implemented**, **Feasible without latency degradation**, and **Infeasible (delegated to Roslyn proxy)**, see **[LSP Feature Plan](feature-plan.md)**.

---

## 1. Supported LSP Methods

| LSP Method | Protocol Kind | Support Status | Implementation Details |
| :--- | :--- | :---: | :--- |
| `initialize` | Request | **Full** | Returns capabilities (`textDocumentSync: 2` (Incremental), hover, completion, definition, references, documentSymbol). Starts async workspace indexing in the background. |
| `initialized` | Notification | **Full** | Acknowledgement notification from client. |
| `shutdown` | Request | **Full** | Transitions server to shutdown state, returns `null`. |
| `exit` | Notification | **Full** | Exits server process cleanly with exit code 0. |
| `textDocument/didOpen` | Notification | **Full** | Parses full text using csff CST, adds to in-memory `DocumentStore`, extracts symbols, and publishes initial diagnostics. |
| `textDocument/didChange` | Notification | **Full** | Applies incremental or full changes. Uses `IncrementalParser` for method/block edits (< 30 us) and falls back to full parse on structural changes. Triggers debounced diagnostics. |
| `textDocument/didClose` | Notification | **Full** | Cleans up document snapshots, cancels pending diagnostic tasks, and clears diagnostics. |
| `textDocument/publishDiagnostics` | Notification | **Full** | Pushed from server. Reports CST syntax errors (`CSFF001` unclosed syntax, `CSFF002` bad token). Debounced by 5 ms with latest-version cancellation. |
| `textDocument/documentSymbol` | Request | **Full** | Hierarchical document symbols (Namespace -> Class/Struct/Interface -> Methods/Properties/Fields/Enums) extracted from CST. |
| `textDocument/hover` | Request | **Full** | Returns symbol signature, containing type, and Unity MonoBehaviour lifecycle documentation in Markdown. |
| `textDocument/completion` | Request | **Full** | Provides dot-member filtering (`x.`) via lightweight type inference (`var x = new T()`, `T x`, `this.`, parameters, fields), Unity MonoBehaviour messages (13 methods with snippets), common Unity attributes (11 attributes), and workspace symbol names. |
| `textDocument/definition` | Request | **Full** | Navigates to definition locations using scoped AST declaration check, containing-type member lookup, and atomic `SymbolIndex` dictionary lookup. Supports file auto-loading. |
| `textDocument/references` | Request | **Full** | Finds symbol references and declarations across the workspace. |
| `textDocument/formatting` | Request | **Full** | Fast bracket indentation and whitespace normalization (`FastFormatter`). |
| `textDocument/signatureHelp` | Request | **Full** | Real-time parameter signatures and active parameter index tracking for methods (`(` and `,` triggers). |
| `textDocument/semanticTokens/full` | Request | **Full** | Standard LSP delta-encoded semantic tokens for classes, structs, interfaces, enums, methods, properties, variables, keywords, strings, numbers, and comments. |
| `textDocument/documentHighlight` | Request | **Full** | Fast document-wide identifier occurrence highlighting. |
| `workspace/symbol` | Request | **Full** | High-speed workspace symbol search with pattern extensions (wildcards `*`/`?`, CamelCase `PC` -> `PlayerController`, container-qualified `Player.Update`, substring and fuzzy matching). |

---

## 2. Incremental Parsing Mechanism

Unlike Roslyn which maintains heavy semantic trees, UnityCSharpFastLSP implements a high-speed incremental parser:
- **Local Edit Detection**: Inspects the modification span. If the edit occurs strictly inside a method body or block (statements/expressions), only the modified section is re-tokenized, and token offsets of subsequent nodes are shifted (`delta = newLength - oldLength`).
- **Safety Fallback**: If structural tokens (`class`, `struct`, `interface`, `#if`, etc.) or unbalanced braces are detected, the system immediately and safely falls back to full re-parsing via csff.
- **Latency**: Local edits are processed in **under 30 microseconds**, eliminating keystroke lag during continuous typing.

---

## 3. Unity-Specific Features

1. **MonoBehaviour Lifecycle Messages**:
   - Built-in completion templates and documentation for:
     `Awake`, `Start`, `Update`, `FixedUpdate`, `LateUpdate`, `OnEnable`, `OnDisable`, `OnDestroy`, `OnGUI`, `OnTriggerEnter`, `OnTriggerExit`, `OnCollisionEnter`, `OnCollisionExit`.
2. **Common Unity Attributes**:
   - Autocompletion for:
     `SerializeField`, `HideInInspector`, `RequireComponent`, `Header`, `Tooltip`, `Range`, `Space`, `DisallowMultipleComponent`, `ExecuteInEditMode`, `CreateAssetMenu`, `MenuItem`.
3. **Seeded Preprocessor Symbols**:
   - Supports conditional compilation symbols such as `UNITY_EDITOR` for correct CST branch selection.

---

## 4. Explicit Non-Goals (Out of Scope by Design)

To achieve **10x to 100x lower warm per-request latency** compared to Roslyn-based servers, the following features are intentionally excluded:

| Feature | Reason for Exclusion |
| :--- | :--- |
| **Complete Roslyn Semantic Binding** | Loading external metadata DLLs (`UnityEngine.dll`, BCL) and computing deep generic constraints or overload resolution takes tens of milliseconds. |
| **Type-Mismatch Diagnostics** | FastLSP focuses on instantaneous syntax errors and structure recovery; type validation is left to compiler/editor build steps. |
| **Unity YAML / Prefab / Scene Indexing** | Handled by asset daemons or external tools. FastLSP focuses strictly on pure C# source files (`*.cs`). |
| **.asmdef Assembly Boundary Isolation** | All `.cs` files in `Assets/` and `Packages/` are indexed in a unified flat symbol table for maximum lookup speed. |
