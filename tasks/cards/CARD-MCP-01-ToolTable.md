# [CARD-MCP-01] MCP ツール表（McpToolTable）

- **対象**: `src/UnityCSharpFastLSP.Server/Mcp/McpToolTable.cs`（新規）
- **依存**: CARD-MCP-00（必読）
- **仕様**: `doc/mcp-csharp-lsp.md` §4, §5

## 1. 目的
仕様 §4 の表をそのままコードのデータとして持つ。MCP `tools/list` の応答と `tools/call` のディスパッチはこの表だけを見る。ツールを 1 行足せば公開できる構造にする。

## 2. 実装要件

```csharp
public enum McpToolKind { LspRequest, LspNotification, Reset, DiagnosticsCached }

public sealed record McpTool(
    string Name,            // 例 "textDocument_definition"
    string? LspMethod,      // 例 "textDocument/definition"（Reset/DiagnosticsCached は null）
    McpToolKind Kind,
    bool ReadOnly,
    string Description,
    JsonObject InputSchema);

public static class McpToolTable
{
    public static IReadOnlyList<McpTool> All { get; }
    public static McpTool? Find(string name);
    public static JsonObject ToListEntry(McpTool tool); // {name, description, inputSchema, annotations?}
}
```

### 2.1 ツール一覧（仕様 §4 と完全一致させる）
- §4.1 読み取り系（`ReadOnly = true`）: `textDocument_hover`, `_definition`, `_declaration`, `_typeDefinition`, `_implementation`, `_references`, `_documentSymbol`, `_documentHighlight`, `_signatureHelp`, `_completion`, `completionItem_resolve`, `textDocument_diagnostic`, `workspace_diagnostic`, `workspace_symbol`, `workspaceSymbol_resolve`, `textDocument_prepareCallHierarchy`, `callHierarchy_incomingCalls`, `callHierarchy_outgoingCalls`, `textDocument_prepareTypeHierarchy`, `typeHierarchy_supertypes`, `typeHierarchy_subtypes`, `textDocument_codeLens`, `textDocument_documentLink`, `textDocument_inlayHint`, `textDocument_moniker`, `textDocument_prepareRename`
- §4.2 編集提案系（`ReadOnly = false`）: `textDocument_rename`, `textDocument_codeAction`, `codeAction_resolve`, `textDocument_formatting`, `textDocument_rangeFormatting`, `workspace_willRenameFiles`, `workspace_willCreateFiles`, `workspace_willDeleteFiles`
- §4.3: `workspace_executeCommand`（LspRequest）, `workspace_didChangeConfiguration`（LspNotification, method `workspace/didChangeConfiguration`）, `lsp_reset`（Reset）
- 追加: `lsp_diagnostics_cached`（DiagnosticsCached, ReadOnly）。引数 `{ uri?: string }`。

### 2.2 inputSchema（手書き。部品化して重複を避ける）
- 部品: `TextDocument`（`{uri: string}` required uri。説明「file:/// URI, absolute path, or workspace-relative path」）、`Position`（`{line, character}` integer ≥0, required 両方。説明「0-based line / UTF-16 code unit offset」）、`Range`（start/end）。
- 位置系（hover, definition, declaration, typeDefinition, implementation, documentHighlight, prepareCallHierarchy, prepareTypeHierarchy, moniker, prepareRename）: `{textDocument, position}` required 両方。
- references: 位置系 + `context: {includeDeclaration: boolean}` + `includePatterns: string[]` + `excludePatterns: string[]`（説明: glob。UnityCSharpLSP 拡張）。
- completion: 位置系 + `context: {triggerKind: integer, triggerCharacter: string}`。signatureHelp: 位置系 + `context: object`。
- documentSymbol / codeLens / documentLink: `{textDocument}`。diagnostic: `{textDocument, identifier?, previousResultId?}`。
- workspace_diagnostic: `{identifier?, previousResultIds: array}` required previousResultIds。workspace_symbol: `{query: string}` required。
- resolve 系（completionItem_resolve, workspaceSymbol_resolve, codeAction_resolve）: 対象 LSP オブジェクトそのもの（`type: object`, `additionalProperties: true`。completionItem は required `label`、codeAction は required `title`、workspaceSymbol は required `name`）。
- callHierarchy_* / typeHierarchy_*: `{item: object}` required item（説明: prepare* の結果の要素を渡す）。
- inlayHint: `{textDocument, range}`。rename: 位置系 + `newName: string`。
- codeAction: `{textDocument, range, context: {diagnostics: array, only: string[], triggerKind: integer}}` required 3 つ（context は required diagnostics）。
- formatting: `{textDocument, options: {tabSize: integer, insertSpaces: boolean}}`（options は additionalProperties true）。rangeFormatting: + range。
- willRenameFiles: `{files: [{oldUri, newUri}]}`。willCreate/DeleteFiles: `{files: [{uri}]}`。
- executeCommand: `{command: string enum, arguments: array}` required command。enum = `unitycsharp.reloadWorkspace`, `unitycsharp.showWorkspaceInfo`, `unitycsharp.getSceneOrPrefabHierarchy`, `unitycsharp.findSymbolUsagesInAssets`, `unitycsharp.findGuidReferences`, `unitycsharp.listReferencedGuids`, `unitycsharp.assetServerStatus`（UnityCSharpLSP `ExecuteCommandHandler.Commands` と同一）。
- didChangeConfiguration: `{settings: {}}`（任意の JSON）required settings。lsp_reset: `{}`。
- 全スキーマ `"type": "object"`。

### 2.3 description
LSP 仕様の 1 行要約（英語）+ 共通注記「Positions are 0-based (line, UTF-16 character). textDocument.uri may be a file:/// URI, an absolute path, or a workspace-relative path.」（位置/uri を取るツールのみ）。

### 2.4 ToListEntry
`{ "name", "description", "inputSchema", "annotations": { "readOnlyHint": true } }`（annotations は ReadOnly のときだけ）。毎回 `DeepClone` して返す（呼び出し側で改変されても表が壊れないように）。

## 3. 完了条件
1. 表のツール数 = §4.1(26) + §4.2(8) + §4.3(3) + 1 = **38**。名前重複なし。
2. `dotnet build UnityCSharpFastLSP.sln -c Release` が警告 0 / エラー 0。
