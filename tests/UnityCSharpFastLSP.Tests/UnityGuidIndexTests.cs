using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Tests.Mcp;
using UnityCSharpFastLSP.Workspace;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

/// <summary>Unity GUID 索引（.meta 常駐 + YAML アセット都度走査）と MCP ツールのテスト。</summary>
public class UnityGuidIndexTests
{
    private const string PlayerGuid = "0123456789abcdef0123456789abcdef";
    private const string MaterialGuid = "fedcba9876543210fedcba9876543210";
    private const string AsmdefGuid = "11112222333344445555666677778888";

    private static TempUnityWorkspace CreateGuidSample()
    {
        var ws = TempUnityWorkspace.CreateSample();
        ws.Write("Assets/Scripts/Player.cs.meta", $$"""
            fileFormatVersion: 2
            guid: {{PlayerGuid}}
            MonoImporter:
              externalObjects: {}
            """);
        ws.Write("Assets/Materials/Red.mat.meta", $"fileFormatVersion: 2\nguid: {MaterialGuid}\n");
        ws.Write("Assets/Materials/Red.mat", "%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Name: Red\n");
        ws.Write("Assets/Scripts/Game.asmdef.meta", $"fileFormatVersion: 2\nguid: {AsmdefGuid}\n");
        ws.Write("Assets/Scripts/Game.asmdef", "{ \"name\": \"Game\" }");
        ws.Write("Assets/Editor/GameEditor.asmdef", $"{{\n  \"name\": \"GameEditor\",\n  \"references\": [\"GUID:{AsmdefGuid}\"]\n}}");

        ws.Write("Assets/Prefabs/Hero.prefab", $$"""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &1001
            GameObject:
              m_Name: Hero
            --- !u!114 &1002
            MonoBehaviour:
              m_GameObject: {fileID: 1001}
              m_Script: {fileID: 11500000, guid: {{PlayerGuid}}, type: 3}
            --- !u!23 &1003
            MeshRenderer:
              m_Materials:
              - {fileID: 2100000, guid: {{MaterialGuid}}, type: 2}
            """);
        ws.Write("Assets/Scenes/Main.unity", $$"""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!114 &500
            MonoBehaviour:
              m_Script: {fileID: 11500000, guid: {{PlayerGuid}}, type: 3}
            --- !u!114 &501
            MonoBehaviour:
              m_Script: {fileID: 11500000, guid: {{PlayerGuid}}, type: 3}
            """);

        // Binary 直列化のアセット（NUL を含む）は guid 文字列が入っていても対象外
        File.WriteAllBytes(ws.FullPath("Assets/Scenes/Binary.unity"),
            [0, 0, 0, 1, .. System.Text.Encoding.ASCII.GetBytes(PlayerGuid)]);
        return ws;
    }

    private static UnityGuidIndex BuildIndex(TempUnityWorkspace ws)
    {
        var index = new UnityGuidIndex();
        index.Index(ws.Root);
        return index;
    }

    [Fact]
    public void Index_ReadsGuidsFromMetaFiles()
    {
        using var ws = CreateGuidSample();
        UnityGuidIndex index = BuildIndex(ws);

        Assert.Equal(3, index.Count);
        Assert.Equal(PlayerGuid, index.ResolveGuid(null, ws.FullPath("Assets/Scripts/Player.cs")));
        Assert.Equal(PlayerGuid, index.ResolveGuid(null, WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Player.cs.meta"))));
        Assert.Equal(PlayerGuid, index.ResolveGuid(PlayerGuid.ToUpperInvariant(), null));
        Assert.Null(index.ResolveGuid("not-a-guid", null));
        Assert.Null(index.ResolveGuid(null, ws.FullPath("Assets/Scripts/Enemy.cs")));
    }

    [Fact]
    public void FindDefinition_ReturnsMetaLocationAndAsset()
    {
        using var ws = CreateGuidSample();
        UnityGuidIndex index = BuildIndex(ws);

        var def = index.FindDefinition(PlayerGuid);

        Assert.NotNull(def);
        Assert.Equal(WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Player.cs")), def!.AssetUri);
        Assert.Equal(WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Player.cs.meta")), def.Meta.Uri);
        Assert.Equal(1, def.Meta.Range.Start.Line);
        Assert.Equal(6, def.Meta.Range.Start.Character);
        Assert.Equal(38, def.Meta.Range.End.Character);
    }

    [Fact]
    public void FindDefinition_PicksUpMetaAddedAfterIndexing()
    {
        using var ws = CreateGuidSample();
        UnityGuidIndex index = BuildIndex(ws);

        const string newGuid = "abababababababababababababababab";
        ws.Write("Assets/New.asset.meta", $"fileFormatVersion: 2\nguid: {newGuid}\n");

        Assert.NotNull(index.FindDefinition(newGuid));
    }

    [Fact]
    public void FindReferences_FindsScriptUsagesInPrefabsAndScenesWithObjectInfo()
    {
        using var ws = CreateGuidSample();
        UnityGuidIndex index = BuildIndex(ws);

        var result = index.FindReferences(PlayerGuid);

        Assert.False(result.Truncated);
        Assert.Equal(WorkspaceManager.PathToFileUri(ws.FullPath("Assets/Scripts/Player.cs")), result.AssetUri);
        Assert.Equal(3, result.References.Count);
        Assert.DoesNotContain(result.References, r => r.Uri.EndsWith(".meta", StringComparison.Ordinal));
        Assert.DoesNotContain(result.References, r => r.Uri.EndsWith("Binary.unity", StringComparison.Ordinal));

        var prefabRef = Assert.Single(result.References, r => r.Uri.EndsWith("Hero.prefab", StringComparison.Ordinal));
        Assert.Equal(8, prefabRef.Range.Start.Line);
        Assert.Equal(114, prefabRef.ClassId);
        Assert.Equal(1002, prefabRef.ObjectFileId);
        Assert.Equal(11500000, prefabRef.FileId);
        Assert.StartsWith("m_Script:", prefabRef.LineText);
        string line = File.ReadAllLines(ws.FullPath("Assets/Prefabs/Hero.prefab"))[8];
        Assert.Equal(PlayerGuid, line.Substring(prefabRef.Range.Start.Character, 32));

        var sceneRefs = result.References.Where(r => r.Uri.EndsWith("Main.unity", StringComparison.Ordinal)).ToList();
        Assert.Equal([500L, 501L], sceneRefs.Select(r => r.ObjectFileId!.Value).ToArray());
    }

    [Fact]
    public void FindReferences_FindsMaterialAndAsmdefGuidReferences()
    {
        using var ws = CreateGuidSample();
        UnityGuidIndex index = BuildIndex(ws);

        var mat = Assert.Single(index.FindReferences(MaterialGuid).References);
        Assert.EndsWith("Hero.prefab", mat.Uri);
        Assert.Equal(23, mat.ClassId);
        Assert.Equal(2100000, mat.FileId);

        var asm = Assert.Single(index.FindReferences(AsmdefGuid).References);
        Assert.EndsWith("GameEditor.asmdef", asm.Uri);
        Assert.Null(asm.ClassId);
        Assert.Contains($"GUID:{AsmdefGuid}", asm.LineText);
    }

    [Fact]
    public void FindReferences_RespectsMaxResults()
    {
        using var ws = CreateGuidSample();
        UnityGuidIndex index = BuildIndex(ws);

        var result = index.FindReferences(PlayerGuid, maxResults: 1);

        Assert.True(result.Truncated);
        Assert.Single(result.References);
    }

    [Fact]
    public async Task Mcp_GuidTools_ResolveRelativeAssetPath()
    {
        using var ws = CreateGuidSample();
        await using var mcp = McpTestSession.Standalone(ws.Root);

        JsonObject refsResult = McpTestSession.ResultOf(await mcp.CallToolAsync("unity_guid_references", new JsonObject
        {
            ["asset"] = "Assets/Scripts/Player.cs",
        }));
        Assert.False(refsResult["isError"]?.GetValue<bool>() ?? false, refsResult.ToJsonString());
        JsonNode refs = McpTestSession.TextOf(refsResult)!;
        Assert.Equal(PlayerGuid, refs["guid"]!.GetValue<string>());
        Assert.Equal(3, refs["references"]!.AsArray().Count);

        JsonObject defResult = McpTestSession.ResultOf(await mcp.CallToolAsync("unity_guid_definition", new JsonObject
        {
            ["guid"] = MaterialGuid,
        }));
        JsonNode def = McpTestSession.TextOf(defResult)!;
        Assert.EndsWith("Red.mat", def["assetUri"]!.GetValue<string>());
        Assert.EndsWith("Red.mat.meta", def["meta"]!["uri"]!.GetValue<string>());

        JsonObject bad = McpTestSession.ResultOf(await mcp.CallToolAsync("unity_guid_references", new JsonObject()));
        Assert.True(bad["isError"]!.GetValue<bool>());
        Assert.Equal(-32602, McpTestSession.TextOf(bad)!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Mcp_Proxy_GuidToolsAreAnsweredByFastLsp()
    {
        using var ws = CreateGuidSample();
        await using var mcp = McpTestSession.Proxy(ws.Root, @"Z:\__no_such_dir__\UnityCSharpLSP.Server.exe");

        JsonObject result = McpTestSession.ResultOf(await mcp.CallToolAsync("unity_guid_references", new JsonObject
        {
            ["guid"] = MaterialGuid,
        }));

        Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
        Assert.Single(McpTestSession.TextOf(result)!["references"]!.AsArray());
    }
}
