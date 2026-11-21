using System.Text.Json;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// LSP ライフサイクル（initialize / initialized / shutdown / exit）と、
/// initialize で広告する ServerCapabilities の仕様適合を確認する。
/// </summary>
public class LifecycleSpecTests
{
    [Fact]
    public void Initialize_ReturnsCapabilitiesAndServerInfo()
    {
        using var s = new LspSession();

        var result = s.Initialize();

        Assert.True(result.TryGetProperty("capabilities", out _), "InitializeResult must contain 'capabilities'.");
        Assert.True(result.TryGetProperty("serverInfo", out _), "InitializeResult must contain 'serverInfo'.");
    }

    [Fact]
    public void Initialize_ServerInfoHasName()
    {
        using var s = new LspSession();

        var result = s.Initialize();

        Assert.Equal("UnityCSharpFastLSP", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void Initialize_AdvertisesIncrementalTextDocumentSync()
    {
        // TextDocumentSyncKind: 0=None, 1=Full, 2=Incremental
        using var s = new LspSession();

        var caps = s.Initialize().GetProperty("capabilities");

        Assert.Equal(2, caps.GetProperty("textDocumentSync").GetInt32());
    }

    [Theory]
    [InlineData("hoverProvider")]
    [InlineData("definitionProvider")]
    [InlineData("declarationProvider")]
    [InlineData("implementationProvider")]
    [InlineData("typeDefinitionProvider")]
    [InlineData("referencesProvider")]
    [InlineData("documentSymbolProvider")]
    [InlineData("documentHighlightProvider")]
    [InlineData("documentFormattingProvider")]
    [InlineData("workspaceSymbolProvider")]
    public void Initialize_AdvertisesImplementedBooleanProviders(string providerName)
    {
        using var s = new LspSession();

        var caps = s.Initialize().GetProperty("capabilities");

        Assert.True(caps.TryGetProperty(providerName, out var value), $"'{providerName}' must be advertised.");
        Assert.Equal(JsonValueKind.True, value.ValueKind);
    }

    [Fact]
    public void Initialize_CompletionAdvertisesDotTriggerCharacter()
    {
        using var s = new LspSession();

        var completion = s.Initialize().GetProperty("capabilities").GetProperty("completionProvider");
        var triggers = completion.GetProperty("triggerCharacters").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains(".", triggers);
    }

    [Fact]
    public void Initialize_CompletionResolveProviderIsFalse()
    {
        using var s = new LspSession();

        var completion = s.Initialize().GetProperty("capabilities").GetProperty("completionProvider");

        Assert.False(completion.GetProperty("resolveProvider").GetBoolean());
    }

    [Fact]
    public void Initialize_SignatureHelpAdvertisesParenAndComma()
    {
        using var s = new LspSession();

        var sig = s.Initialize().GetProperty("capabilities").GetProperty("signatureHelpProvider");
        var triggers = sig.GetProperty("triggerCharacters").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("(", triggers);
        Assert.Contains(",", triggers);
    }

    [Fact]
    public void Initialize_SemanticTokensAdvertisesLegendAndFullSupport()
    {
        using var s = new LspSession();

        var sem = s.Initialize().GetProperty("capabilities").GetProperty("semanticTokensProvider");

        Assert.True(sem.GetProperty("full").GetBoolean());
        Assert.Equal(JsonValueKind.Array, sem.GetProperty("legend").GetProperty("tokenTypes").ValueKind);
        Assert.Equal(JsonValueKind.Array, sem.GetProperty("legend").GetProperty("tokenModifiers").ValueKind);
    }

    [Fact]
    public void InitializedNotification_ProducesNoOutput()
    {
        // 仕様: initialized は通知なので応答してはならない。
        using var s = new LspSession();
        s.Initialize(); // Initialize() 内で initialized も送信済み

        int before = s.CapturedByteCount;
        s.Notify("initialized", new { });

        Assert.Equal(before, s.CapturedByteCount);
    }

    [Fact]
    public void Shutdown_ThenExit_EndsServerLoop()
    {
        // 仕様: exit で終了。HandleMessage が false を返すとホストのループが止まる。
        using var s = new LspSession();
        s.Initialize();
        s.Request("shutdown", new { });

        bool keepRunning = s.NotifyWithResult("exit", new { });

        Assert.False(keepRunning);
    }

    [Fact]
    public void Exit_WithoutShutdown_EndsServerLoop()
    {
        // 仕様: shutdown 前の exit もプロセス終了させる（終了コードだけが異なる）。
        using var s = new LspSession();
        s.Initialize();

        bool keepRunning = s.NotifyWithResult("exit", new { });

        Assert.False(keepRunning);
    }
}
