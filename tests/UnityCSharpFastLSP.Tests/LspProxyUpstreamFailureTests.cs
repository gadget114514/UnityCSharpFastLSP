using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Server;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

/// <summary>upstream を起動できないとき、upstream 宛てのリクエストに RequestFailed を返すこと。</summary>
public sealed class LspProxyUpstreamFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fastlsp-proxyfail-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryStream _output = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private LspProxy CreateProxy(Func<UpstreamAttempt, string?, Process> starter) =>
        new(_output, upstreamExecutablePath: null, UpstreamKind.CSharpLs, workspacePath: null,
            capabilitiesCache: new UpstreamCapabilitiesCache(_dir), upstreamStarter: starter);

    private static byte[] Msg(string json) => Encoding.UTF8.GetBytes(json);

    private List<JsonObject> Responses()
    {
        byte[] snapshot;
        lock (_output)
        {
            snapshot = _output.ToArray();
        }

        var list = new List<JsonObject>();
        using var read = new MemoryStream(snapshot);
        while (LspMessageReader.TryReadMessage(read, out byte[]? message) && message != null)
        {
            if (JsonNode.Parse(message) is JsonObject obj) list.Add(obj);
        }
        return list;
    }

    private JsonObject? ResponseFor(int id) =>
        Responses().FirstOrDefault(r => r["id"] is JsonValue v && v.TryGetValue(out int i) && i == id);

    private JsonObject WaitForResponse(int id)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (ResponseFor(id) is JsonObject r) return r;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException($"No response for id {id}.");
    }

    private static void AssertRequestFailed(JsonObject response)
    {
        Assert.Equal(JsonRpcErrorCode.RequestFailed, (int)response["error"]!["code"]!);
        Assert.Contains("upstream", (string)response["error"]!["message"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueuedRequests_GetErrorWhenUpstreamFailsToStart()
    {
        using var release = new ManualResetEventSlim();
        using LspProxy proxy = CreateProxy((_, _) =>
        {
            release.Wait();
            throw new InvalidOperationException("boom");
        });

        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}"""));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","method":"initialized","params":{}}"""));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":2,"method":"textDocument/rename","params":{}}"""));

        // 起動中はまだ応答しない（キューに溜まっている）
        Assert.Null(ResponseFor(2));

        release.Set();

        AssertRequestFailed(WaitForResponse(2));
        // initialize は FastLSP が応答済み。エラーで上書きしない
        JsonObject init = ResponseFor(1)!;
        Assert.Null(init["error"]);
        Assert.Single(Responses(), r => r["id"] is JsonValue v && v.TryGetValue(out int i) && i == 1);
    }

    [Fact]
    public void RequestsAfterStartFailure_GetErrorImmediately()
    {
        using LspProxy proxy = CreateProxy((_, _) => throw new InvalidOperationException("boom"));

        var sw = Stopwatch.StartNew();
        while (proxy.UpstreamError == null && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        Assert.NotNull(proxy.UpstreamError);

        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}"""));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":3,"method":"textDocument/codeAction","params":{}}"""));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","method":"workspace/didChangeConfiguration","params":{}}"""));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":4,"method":"shutdown"}"""));

        AssertRequestFailed(ResponseFor(3)!);
        Assert.Contains("boom", (string)ResponseFor(3)!["error"]!["message"]!);

        // shutdown は FastLSP の応答 1 件だけ、通知には何も返さない
        List<JsonObject> responses = Responses();
        Assert.Single(responses, r => r["id"] is JsonValue v && v.TryGetValue(out int i) && i == 4);
        Assert.Null(ResponseFor(4)!["error"]);
        Assert.Equal(3, responses.Count);
    }
}
