using System.Text;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Server;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

/// <summary>--proxy の upstream capabilities キャッシュ（kind ごとの保存・マージ・クリア）。</summary>
public sealed class UpstreamCapabilitiesCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fastlsp-capcache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static JsonObject Caps(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Save_ThenLoad_RoundTripsPerKind()
    {
        var cache = new UpstreamCapabilitiesCache(_dir);
        Assert.Null(cache.Load(UpstreamKind.CSharpLs));

        Assert.True(cache.Save(UpstreamKind.CSharpLs, Caps("""{"implementationProvider":true}""")));

        Assert.True(JsonNode.DeepEquals(Caps("""{"implementationProvider":true}"""), cache.Load(UpstreamKind.CSharpLs)));
        Assert.Null(cache.Load(UpstreamKind.OmniSharp));
    }

    [Fact]
    public void Save_SameContent_ReturnsFalse()
    {
        var cache = new UpstreamCapabilitiesCache(_dir);
        Assert.True(cache.Save(UpstreamKind.OmniSharp, Caps("""{"a":1}""")));
        Assert.False(cache.Save(UpstreamKind.OmniSharp, Caps("""{"a":1}""")));
        Assert.True(cache.Save(UpstreamKind.OmniSharp, Caps("""{"a":2}""")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        var cache = new UpstreamCapabilitiesCache(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(cache.GetPath(UpstreamKind.CSharpLs), "{ not json");
        Assert.Null(cache.Load(UpstreamKind.CSharpLs));
    }

    [Fact]
    public void Clear_OneKindOrAll()
    {
        var cache = new UpstreamCapabilitiesCache(_dir);
        cache.Save(UpstreamKind.CSharpLs, Caps("""{"a":1}"""));
        cache.Save(UpstreamKind.OmniSharp, Caps("""{"a":1}"""));
        cache.Save(UpstreamKind.Auto, Caps("""{"a":1}"""));

        Assert.Single(cache.Clear(UpstreamKind.CSharpLs));
        Assert.Null(cache.Load(UpstreamKind.CSharpLs));
        Assert.NotNull(cache.Load(UpstreamKind.OmniSharp));

        Assert.Equal(2, cache.Clear().Count);
        Assert.Null(cache.Load(UpstreamKind.OmniSharp));
        Assert.Null(cache.Load(UpstreamKind.Auto));
        Assert.Empty(cache.Clear());
    }

    [Fact]
    public void MergeInto_FastWins_AndFastOwnedKeysAreSkipped()
    {
        JsonObject fast = Caps("""{"textDocumentSync":2,"hoverProvider":true,"renameProvider":true}""");
        JsonObject upstream = Caps("""
            {
              "textDocumentSync": 1,
              "positionEncoding": "utf-8",
              "semanticTokensProvider": {"legend":{"tokenTypes":[],"tokenModifiers":[]}},
              "hoverProvider": false,
              "renameProvider": {"prepareProvider": true},
              "implementationProvider": true,
              "callHierarchyProvider": true
            }
            """);

        UpstreamCapabilitiesCache.MergeInto(fast, upstream);

        Assert.Equal(2, (int)fast["textDocumentSync"]!);
        Assert.Null(fast["positionEncoding"]);
        Assert.Null(fast["semanticTokensProvider"]);
        Assert.True((bool)fast["hoverProvider"]!);
        Assert.True((bool)fast["renameProvider"]!);
        Assert.True((bool)fast["implementationProvider"]!);
        Assert.True((bool)fast["callHierarchyProvider"]!);
    }

    [Fact]
    public void Proxy_Initialize_AdvertisesCachedUpstreamCapabilities()
    {
        var cache = new UpstreamCapabilitiesCache(_dir);
        cache.Save(UpstreamKind.CSharpLs, Caps("""{"callHierarchyProvider":true,"textDocumentSync":1}"""));

        JsonObject caps = InitializeThroughProxy(cache, UpstreamKind.CSharpLs);

        Assert.True((bool)caps["callHierarchyProvider"]!);
        Assert.Equal(2, (int)caps["textDocumentSync"]!);
        Assert.True((bool)caps["hoverProvider"]!);
    }

    [Fact]
    public void Proxy_Initialize_WithoutCache_AdvertisesFastCapabilitiesOnly()
    {
        var cache = new UpstreamCapabilitiesCache(_dir);
        cache.Save(UpstreamKind.OmniSharp, Caps("""{"callHierarchyProvider":true}"""));

        JsonObject caps = InitializeThroughProxy(cache, UpstreamKind.CSharpLs);

        Assert.Null(caps["callHierarchyProvider"]);
        Assert.True((bool)caps["hoverProvider"]!);
    }

    private JsonObject InitializeThroughProxy(UpstreamCapabilitiesCache cache, UpstreamKind kind)
    {
        var output = new MemoryStream();
        byte[] snapshot;
        // 存在しない upstream を指定して実プロセスを起動しない（起動失敗は非致命的）
        string missing = Path.Combine(_dir, "no-such-upstream.exe");
        using (var proxy = new LspProxy(output, missing, kind, workspacePath: null, capabilitiesCache: cache))
        {
            byte[] init = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            Assert.True(proxy.HandleClientMessage(init));
            lock (output)
            {
                snapshot = output.ToArray();
            }
        }

        using var read = new MemoryStream(snapshot);
        while (LspMessageReader.TryReadMessage(read, out byte[]? message) && message != null)
        {
            if (JsonNode.Parse(message) is JsonObject root && root["result"]?["capabilities"] is JsonObject caps)
            {
                return caps;
            }
        }
        throw new Xunit.Sdk.XunitException("No initialize response was written.");
    }
}
