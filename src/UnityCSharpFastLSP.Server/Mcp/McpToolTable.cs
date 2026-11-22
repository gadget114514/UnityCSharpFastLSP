using System.Text.Json.Nodes;

namespace UnityCSharpFastLSP.Server.Mcp;

/// <summary>MCP ツールの種別。</summary>
public enum McpToolKind
{
    /// <summary>LSP リクエストを 1 対 1 で転送する。</summary>
    LspRequest,

    /// <summary>LSP 通知を送り、結果は null を返す。</summary>
    LspNotification,

    /// <summary>ブリッジ独自: 全ドキュメント didClose + unitycsharp.reloadWorkspace。</summary>
    Reset,

    /// <summary>ブリッジ独自: publishDiagnostics のキャッシュを返す。</summary>
    DiagnosticsCached,
}

/// <summary>MCP に公開する 1 ツール。LspMethod は Reset / DiagnosticsCached では null。</summary>
public sealed record McpTool(
    string Name,
    string? LspMethod,
    McpToolKind Kind,
    bool ReadOnly,
    string Description,
    JsonObject InputSchema);

/// <summary>
/// 公開する MCP ツールの一覧（仕様 doc/mcp-csharp-lsp.md §4）。
/// tools/list と tools/call はこの表だけを見る。ツールを 1 行足せば公開できる構造。
/// </summary>
public static class McpToolTable
{
    private const string PositionNote =
        "Positions are 0-based (line, UTF-16 character). textDocument.uri may be a file:/// URI, an absolute path, or a workspace-relative path.";

    /// <summary>UnityCSharpLSP の <c>ExecuteCommandHandler.Commands</c> と同一。</summary>
    private static readonly string[] ExecuteCommands =
    [
        "unitycsharp.reloadWorkspace",
        "unitycsharp.showWorkspaceInfo",
        "unitycsharp.getSceneOrPrefabHierarchy",
        "unitycsharp.findSymbolUsagesInAssets",
        "unitycsharp.findGuidReferences",
        "unitycsharp.listReferencedGuids",
        "unitycsharp.assetServerStatus",
    ];

    private static readonly IReadOnlyList<McpTool> Tools = BuildTools();

    private static readonly IReadOnlyDictionary<string, McpTool> ByName =
        Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>公開ツールの全件（仕様順）。</summary>
    public static IReadOnlyList<McpTool> All => Tools;

    /// <summary>名前でツールを探す。見つからなければ null。</summary>
    public static McpTool? Find(string name) => ByName.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>tools/list の 1 要素。呼び出し側が改変しても表が壊れないよう毎回複製する。</summary>
    public static JsonObject ToListEntry(McpTool tool)
    {
        var entry = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = tool.InputSchema.DeepClone(),
        };
        if (tool.ReadOnly)
        {
            entry["annotations"] = new JsonObject { ["readOnlyHint"] = true };
        }
        return entry;
    }

    private static List<McpTool> BuildTools() =>
    [
        // ---- 4.1 読み取り系 ----
        Lsp("textDocument/hover", true, "Get hover information (type and documentation) at a position.", AtPosition()),
        Lsp("textDocument/definition", true, "Go to the definition of the symbol at a position.", AtPosition()),
        Lsp("textDocument/declaration", true, "Go to the declaration of the symbol at a position.", AtPosition()),
        Lsp("textDocument/typeDefinition", true, "Go to the type definition of the symbol at a position.", AtPosition()),
        Lsp("textDocument/implementation", true, "Find implementations of the symbol at a position.", AtPosition()),
        Lsp("textDocument/references", true, "Find all references to the symbol at a position.", AtPosition(
            F("context", Obj(F("includeDeclaration", Bool("Include the declaration of the symbol in the results"), true))),
            F("includePatterns", StrArray("Glob patterns of files to include (UnityCSharpLSP extension)")),
            F("excludePatterns", StrArray("Glob patterns of files to exclude (UnityCSharpLSP extension)")))),
        Lsp("textDocument/documentSymbol", true, "List the symbols declared in a document.", Obj(TD())),
        Lsp("textDocument/documentHighlight", true, "Highlight all occurrences of the symbol at a position within the document.", AtPosition()),
        Lsp("textDocument/signatureHelp", true, "Get signature help for the call at a position.", AtPosition(
            F("context", ObjectNode("Signature help context (optional)")))),
        Lsp("textDocument/completion", true, "Get completion items at a position.", AtPosition(
            F("context", Obj(
                F("triggerKind", Int("CompletionTriggerKind: 1=Invoked, 2=TriggerCharacter, 3=TriggerForIncompleteCompletions"), true),
                F("triggerCharacter", Str("Character that triggered completion, if any")))))),
        Lsp("completionItem/resolve", true, "Resolve additional details (documentation, edits) for a completion item.",
            Resolve("label", "CompletionItem object as returned by textDocument/completion")),
        Lsp("textDocument/diagnostic", true, "Pull diagnostics for a document.", Obj(
            TD(),
            F("identifier", Str("Diagnostic provider identifier")),
            F("previousResultId", Str("Result id from a previous pull, if any")))),
        Lsp("workspace/diagnostic", true, "Pull diagnostics for the whole workspace.", Obj(
            F("identifier", Str("Diagnostic provider identifier")),
            F("previousResultIds", ObjArray("Previous result ids (PreviousResultId objects)"), true))),
        Lsp("workspace/symbol", true, "Search symbols across the workspace by name.", Obj(
            F("query", Str("Search query (symbol name or pattern)"), true))),
        Lsp("workspaceSymbol/resolve", true, "Resolve additional details (location) for a workspace symbol.",
            Resolve("name", "WorkspaceSymbol object as returned by workspace/symbol")),
        Lsp("textDocument/prepareCallHierarchy", true, "Prepare call hierarchy items for the symbol at a position.", AtPosition()),
        Lsp("callHierarchy/incomingCalls", true, "Find the callers of a call hierarchy item.", ItemParams("CallHierarchyItem returned by textDocument/prepareCallHierarchy")),
        Lsp("callHierarchy/outgoingCalls", true, "Find the callees of a call hierarchy item.", ItemParams("CallHierarchyItem returned by textDocument/prepareCallHierarchy")),
        Lsp("textDocument/prepareTypeHierarchy", true, "Prepare type hierarchy items for the type at a position.", AtPosition()),
        Lsp("typeHierarchy/supertypes", true, "Find the supertypes of a type hierarchy item.", ItemParams("TypeHierarchyItem returned by textDocument/prepareTypeHierarchy")),
        Lsp("typeHierarchy/subtypes", true, "Find the subtypes of a type hierarchy item.", ItemParams("TypeHierarchyItem returned by textDocument/prepareTypeHierarchy")),
        Lsp("textDocument/codeLens", true, "Get code lenses for a document.", Obj(TD())),
        Lsp("textDocument/documentLink", true, "Get document links in a document.", Obj(TD())),
        Lsp("textDocument/inlayHint", true, "Get inlay hints for a range of a document.", Obj(TD(), Rng())),
        Lsp("textDocument/moniker", true, "Get monikers for the symbol at a position.", AtPosition()),
        Lsp("textDocument/prepareRename", true, "Check whether rename is valid at a position and get the range to rename.", AtPosition()),

        // ---- 4.2 編集提案系（WorkspaceEdit を返すだけ。ファイルは書き換えない） ----
        Lsp("textDocument/rename", false, "Compute a WorkspaceEdit renaming the symbol at a position (returns the edit; does not modify files).",
            AtPosition(F("newName", Str("New name for the symbol"), true))),
        Lsp("textDocument/codeAction", false, "Get code actions (quick fixes, refactorings) for a range (returns edits; does not modify files).", Obj(
            TD(),
            Rng(),
            F("context", Obj(
                F("diagnostics", ObjArray("Diagnostics in the range"), true),
                F("only", StrArray("Requested code action kinds")),
                F("triggerKind", Int("CodeActionTriggerKind: 1=Invoked, 2=Automatic"))), true))),
        Lsp("codeAction/resolve", false, "Resolve the edit of a code action (returns edits; does not modify files).",
            Resolve("title", "CodeAction object as returned by textDocument/codeAction")),
        Lsp("textDocument/formatting", false, "Format a whole document (returns TextEdits; does not modify files).", Obj(TD(), Options())),
        Lsp("textDocument/rangeFormatting", false, "Format a range of a document (returns TextEdits; does not modify files).", Obj(TD(), Rng(), Options())),
        Lsp("workspace/willRenameFiles", false, "Ask for edits required before files are renamed (returns WorkspaceEdit; does not modify files).", Obj(
            F("files", ArrayOf(Obj(F("oldUri", Str("Old file URI"), true), F("newUri", Str("New file URI"), true)), "Files being renamed"), true))),
        Lsp("workspace/willCreateFiles", false, "Ask for edits required before files are created (returns WorkspaceEdit; does not modify files).", Obj(
            F("files", ArrayOf(Obj(F("uri", Str("URI of the file to be created"), true)), "Files being created"), true))),
        Lsp("workspace/willDeleteFiles", false, "Ask for edits required before files are deleted (returns WorkspaceEdit; does not modify files).", Obj(
            F("files", ArrayOf(Obj(F("uri", Str("URI of the file to be deleted"), true)), "Files being deleted"), true))),

        // ---- 4.3 コマンド・設定 ----
        Lsp("workspace/executeCommand", false, "Execute a unitycsharp.* workspace command (same as workspace/executeCommand).", Obj(
            F("command", Enum("Command identifier", ExecuteCommands), true),
            F("arguments", new JsonObject { ["type"] = "array", ["description"] = "Command arguments" }))),
        Lsp("workspace/didChangeConfiguration", false, "Send a workspace/didChangeConfiguration notification. Returns null.", Obj(
            F("settings", new JsonObject(), true)), McpToolKind.LspNotification),
        new McpTool("lsp_reset", null, McpToolKind.Reset, false,
            Describe("Close all open documents (didClose) and reload the workspace (unitycsharp.reloadWorkspace).", Obj()),
            Obj()),

        // ---- 追加: ブリッジ独自 ----
        new McpTool("lsp_diagnostics_cached", null, McpToolKind.DiagnosticsCached, true,
            Describe("Return the cached textDocument/publishDiagnostics results (all documents, or one document when uri is given).", Obj(
                F("uri", Str("file:/// URI, absolute path, or workspace-relative path"))
            )),
            Obj(F("uri", Str("file:/// URI, absolute path, or workspace-relative path")))),

        // ---- 追加: Unity GUID（FastLSP 独自メソッド unitylsp/*） ----
        new McpTool("unity_guid_definition", LspServer.GuidDefinitionMethod, McpToolKind.LspRequest, true,
            "Resolve a Unity asset GUID to its .meta file and asset. Pass 'guid' (32 hex chars) or 'asset' " +
            "(path of an asset, script or .meta; file:/// URI, absolute, or workspace-relative). Returns {guid, assetUri, meta: Location} or null.",
            GuidSchema(withMax: false)),
        new McpTool("unity_guid_references", LspServer.GuidReferencesMethod, McpToolKind.LspRequest, true,
            "Find the scenes, prefabs, materials, ScriptableObjects, .meta and .asmdef/.asmref files that reference a Unity asset GUID. " +
            "Pass 'guid' or 'asset' (e.g. 'Assets/Scripts/Player.cs' to find where the MonoBehaviour is attached). " +
            "Each reference has uri, 0-based range, lineText, and for YAML the owning object's classId/objectFileId and the reference fileId.",
            GuidSchema(withMax: true)),
    ];

    private static JsonObject GuidSchema(bool withMax)
    {
        var fields = new List<Field>
        {
            F("guid", Str("Unity asset GUID (32 hexadecimal characters). Either guid or asset is required.")),
            F("asset", Str("Asset, script or .meta path (file:/// URI, absolute, or workspace-relative). Used when guid is omitted.")),
        };
        if (withMax)
        {
            fields.Add(F("maxResults", Int($"Maximum number of references to return (default {UnityCSharpFastLSP.Workspace.UnityGuidIndex.DefaultMaxResults})", nonNegative: true)));
            fields.Add(F("includePatterns", StrArray(
                "Optional glob patterns of files to scan, relative to the project root (e.g. [\"*.prefab\", \"Assets/Scenes/**\", \"*.cs\"]). " +
                "Replaces the default text-asset extensions, so non-asset files such as *.cs can be searched.")));
            fields.Add(F("excludePatterns", StrArray(
                "Optional glob patterns of files to skip (e.g. [\"Library/PackageCache/**\", \"*.meta\"]).")));
        }
        return Obj(fields.ToArray());
    }

    private static McpTool Lsp(string method, bool readOnly, string summary, JsonObject schema, McpToolKind kind = McpToolKind.LspRequest) =>
        new(method.Replace('/', '_'), method, kind, readOnly, Describe(summary, schema), schema);

    private static string Describe(string summary, JsonObject schema) =>
        IsLocatable(schema) ? $"{summary} {PositionNote}" : summary;

    /// <summary>トップレベルに文書・位置・範囲・uri を取るツールには位置の注記を付ける。</summary>
    private static bool IsLocatable(JsonObject schema) =>
        schema["properties"] is JsonObject props
        && (props.ContainsKey("textDocument") || props.ContainsKey("position") || props.ContainsKey("range") || props.ContainsKey("uri"));

    // ---- スキーマ部品（呼び出しごとに新しいノードを作る。JsonNode は親を 1 つしか持てないため） ----

    private readonly record struct Field(string Name, JsonNode Schema, bool Required);

    private static Field F(string name, JsonNode schema, bool required = false) => new(name, schema, required);

    private static JsonObject Obj(params Field[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Name] = field.Schema;
            if (field.Required) required.Add(field.Name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static JsonObject Open(JsonObject schema)
    {
        schema["additionalProperties"] = true;
        return schema;
    }

    private static JsonObject ObjectNode(string description) =>
        new() { ["type"] = "object", ["description"] = description };

    private static JsonObject Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Bool(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Int(string description, bool nonNegative = false)
    {
        var schema = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (nonNegative) schema["minimum"] = 0;
        return schema;
    }

    private static JsonObject Enum(string description, string[] values) =>
        new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["description"] = description,
        };

    private static JsonObject ArrayOf(JsonObject items, string description) =>
        new() { ["type"] = "array", ["items"] = items, ["description"] = description };

    private static JsonObject StrArray(string description) =>
        ArrayOf(new JsonObject { ["type"] = "string" }, description);

    private static JsonObject ObjArray(string description) =>
        ArrayOf(new JsonObject { ["type"] = "object" }, description);

    private static JsonObject TextDocumentSchema() =>
        Obj(F("uri", Str("file:/// URI, absolute path, or workspace-relative path"), true));

    private static JsonObject PositionSchema() =>
        new()
        {
            ["type"] = "object",
            ["description"] = "0-based line / UTF-16 code unit offset",
            ["properties"] = new JsonObject
            {
                ["line"] = Int("0-based line", nonNegative: true),
                ["character"] = Int("0-based UTF-16 code unit offset", nonNegative: true),
            },
            ["required"] = new JsonArray("line", "character"),
        };

    private static JsonObject RangeSchema() =>
        Obj(F("start", PositionSchema(), true), F("end", PositionSchema(), true));

    /// <summary>textDocument（必須）。</summary>
    private static Field TD() => F("textDocument", TextDocumentSchema(), true);

    /// <summary>position（必須）。</summary>
    private static Field Pos() => F("position", PositionSchema(), true);

    /// <summary>range（必須）。</summary>
    private static Field Rng() => F("range", RangeSchema(), true);

    /// <summary>formatting の options（必須、追加プロパティ可）。</summary>
    private static Field Options() => F("options", Open(Obj(
        F("tabSize", Int("Size of a tab in spaces"), true),
        F("insertSpaces", Bool("Prefer spaces over tabs"), true))), true);

    /// <summary>位置系: textDocument + position（必須）に追加フィールドを足す。</summary>
    private static JsonObject AtPosition(params Field[] extra) =>
        Obj(new[] { TD(), Pos() }.Concat(extra).ToArray());

    /// <summary>callHierarchy / typeHierarchy: item（必須）。</summary>
    private static JsonObject ItemParams(string description) =>
        Obj(F("item", Open(ObjectNode(description)), true));

    /// <summary>resolve 系: 対象 LSP オブジェクトそのもの（追加プロパティ可、<paramref name="requiredField"/> 必須）。</summary>
    private static JsonObject Resolve(string requiredField, string objectDescription) =>
        Open(Obj(F(requiredField, Str($"Field of the {objectDescription}"), true)));
}
