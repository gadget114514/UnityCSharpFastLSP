using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Server;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

/// <summary>
/// unitylsp/* は upstream の advertise（capabilities.experimental.unitylsp.methods）で振り分ける:
/// advertise していれば upstream だけが答え、FastLSP 側の GUID 機能は完全に止まる。
/// advertise していない・upstream が起動しないときは FastLSP が答える。
/// </summary>
public sealed class LspProxyUnityLspRoutingTests : IDisposable
{
    private const string Guid32 = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// 最小の偽 upstream LSP。-Advertise のメソッドを experimental.unitylsp.methods で advertise し、
    /// -Supported のメソッドにだけ {"from":"upstream"} を返す（それ以外は MethodNotFound）。
    /// </summary>
    private const string FakeUpstreamScript = """
        param([string]$Advertise = '', [string]$Supported = '')
        $supportedList = $Supported -split ','
        $advertiseJson = (@($Advertise -split ',' | Where-Object { $_ -ne '' }) | ForEach-Object { '"' + $_ + '"' }) -join ','
        $in = [Console]::OpenStandardInput()
        $out = [Console]::OpenStandardOutput()
        function Read-Message {
            $header = New-Object System.Collections.Generic.List[byte]
            while ($true) {
                $b = $in.ReadByte()
                if ($b -lt 0) { return $null }
                $header.Add([byte]$b)
                $n = $header.Count
                if ($n -ge 4 -and $header[$n-4] -eq 13 -and $header[$n-3] -eq 10 -and $header[$n-2] -eq 13 -and $header[$n-1] -eq 10) { break }
            }
            $text = [Text.Encoding]::ASCII.GetString($header.ToArray())
            $len = [int]([regex]::Match($text, 'Content-Length:\s*(\d+)').Groups[1].Value)
            $buf = New-Object byte[] $len
            $read = 0
            while ($read -lt $len) {
                $r = $in.Read($buf, $read, $len - $read)
                if ($r -le 0) { return $null }
                $read += $r
            }
            return [Text.Encoding]::UTF8.GetString($buf)
        }
        function Send-Message([string]$json) {
            $body = [Text.Encoding]::UTF8.GetBytes($json)
            $head = [Text.Encoding]::ASCII.GetBytes("Content-Length: $($body.Length)`r`n`r`n")
            $out.Write($head, 0, $head.Length)
            $out.Write($body, 0, $body.Length)
            $out.Flush()
        }
        while ($true) {
            $raw = Read-Message
            if ($null -eq $raw) { break }
            $msg = $raw | ConvertFrom-Json
            if ($msg.method -eq 'exit') { break }
            if ($null -eq $msg.id) { continue }
            $id = $msg.id | ConvertTo-Json -Compress
            if ($msg.method -eq 'initialize') {
                Send-Message "{""jsonrpc"":""2.0"",""id"":$id,""result"":{""capabilities"":{""experimental"":{""unitylsp"":{""methods"":[$advertiseJson]}}}}}"
            } elseif ($msg.method -eq 'shutdown') {
                Send-Message "{""jsonrpc"":""2.0"",""id"":$id,""result"":null}"
            } elseif ($supportedList -contains $msg.method) {
                Send-Message "{""jsonrpc"":""2.0"",""id"":$id,""result"":{""from"":""upstream""}}"
            } else {
                Send-Message "{""jsonrpc"":""2.0"",""id"":$id,""error"":{""code"":-32601,""message"":""not found""}}"
            }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fastlsp-unitylsp-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryStream _output = new();

    public LspProxyUnityLspRoutingTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "fake-upstream.ps1"), FakeUpstreamScript);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Process StartFakeUpstream(string advertise, string supported)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(_dir, "fake-upstream.ps1")}\" -Advertise \"{advertise}\" -Supported \"{supported}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

    private LspProxy CreateProxy(Func<UpstreamAttempt, string?, Process> starter) =>
        new(_output, upstreamExecutablePath: null, UpstreamKind.CSharpLs, workspacePath: null,
            capabilitiesCache: new UpstreamCapabilitiesCache(Path.Combine(_dir, "cache")), upstreamStarter: starter);

    private static byte[] Msg(string json) => Encoding.UTF8.GetBytes(json);

    private JsonObject? ResponseFor(int id)
    {
        byte[] snapshot;
        lock (_output)
        {
            snapshot = _output.ToArray();
        }

        using var read = new MemoryStream(snapshot);
        while (LspMessageReader.TryReadMessage(read, out byte[]? message) && message != null)
        {
            if (JsonNode.Parse(message) is JsonObject obj &&
                obj["id"] is JsonValue v && v.TryGetValue(out int i) && i == id)
            {
                return obj;
            }
        }
        return null;
    }

    private JsonObject WaitForResponse(int id)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (ResponseFor(id) is JsonObject r) return r;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException($"No response for id {id}.");
    }

    /// <summary>initialize 後、upstream 宛てのリクエストに応答が来るまで待つ（＝ upstream が生きている）。</summary>
    private void InitializeAndWaitForUpstream(LspProxy proxy)
    {
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}"""));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","method":"initialized","params":{}}"""));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":2,"method":"textDocument/rename","params":{}}"""));
        WaitForResponse(2);
    }

    private static string GuidRequest(int id) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{LspServer.GuidDefinitionMethod}}}","params":{"guid":"{{{Guid32}}}"}}""";

    private static bool IsFromUpstream(JsonObject response) =>
        response["result"] is JsonObject result && (string?)result["from"] == "upstream";

    private static readonly string BothGuidMethods = LspServer.GuidDefinitionMethod + "," + LspServer.GuidReferencesMethod;

    [Fact]
    public void UnityLsp_AdvertisedByUpstream_UpstreamAnswersAndFastGuidFeatureStops()
    {
        if (!OperatingSystem.IsWindows()) return;
        using LspProxy proxy = CreateProxy((_, _) => StartFakeUpstream(BothGuidMethods, BothGuidMethods));
        InitializeAndWaitForUpstream(proxy);

        proxy.HandleClientMessage(Msg(GuidRequest(10)));

        Assert.True(IsFromUpstream(WaitForResponse(10)), ResponseFor(10)!.ToJsonString());
        Assert.False(proxy.FastGuidFeatureEnabled);
    }

    [Fact]
    public void UnityLsp_AdvertisedButFails_NoFallbackToFastLsp()
    {
        if (!OperatingSystem.IsWindows()) return;
        // advertise はするが実際には MethodNotFound を返す upstream
        using LspProxy proxy = CreateProxy((_, _) => StartFakeUpstream(BothGuidMethods, "none"));
        InitializeAndWaitForUpstream(proxy);

        proxy.HandleClientMessage(Msg(GuidRequest(10)));

        JsonObject response = WaitForResponse(10);
        Assert.Equal(JsonRpcErrorCode.MethodNotFound, (int)response["error"]!["code"]!);
        Assert.Equal("not found", (string)response["error"]!["message"]!); // upstream の応答がそのまま届く
        Assert.False(proxy.FastGuidFeatureEnabled);
    }

    [Fact]
    public void UnityLsp_NotAdvertised_FastLspAnswers()
    {
        if (!OperatingSystem.IsWindows()) return;
        // advertise しない upstream は、実装していても使わない
        using LspProxy proxy = CreateProxy((_, _) => StartFakeUpstream("", BothGuidMethods));
        InitializeAndWaitForUpstream(proxy);

        proxy.HandleClientMessage(Msg(GuidRequest(10)));

        JsonObject response = ResponseFor(10) ?? throw new Xunit.Sdk.XunitException("FastLSP should answer synchronously.");
        Assert.Null(response["error"]);
        Assert.False(IsFromUpstream(response));
        Assert.True(proxy.FastGuidFeatureEnabled);
    }

    [Fact]
    public void UnityLsp_CachedAdvertise_StopsFastGuidFeatureBeforeUpstreamStarts()
    {
        var cache = new UpstreamCapabilitiesCache(Path.Combine(_dir, "cache"));
        var advertised = new JsonObject
        {
            ["experimental"] = new JsonObject
            {
                ["unitylsp"] = new JsonObject
                {
                    ["methods"] = new JsonArray(LspServer.GuidDefinitionMethod, LspServer.GuidReferencesMethod),
                },
            },
        };
        cache.Save(UpstreamKind.CSharpLs, advertised, null);

        using var release = new ManualResetEventSlim();
        using LspProxy proxy = CreateProxy((_, _) =>
        {
            release.Wait();
            throw new InvalidOperationException("boom");
        });
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}"""));

        // upstream の起動を待たず、キャッシュの advertise で FastLSP 側は止まっている
        Assert.False(proxy.FastGuidFeatureEnabled);
        JsonObject init = WaitForResponse(1);
        JsonArray methods = init["result"]!["capabilities"]!["experimental"]!["unitylsp"]!["methods"]!.AsArray();
        Assert.Contains(LspServer.GuidReferencesMethod, methods.Select(m => (string)m!));

        // upstream が起動しなかったら FastLSP が受け持つ
        release.Set();
        var sw = Stopwatch.StartNew();
        while (!proxy.FastGuidFeatureEnabled && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        Assert.True(proxy.FastGuidFeatureEnabled);
        proxy.HandleClientMessage(Msg(GuidRequest(10)));
        Assert.Null(WaitForResponse(10)["error"]);
    }

    [Fact]
    public void UnityLsp_UpstreamUnavailable_FastLspAnswersInsteadOfRequestFailed()
    {
        using LspProxy proxy = CreateProxy((_, _) => throw new InvalidOperationException("boom"));
        proxy.HandleClientMessage(Msg("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}"""));

        proxy.HandleClientMessage(Msg(GuidRequest(10)));

        JsonObject response = WaitForResponse(10);
        Assert.Null(response["error"]);
        Assert.True(LspProxy.IsFastPath(LspServer.GuidReferencesMethod));
    }

    [Fact]
    public void FastLsp_AdvertisesUnityLspMethods_AndMergeUnionsUpstreamMethods()
    {
        var fast = JsonNode.Parse("""{"experimental":{"unitylsp":{"methods":["unitylsp/guidDefinition"]}}}""")!.AsObject();
        var upstream = JsonNode.Parse("""{"experimental":{"unitylsp":{"methods":["unitylsp/other"]}},"renameProvider":true}""")!.AsObject();

        UpstreamCapabilitiesCache.MergeInto(fast, upstream);

        Assert.Equal(
            ["unitylsp/guidDefinition", "unitylsp/other"],
            UpstreamCapabilitiesCache.GetUnityLspMethods(fast).Order(StringComparer.Ordinal).ToArray());
        Assert.True((bool)fast["renameProvider"]!);
    }
}
