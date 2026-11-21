# Feature Comparison: UnityCSharpFastLSP vs. UnityCSharpLSP

This document provides an honest, rigorous, and technical comparison between **UnityCSharpFastLSP** (this project, csff CST-based) and **UnityCSharpLSP** (Roslyn-based with Unity Asset Daemon).

---

## 1. Feature Matrix

| Capability | UnityCSharpLSP (Roslyn-based) | UnityCSharpFastLSP (csff CST-based) | Technical Impact & Practical Meaning |
| :--- | :---: | :---: | :--- |
| **Syntax Diagnostics** | **Full** (Roslyn parser) | **Full** (csff CST parser) | Both detect syntax errors (`CSFF001` unclosed braces, `CSFF002` bad tokens, missing semicolons, malformed declarations). FastLSP reports syntax errors in < 50 us. |
| **Type & Semantic Diagnostics** | **Full** (CS0103 undeclared vars, CS0029 type mismatch, CS1503 arg types) | **None (Excluded by design)** | **Crucial difference**: FastLSP does NOT show red squigglies for type errors or calling non-existent methods. Compiler build is required to catch type errors. |
| **Member Access Completion (`obj.`)** | **Precise** (Resolves type of `obj` via Roslyn and lists exact members) | **Lightweight Scoped Inference** (`ScopedResolver`) | FastLSP resolves `obj`'s type using fast local inference (`var x = new T()`, `T x`, `this.`, parameters, fields) and filters candidates strictly to `obj`'s type members across the workspace index. |
| **Unity Lifecycle Completion** | Supported | **Built-in Templates** (13 methods with snippets) | Both provide snippets for `Start()`, `Update()`, `Awake()`, etc. FastLSP returns in 0.15 ms. |
| **Hover Information** | **Full** (Resolved type, XML docs, generic constraints) | **CST Signature + Unity Docs** | FastLSP displays `Kind Name`, containing type, and Markdown docs for Unity MonoBehaviour messages. FastLSP responds in 0.10 ms. |
| **Go to Definition** | **Exact** (Resolves across overloads & namespace scopes) | **Name-Matched** ($O(1)$ dictionary lookup) | FastLSP jumps instantly to declarations sharing the same identifier. Overload ambiguity goes to the primary match. |
| **Find References** | **Exact** (Type-safe semantic references) | **Identifier References** (Symbol name matching) | FastLSP finds all occurrences of the identifier across workspace `.cs` files. |
| **Unity YAML (.prefab/.unity)** | **Full** (Asset Daemon indexes GUIDs, components, overrides) | **None (Out of Scope)** | FastLSP strictly analyzes `.cs` source code and does not parse YAML assets. |
| **Incremental Parsing** | Roslyn syntax tree diff | **Method/Block Shift (< 30 us)** | FastLSP updates syntax snapshots locally without triggering global re-compilations. |
| **Memory Footprint** | Large (hundreds of MBs to GBs for Roslyn compilations) | **Minimal (< 30 MB)** | FastLSP stores flat structs and interned string arrays. |
| **Warm Completion Latency** | ~13.8 ms | **~0.15 ms (~90x faster)** | FastLSP avoids external metadata DLL binding and type inference. |
| **Cold Start (`initialize`)** | ~771 ms | **~481 ms (~38% faster)** | FastLSP responds to initialize immediately and indexes files in the background. |

---

## 2. Is FastLSP Meaningful as a C# Language Server?

### The Core Dilemma: "If it doesn't do semantic type-checking, what is its value?"

If an LSP server only provides syntax errors and name-based lookups without Roslyn's full type system, **is it actually useful for C# development?**

The short and honest answer:
- **For strict type safety and refactoring**: **No.** A Roslyn-based server (like `UnityCSharpLSP` or `csharp-ls`) is indispensable when you need compiler-level guarantees, rename refactoring across complex overloads, or instant red squigglies when passing the wrong argument type.
- **For AI Agents (OpenCode, Claude, Cursor) & Rapid Editing**: **Yes, exceptionally so.**

---

## 3. Why FastLSP Exists: The Two Distinct Use Cases

### Scenario A: Heavy IDE Development (Use `UnityCSharpLSP`)
When a human developer is writing intricate domain logic in VS Code / Visual Studio:
1. They need red squigglies the moment a type signature mismatches.
2. They rely on `player.` showing *only* the methods declared on `Player`.
3. They need to know which `.prefab` references their script.
- **Verdict**: Use **UnityCSharpLSP**. The 10–50 ms latency and 500 MB RAM usage are acceptable costs for full semantic correctness.

### Scenario B: AI Agent Iteration & Fast Editor Typing (Use `UnityCSharpFastLSP`)
When an AI agent (such as OpenCode or Claude) is reading, modifying, and generating C# scripts in loops:
1. **Latency Kills LLM Throughput**: Waiting 15–50 ms per LSP request across hundreds of automated queries stalls the agent pipeline. FastLSP responds in **0.1 ms**.
2. **Crash-Resilience**: AI agents often produce temporarily incomplete code (`public class C { void M(`). Roslyn parsers can hang or degrade performance on broken syntax; csff CST recovers immediately with `IsComplete = false` in **25 microseconds**.
3. **Low Resource Footprint**: Running background CLI agents alongside Unity Editor and local LLMs easily exhausts memory. FastLSP runs with practically zero CPU and memory footprint.
4. **Compiler Verification as Final Gate**: Agents typically verify code by running `dotnet build` or Unity batch build at the end of a task rather than needing the LSP server to simulate a full compiler continuously on every keystroke.

---

## 4. Implemented Lightweight Semantic Intelligence & Future Roadmap

FastLSP is not just a flat identifier matcher. It incorporates high-speed structural inference:

### What Is Already Implemented (v0.2.0):
1. **Lightweight Type Inference for Local Variables & Parameters**:
   - Deduces `x`'s type from `var x = new Foo()`, `Foo x = ...;`, `(Foo target)`, and fields (`[SerializeField] private Foo _foo;`).
2. **Member Access Filtering on Dots (`x.`)**:
   - Once `x` is resolved to `Foo`, completion candidates are filtered strictly to members belonging to `Foo` (methods, properties, fields) via `ScopedResolver` and `SymbolIndex.FindMembersByContainingType`.
3. **`this.` Self-Type Resolution**:
   - Resolves `this.` to the enclosing class or struct.
4. **Fast Indentation Formatting (`textDocument/formatting`)**:
   - Re-indents braces and statements in < 1 ms without Roslyn.

### Next Steps for Further Semantic Power:
1. **Type Inheritance Chaining (`class A : B`)**:
   - Traverse base class links in `SymbolIndex` to merge base members during dot completion (~2–5 µs overhead).
2. **Pre-baked Unity API Dictionary**:
   - In-memory trie of common Unity engine classes (`Transform`, `GameObject`, `Vector3`, `Mathf`) for instant completion without loading DLLs.
3. **Signature Help (`textDocument/signatureHelp`)**:
   - Display parameter signatures and active parameter indices while typing method calls.
