using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Server.Mcp;
using UnityCSharpFastLSP.Workspace;
using UnityCSharpFastLSP.Tests.Spec;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Mcp;

/// <summary>CARD-MCP-04 §2: スタンドアロン（--mcp）の MCP サーバーのテスト。</summary>
public class McpStandaloneTests
{
    // 仕様 §4.1 の読み取り系（MCP 名）+ ブリッジ独自の lsp_diagnostics_cached
    private static readonly string[] ReadOnlyTools =
    [
        "textDocument_hover",
        "textDocument_definition",
        "textDocument_declaration",
        "textDocument_typeDefinition",
        "textDocument_implementation",
        "textDocument_references",
        "textDocument_documentSymbol",
        "textDocument_documentHighlight",
        "textDocument_signatureHelp",
        "textDocument_completion",
        "completionItem_resolve",
        "textDocument_diagnostic",
        "workspace_diagnostic",
        "workspace_symbol",
        "workspaceSymbol_resolve",
        "textDocument_prepareCallHierarchy",
        "callHierarchy_incomingCalls",
        "callHierarchy_outgoingCalls",
        "textDocument_prepareTypeHierarchy",
        "typeHierarchy_supertypes",
        "typeHierarchy_subtypes",
        "textDocument_codeLens",
        "textDocument_documentLink",
        "textDocument_inlayHint",
        "textDocument_moniker",
        "textDocument_prepareRename",
        "lsp_diagnostics_cached",
        "unity_guid_definition",
        "unity_guid_references",
    ];

    // 仕様 §4.2 / §4.3 の非読み取り系
    private static readonly string[] WriteTools =
    [
        "textDocument_rename",
        "textDocument_codeAction",
        "codeAction_resolve",
        "textDocument_formatting",
        "textDocument_rangeFormatting",
        "workspace_willRenameFiles",
        "workspace_willCreateFiles",
        "workspace_willDeleteFiles",
        "workspace_executeCommand",
        "workspace_didChangeConfiguration",
        "lsp_reset",
    ];

    // 仕様 §4.4（公開しない）
    private static readonly string[] HiddenTools =
    [
        "textDocument_semanticTokens_full",
        "textDocument_foldingRange",
        "textDocument_selectionRange",
        "textDocument_linkedEditingRange",
        "textDocument_inlineValue",
        "textDocument_onTypeFormatting",
        "codeLens_resolve",
        "textDocument_didSave",
    ];

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionServerInfoAndToolsCapability()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject result = McpTestSession.ResultOf(await mcp.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "0" },
        }));

        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("unity-csharp-fast-lsp", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task ToolsList_HasAllPublicToolsWithReadOnlyAnnotationsAndNoHiddenTools()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject result = McpTestSession.ResultOf(await mcp.RequestAsync("tools/list"));
        JsonArray tools = result["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();

        Assert.Equal(40, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        foreach (string expected in ReadOnlyTools.Concat(WriteTools))
        {
            Assert.Contains(expected, names);
        }
        foreach (string hidden in HiddenTools)
        {
            Assert.DoesNotContain(hidden, names);
        }

        // readOnlyHint は §4.1 とブリッジ独自の lsp_diagnostics_cached だけに付く
        var readOnly = tools
            .Where(t => t!["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true)
            .Select(t => t!["name"]!.GetValue<string>())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ReadOnlyTools.OrderBy(n => n, StringComparer.Ordinal).ToArray(), readOnly);

        foreach (JsonNode? tool in tools)
        {
            Assert.Equal("object", tool!["inputSchema"]!["type"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Definition_CrossFile_MatchesDirectLspResult()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        string enemyPath = ws.FullPath("Assets/Scripts/Enemy.cs");
        string enemyUri = WorkspaceManager.PathToFileUri(enemyPath);
        var args = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = enemyUri },
            ["position"] = new JsonObject { ["line"] = 4, ["character"] = 27 },
        };

        JsonNode? viaMcp;
        await using (var mcp = McpTestSession.Standalone(ws.Root))
        {
            JsonObject toolResult = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_definition", args));
            Assert.False(toolResult["isError"]?.GetValue<bool>() ?? false);
            viaMcp = McpTestSession.TextOf(toolResult);
        }

        JsonNode? direct = DirectLspResult(ws.Root, enemyPath, "textDocument/definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = enemyUri },
            ["position"] = new JsonObject { ["line"] = 4, ["character"] = 27 },
        });

        Assert.NotNull(viaMcp);
        Assert.True(JsonNode.DeepEquals(direct, viaMcp), $"direct={direct?.ToJsonString()} mcp={viaMcp.ToJsonString()}");
        Assert.Contains("Player.cs", viaMcp!.ToJsonString());
    }

    [Fact]
    public async Task ReadTools_ReturnNonErrorExpectedResults()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        string playerUri = WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Player.cs"));
        string enemyUri = WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Enemy.cs"));
        await using var mcp = McpTestSession.Standalone(ws.Root);

        // references: Health フィールド（Player.cs 4:19）→ 同一ファイル内の使用箇所（7 行目 "Health -= amount"）を含む。
        // FastLSP の references は索引の宣言 + 同一ドキュメント内の一致のみ（横断検索は UnityCSharpLSP 側の責務）。
        JsonObject refs = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_references", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = playerUri },
            ["position"] = new JsonObject { ["line"] = 4, ["character"] = 19 },
            ["context"] = new JsonObject { ["includeDeclaration"] = true },
        }));
        Assert.False(refs["isError"]?.GetValue<bool>() ?? false);
        JsonArray refArray = McpTestSession.TextOf(refs)!.AsArray();
        Assert.True(refArray.Count >= 2, $"references: {refArray.ToJsonString()}");
        Assert.Contains(refArray, r => r!["range"]!["start"]!["line"]!.GetValue<int>() == 7);

        // workspace/symbol
        JsonObject symbols = McpTestSession.ResultOf(await mcp.CallToolAsync("workspace_symbol", new JsonObject
        {
            ["query"] = "Player",
        }));
        Assert.False(symbols["isError"]?.GetValue<bool>() ?? false);
        Assert.Contains(McpTestSession.TextOf(symbols)!.AsArray(), s => s!["name"]!.GetValue<string>() == "Player");

        // hover: Enemy.cs の Player 型参照
        JsonObject hover = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_hover", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = enemyUri },
            ["position"] = new JsonObject { ["line"] = 4, ["character"] = 27 },
        }));
        Assert.False(hover["isError"]?.GetValue<bool>() ?? false);
        Assert.NotNull(McpTestSession.TextOf(hover));
        Assert.NotNull(hover["structuredContent"]);

        // documentSymbol
        JsonObject docSymbols = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = playerUri },
        }));
        Assert.False(docSymbols["isError"]?.GetValue<bool>() ?? false);
        Assert.Contains(FlattenNames(McpTestSession.TextOf(docSymbols)!), n => n == "Player");
    }

    [Fact]
    public async Task UriForms_AbsolutePathAndRelativePath_MatchFileUri()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        string enemyPath = ws.FullPath("Assets/Scripts/Enemy.cs");
        string fileUri = WorkspaceManager.PathToFileUri(enemyPath);
        var position = new JsonObject { ["line"] = 4, ["character"] = 27 };
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonNode? viaUri = McpTestSession.TextOf(McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fileUri },
            ["position"] = position.DeepClone(),
        })));
        JsonNode? viaAbsolute = McpTestSession.TextOf(McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = enemyPath },
            ["position"] = position.DeepClone(),
        })));
        JsonNode? viaRelative = McpTestSession.TextOf(McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = "Assets/Scripts/Enemy.cs" },
            ["position"] = position.DeepClone(),
        })));

        Assert.NotNull(viaUri);
        Assert.True(JsonNode.DeepEquals(viaUri, viaAbsolute), "absolute path differs from file:/// URI");
        Assert.True(JsonNode.DeepEquals(viaUri, viaRelative), "workspace-relative path differs from file:/// URI");
    }

    [Fact]
    public async Task DiskChange_IsResynchronizedOnNextCall()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        string playerPath = ws.FullPath("Assets/Scripts/Player.cs");
        string playerUri = WorkspaceManager.PathToFileUri(playerPath);
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject before = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = playerUri },
        }));
        Assert.DoesNotContain("Heal", FlattenNames(McpTestSession.TextOf(before)!));

        // エージェントがディスク上のファイルを直接編集した状態を再現する
        ws.Write("Assets/Scripts/Player.cs", """
            namespace Game.Core
            {
                public class Player
                {
                    public int Health = 100;
                    public void TakeDamage(int amount)
                    {
                        Health -= amount;
                    }
                    public void Heal(int amount)
                    {
                        Health += amount;
                    }
                }
            }
            """);

        JsonObject after = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = playerUri },
        }));
        Assert.Contains("Heal", FlattenNames(McpTestSession.TextOf(after)!));
    }

    [Fact]
    public async Task Rename_Standalone_ReturnsIsErrorWithMethodNotFoundAndProxyHint()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject result = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_rename", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Player.cs")) },
            ["position"] = new JsonObject { ["line"] = 2, ["character"] = 17 },
            ["newName"] = "Fighter",
        }));

        Assert.True(result["isError"]!.GetValue<bool>());
        JsonNode error = McpTestSession.TextOf(result)!;
        Assert.Equal(-32601, error["code"]!.GetValue<int>());
        Assert.Contains("--proxy", error["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownTool_ReturnsJsonRpcInvalidParams()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonNode response = await mcp.CallToolAsync("no_such_tool");

        Assert.NotNull(response["error"]);
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ChangeConfigurationAndReset_ReturnNullAndNonError()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject config = McpTestSession.ResultOf(await mcp.CallToolAsync("workspace_didChangeConfiguration", new JsonObject
        {
            ["settings"] = new JsonObject(),
        }));
        Assert.Equal("null", config["content"]![0]!["text"]!.GetValue<string>());

        JsonObject reset = McpTestSession.ResultOf(await mcp.CallToolAsync("lsp_reset"));
        Assert.False(reset["isError"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task DiagnosticsCached_ReturnsSyntaxErrorsAfterOpen()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        string brokenPath = ws.Write("Assets/Scripts/Broken.cs", "public class {\n");
        string brokenUri = WorkspaceManager.PathToFileUri(brokenPath);
        await using var mcp = McpTestSession.Standalone(ws.Root);

        // 初回呼び出しでドキュメントが開かれ、publishDiagnostics が非同期に届く
        await mcp.CallToolAsync("textDocument_documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = brokenUri },
        });

        JsonNode? diagnostics = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            JsonObject cached = McpTestSession.ResultOf(await mcp.CallToolAsync("lsp_diagnostics_cached", new JsonObject
            {
                ["uri"] = brokenUri,
            }));
            diagnostics = McpTestSession.TextOf(cached);
            if (diagnostics is JsonObject { } d && d["diagnostics"] is JsonArray { Count: > 0 })
            {
                break;
            }
            await Task.Delay(100);
        }

        Assert.NotNull(diagnostics);
        Assert.True(diagnostics!["diagnostics"]!.AsArray().Count > 0, diagnostics.ToJsonString());
    }

    [Fact]
    public async Task DiagnosticsCached_UnopenedDocument_ReturnsNullWithoutStructuredContent()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject result = McpTestSession.ResultOf(await mcp.CallToolAsync("lsp_diagnostics_cached", new JsonObject
        {
            ["uri"] = "Assets/Scripts/Enemy.cs",
        }));

        Assert.Equal("null", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(result["structuredContent"]);
    }

    [Fact]
    public async Task Ping_ReturnsEmptyObject()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject result = McpTestSession.ResultOf(await mcp.RequestAsync("ping"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task InvalidJsonLine_ReturnsParseError()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        await mcp.SendRawLineAsync("{not json");
        JsonNode response = await mcp.NextUnmatchedAsync();

        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
    }

    // ---------- ヘルパー ----------

    /// <summary>MCP を介さず LspSession（既存の LSP 仕様テスト用ハーネス）に直接要求を出す。</summary>
    private static JsonNode? DirectLspResult(string root, string filePath, string method, JsonObject parameters)
    {
        using var session = new LspSession();
        session.Request("initialize", new
        {
            processId = (int?)null,
            rootUri = WorkspaceManager.PathToFileUri(root),
            capabilities = new { },
        });
        session.Notify("initialized", new { });
        session.OpenDocument(WorkspaceManager.PathToFileUri(filePath), File.ReadAllText(filePath));

        JsonElement result = session.Request(method, JsonNode.Parse(parameters.ToJsonString())!);
        return JsonNode.Parse(result.GetRawText());
    }

    /// <summary>DocumentSymbol（階層）/ SymbolInformation（平坦）の name をすべて集める。</summary>
    private static IEnumerable<string> FlattenNames(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                foreach (string name in FlattenNames(child!)) yield return name;
            }
        }
        else if (node is JsonObject obj)
        {
            if (obj["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name))
            {
                yield return name;
            }
            if (obj["children"] is JsonArray children)
            {
                foreach (string child in FlattenNames(children)) yield return child;
            }
        }
    }
}
