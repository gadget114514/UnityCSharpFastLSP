using System.Text;
using System.Text.Json;
using UnityCSharpFastLSP.Server;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// JSON-RPC 2.0 の仕様（レスポンスの形式、id の扱い、エラーコード）への適合を確認する。
/// </summary>
public class JsonRpcSpecTests
{
    private const int MethodNotFound = -32601;
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int ServerNotInitialized = -32002;

    [Fact]
    public void Response_HasJsonRpcVersion2()
    {
        using var s = new LspSession();
        s.Initialize();

        var root = s.RequestRaw("shutdown", new { });

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
    }

    [Fact]
    public void Response_EchoesNumericId()
    {
        using var s = new LspSession();
        s.Initialize();

        var root = s.RequestRaw("shutdown", new { });

        Assert.Equal(JsonValueKind.Number, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public void Response_EchoesStringId()
    {
        using var s = new LspSession();
        s.Initialize();

        s.SendRawJson("""{"jsonrpc":"2.0","id":"req-abc","method":"shutdown"}""");

        var response = s.AllMessages().Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String);
        Assert.Equal("req-abc", response.GetProperty("id").GetString());
    }

    [Fact]
    public void Response_ContainsResultMember_NotError_ForSuccessfulRequest()
    {
        using var s = new LspSession();
        s.Initialize();

        var root = s.RequestRaw("shutdown", new { });

        Assert.True(root.TryGetProperty("result", out _));
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void Shutdown_ResultIsNull()
    {
        using var s = new LspSession();
        s.Initialize();

        var result = s.Request("shutdown", new { });

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }

    [Fact]
    public void UnknownRequest_ReturnsMethodNotFoundError()
    {
        // 仕様: 未実装のリクエストには -32601 (MethodNotFound) のエラーレスポンスを返す。
        using var s = new LspSession();
        s.Initialize();

        var root = s.RequestRaw("textDocument/doesNotExist", new { });

        Assert.True(root.TryGetProperty("error", out var error), "Unknown request must return an error response.");
        Assert.Equal(MethodNotFound, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void UnknownNotification_ProducesNoOutput()
    {
        // 仕様: id のない通知には応答してはならない。
        using var s = new LspSession();
        s.Initialize();
        int before = s.CapturedByteCount;

        s.Notify("$/someUnknownNotification", new { });

        Assert.Equal(before, s.CapturedByteCount);
    }

    [Fact]
    public void CancelRequestNotification_ProducesNoOutput()
    {
        using var s = new LspSession();
        s.Initialize();
        int before = s.CapturedByteCount;

        s.Notify("$/cancelRequest", new { id = 999 });

        Assert.Equal(before, s.CapturedByteCount);
    }

    [Fact]
    public void CancelRequestNotification_CancelsRegisteredRequest()
    {
        using var output = new SyncCaptureStream();
        var server = new LspServer(output);
        using var cts = new CancellationTokenSource();
        CancellationToken registered = server.CancellationRegistry.Register("7", cts.Token);

        server.HandleMessage(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":7}}"""));

        Assert.True(registered.IsCancellationRequested);
    }

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        // 仕様: 不正な JSON でサーバーが落ちてはならない（-32700 を返す）。
        using var s = new LspSession();
        s.Initialize();

        var ex = Record.Exception(() => s.SendRawJson("{ this is not json"));

        Assert.Null(ex);
    }

    [Fact]
    public void MalformedJson_ReturnsParseErrorWithNullId()
    {
        // 仕様: パースに失敗した場合は id = null, error.code = -32700 のレスポンスを返す。
        using var s = new LspSession();
        s.Initialize();

        s.SendRawJson("{ this is not json");

        var errorResponse = s.AllMessages().FirstOrDefault(m => m.TryGetProperty("error", out _));
        Assert.Equal(JsonValueKind.Object, errorResponse.ValueKind);
        Assert.Equal(ParseError, errorResponse.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, errorResponse.GetProperty("id").ValueKind);
    }

    [Fact]
    public void RequestBeforeInitialize_ReturnsServerNotInitializedError()
    {
        // 仕様: initialize より前のリクエストには -32002 (ServerNotInitialized) を返すべき。
        using var s = new LspSession();

        var root = s.RequestRaw("textDocument/documentSymbol", new { textDocument = new { uri = "file:///Assets/A.cs" } });

        Assert.True(root.TryGetProperty("error", out var error), "Request before initialize must return an error.");
        Assert.Equal(ServerNotInitialized, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void RequestAfterShutdown_ReturnsInvalidRequestError()
    {
        // 仕様: shutdown 後のリクエストには -32600 (InvalidRequest) を返すべき。
        using var s = new LspSession();
        s.Initialize();
        s.Request("shutdown", new { });

        var root = s.RequestRaw("textDocument/documentSymbol", new { textDocument = new { uri = "file:///Assets/A.cs" } });

        Assert.True(root.TryGetProperty("error", out var error), "Request after shutdown must return an error.");
        Assert.Equal(InvalidRequest, error.GetProperty("code").GetInt32());
    }
}
