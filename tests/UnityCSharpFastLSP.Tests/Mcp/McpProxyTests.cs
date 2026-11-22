using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Workspace;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Mcp;

/// <summary>CARD-MCP-04 §3: プロキシ（--mcp --proxy）のフォールバックのテスト。</summary>
public class McpProxyTests
{
    /// <summary>存在しないパス。Upstream 起動の候補探索（PATH・既定インストール先）は実機の状態に依存する。</summary>
    private const string NonexistentUpstream = @"Z:\__no_such_dir__\UnityCSharpLSP.Server.exe";

    [Fact]
    public async Task Proxy_FastMethodsAreAnsweredByFastLspWithoutWaitingForUpstream()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        string enemyUri = WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Enemy.cs"));
        await using var mcp = McpTestSession.Proxy(ws.Root, NonexistentUpstream);

        JsonNode? definition = McpTestSession.TextOf(McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = enemyUri },
            ["position"] = new JsonObject { ["line"] = 4, ["character"] = 27 },
        })));

        Assert.NotNull(definition);
        Assert.Contains("Player.cs", definition!.ToJsonString());
    }

    [Fact]
    public async Task Proxy_ToolsListMatchesStandalone()
    {
        using var ws = TempUnityWorkspace.CreateSample();
        await using var mcp = McpTestSession.Proxy(ws.Root, NonexistentUpstream);

        JsonArray tools = McpTestSession.ResultOf(await mcp.RequestAsync("tools/list"))["tools"]!.AsArray();

        Assert.Equal(40, tools.Count);
    }

    /// <summary>
    /// 実 UnityCSharpLSP を使う E2E。環境変数 UNITY_CSHARP_LSP_PATH が無ければ何もせず戻る
    /// （xunit 2.x には実行時スキップが無いため。CI 等で重い Upstream を避けるための既定動作）。
    /// </summary>
    [Fact]
    public async Task Proxy_Rename_E2E_ForwardsToUpstream()
    {
        string? upstream = Environment.GetEnvironmentVariable("UNITY_CSHARP_LSP_PATH");
        if (string.IsNullOrWhiteSpace(upstream)) return;

        using var ws = TempUnityWorkspace.CreateSample();
        string playerUri = WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Player.cs"));
        await using var mcp = McpTestSession.Proxy(ws.Root, upstream, timeout: TimeSpan.FromSeconds(120));

        JsonObject result = McpTestSession.ResultOf(await mcp.CallToolAsync("textDocument_rename", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = playerUri },
            ["position"] = new JsonObject { ["line"] = 2, ["character"] = 17 },
            ["newName"] = "Fighter",
        }, timeout: TimeSpan.FromSeconds(150)));

        Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
        JsonNode? edit = McpTestSession.TextOf(result);
        Assert.NotNull(edit);
        string text = edit!.ToJsonString();
        Assert.True(text.Contains("\"changes\"") || text.Contains("\"documentChanges\""), text);
    }
}
